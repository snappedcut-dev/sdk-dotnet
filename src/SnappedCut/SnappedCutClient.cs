using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SnappedCut;

/// <summary>
/// The Snapped Cut API client. HttpClient and System.Text.Json, nothing else.
/// Requests are any object System.Text.Json can serialise (anonymous objects, dictionaries,
/// JsonObject); property names are sent camelCase and nulls are left out.
/// </summary>
public sealed class SnappedCutClient : IDisposable
{
    public const string Version = "0.2.0";
    const string UserAgent = "snappedcut-dotnet/" + Version;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    readonly HttpClient _http;
    readonly string _key;
    readonly string _base;

    /// <summary>First wait between polls in <see cref="WaitForJobAsync"/>; grows ×1.5, capped at 15 s.</summary>
    public TimeSpan PollDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Largest bundle sent in one request by <see cref="RenderAsync(Stream, string, string?, string?, CancellationToken)"/>.
    /// Cloudflare refuses a proxied body over 100 MB, so 64 MiB leaves room for headers.
    /// </summary>
    public long RenderChunkBytes { get; set; } = 64L * 1024 * 1024;

    /// <param name="apiKey">An API key from the account page, <c>sk_snapcut_…</c>.</param>
    /// <param name="baseUrl">Defaults to https://snappedcut.com.</param>
    /// <param name="timeout">Per-request timeout; HttpClient's default when omitted.</param>
    /// <param name="handler">The transport. Inject one in tests.</param>
    public SnappedCutClient(string apiKey, string? baseUrl = null, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        _key = apiKey;
        _base = (baseUrl ?? "https://snappedcut.com").TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        if (timeout is { } t) _http.Timeout = t;
    }

    public void Dispose() => _http.Dispose();

    // ---- transport ----------------------------------------------------------

    HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, _base + path);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        return request;
    }

    static async Task<SnappedCutException> Failure(HttpResponseMessage response, CancellationToken ct)
    {
        ErrorBody? body = null;
        try { body = JsonSerializer.Deserialize<ErrorBody>(await response.Content.ReadAsStringAsync(ct), Json); }
        catch (JsonException) { }
        var status = (int)response.StatusCode;
        return new SnappedCutException(body?.Error ?? $"Snapped Cut said {status}.", status, (long)(body?.RetryAfterMs ?? 0), body?.Code, (long)(body?.Expected ?? 0));
    }

    async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using (response) throw await Failure(response, ct).ConfigureAwait(false);
        }
        return response;
    }

    async Task<JsonObject> Call(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await Send(Request(method, path, body), ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    async Task<T> Call<T>(HttpMethod method, string path, object? body, CancellationToken ct, string? field = null)
    {
        var json = await Call(method, path, body, ct).ConfigureAwait(false);
        JsonNode? node = field is null ? json : json[field];
        return node.Deserialize<T>(Json) ?? throw new SnappedCutException($"Snapped Cut answered {path} without {field ?? "a body"}.", 0);
    }

    static string E(string s) => Uri.EscapeDataString(s);

    static JsonObject ToObject(object? value) =>
        value is null ? new JsonObject() : JsonSerializer.SerializeToNode(value, Json) as JsonObject
            ?? throw new ArgumentException("Expected an object.", nameof(value));

    // ---- jobs ---------------------------------------------------------------

    /// <summary>
    /// Queues a render of an exported project bundle (a zip), sent as the request body.
    /// There is no seconds parameter: the server prices on the bundle's own project.json,
    /// and says what it priced on in <see cref="Created.Priced"/>. A bundle over
    /// <see cref="RenderChunkBytes"/> goes up in chunks and resumes from the server's count.
    /// </summary>
    public Task<Created> RenderAsync(byte[] bundle, string kind = "cloud-render", string? idempotencyKey = null, string? priority = null, CancellationToken ct = default) =>
        RenderAsync(new MemoryStream(bundle, writable: false), kind, idempotencyKey, priority, ct);

    /// <inheritdoc cref="RenderAsync(byte[], string, string?, string?, CancellationToken)"/>
    public async Task<Created> RenderAsync(Stream bundle, string kind = "cloud-render", string? idempotencyKey = null, string? priority = null, CancellationToken ct = default)
    {
        if (!bundle.CanSeek)
        {
            // shortcut: an unseekable stream is buffered whole so a chunk can be resent; pass a FileStream for big bundles.
            var copy = new MemoryStream();
            await bundle.CopyToAsync(copy, ct).ConfigureAwait(false);
            copy.Position = 0;
            bundle = copy;
        }
        long start = bundle.Position, size = bundle.Length - start;
        var lane = priority is null ? "" : $"&priority={E(priority)}";

        if (size <= RenderChunkBytes)
            return (await PostBundle($"/api/jobs?kind={E(kind)}{lane}", bundle, start, size, idempotencyKey, ct).ConfigureAwait(false)).Deserialize<Created>(Json)!;

        string? upload = null;
        long offset = 0;
        var attempts = 0;
        while (offset < size)
        {
            var to = Math.Min(size, offset + RenderChunkBytes);
            var last = to == size;
            var query = $"/api/jobs?kind={E(kind)}&offset={offset}{lane}" + (upload is null ? "" : $"&upload={E(upload)}") + (last ? "&final=1" : "");
            try
            {
                var body = await PostBundle(query, bundle, start + offset, to - offset, last ? idempotencyKey : null, ct).ConfigureAwait(false);
                if (last) return body.Deserialize<Created>(Json)!;
                // The server's count, not ours: it is the one with the file.
                upload = body["upload"]!.GetValue<string>();
                offset = body["received"]!.GetValue<long>();
                attempts = 0;
            }
            catch (Exception error) when (error is SnappedCutException or HttpRequestException)
            {
                if (++attempts > 3) throw;
                // Where the server says its copy ends. Believe it over our own count.
                if (error is SnappedCutException { Status: 409 } behind) { offset = behind.Expected; continue; }
                // A refusal is a refusal; only a lost connection or a server fault is worth sending again.
                if (error is SnappedCutException { Status: < 500 }) throw;
            }
        }
        throw new SnappedCutException("That upload ended without queueing a job.", 0);
    }

    async Task<JsonObject> PostBundle(string path, Stream bundle, long from, long count, string? idempotencyKey, CancellationToken ct)
    {
        bundle.Position = from;
        var buffer = new byte[count];
        await bundle.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        var message = Request(HttpMethod.Post, path);
        message.Content = new ByteArrayContent(buffer);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        if (idempotencyKey is not null) message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await Send(message, ct).ConfigureAwait(false);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject ?? new JsonObject();
    }

    /// <summary>POST /api/jobs. An Idempotency-Key makes a retry return the first job instead of a second charge.</summary>
    public async Task<Created> CreateJobAsync(object request, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var body = ToObject(request);
        var key = idempotencyKey ?? body["idempotencyKey"]?.GetValue<string>();
        var message = Request(HttpMethod.Post, "/api/jobs", body);
        if (key is not null) message.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        using var response = await Send(message, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Created>(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), Json)!;
    }

    /// <summary>Up to 100 jobs; <paramref name="defaults"/> is merged under each. Each item is answered on its own.</summary>
    public Task<JsonObject> CreateBatchAsync(IEnumerable<object> jobs, object? defaults = null, CancellationToken ct = default) =>
        Call(HttpMethod.Post, "/api/jobs/batch", new { jobs, defaults }, ct);

    public Task<Job> GetJobAsync(string id, CancellationToken ct = default) =>
        Call<Job>(HttpMethod.Get, $"/api/jobs/{E(id)}", null, ct);

    public Task<List<Job>> ListJobsAsync(CancellationToken ct = default) =>
        Call<List<Job>>(HttpMethod.Get, "/api/jobs", null, ct, "jobs");

    /// <summary>The rendered file. Throws if the job is not done.</summary>
    public async Task<byte[]> DownloadOutputAsync(string id, int index = 0, CancellationToken ct = default)
    {
        using var response = await Send(Request(HttpMethod.Get, $"/api/jobs/{E(id)}/output" + (index > 0 ? $"?index={index}" : "")), ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Polls until the job is done (returns it) or failed (throws), backing off from <see cref="PollDelay"/> to 15 s.</summary>
    public async Task<Job> WaitForJobAsync(string id, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(30));
        var wait = PollDelay;
        for (;;)
        {
            var job = await GetJobAsync(id, ct).ConfigureAwait(false);
            if (job.State == "done") return job;
            if (job.State == "failed") throw new SnappedCutException(job.Error ?? "The render failed.", 500);
            if (DateTime.UtcNow > deadline) throw new SnappedCutException($"Job {id} is still {job.State}.", 504);
            await Task.Delay(wait, ct).ConfigureAwait(false);
            wait = TimeSpan.FromMilliseconds(Math.Min(wait.TotalMilliseconds * 1.5, 15_000));
        }
    }

    // ---- designs ------------------------------------------------------------

    /// <summary>Compiles a design to the editor's project.json without rendering it.</summary>
    public Task<JsonObject> CompileDesignAsync(object design, object? vars = null, CancellationToken ct = default) =>
        Call(HttpMethod.Post, "/api/designs", new { design, vars }, ct);

    /// <summary>POST /api/jobs with <paramref name="design"/> merged into <paramref name="options"/>.</summary>
    public Task<Created> RenderDesignAsync(object design, object? options = null, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var body = ToObject(options);
        body["design"] = JsonSerializer.SerializeToNode(design, Json);
        return CreateJobAsync(body, idempotencyKey, ct);
    }

    // ---- uploads ------------------------------------------------------------

    public Task<Asset> UploadAsync(byte[] data, string contentType, string? name = null, CancellationToken ct = default) =>
        UploadAsync(new MemoryStream(data, writable: false), contentType, name, ct);

    /// <summary>Uploads a file from disk, streamed one chunk at a time.</summary>
    public async Task<Asset> UploadFileAsync(string path, string contentType, string? name = null, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(path);
        return await UploadAsync(file, contentType, name ?? Path.GetFileName(path), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// POST /api/uploads, PUT the bytes in chunks of at most maxChunkBytes, then confirm.
    /// Media uploaded once is used by id in designs, tools and jobs.
    /// </summary>
    public async Task<Asset> UploadAsync(Stream data, string contentType, string? name = null, CancellationToken ct = default)
    {
        var ticket = await Call<UploadTicket>(HttpMethod.Post, "/api/uploads", new { name, contentType }, ct).ConfigureAwait(false);
        long total = data.CanSeek ? data.Length - data.Position : -1;
        long step = Math.Max(1, ticket.Upload.MaxChunkBytes);
        var buffer = new byte[total >= 0 ? Math.Max(1, Math.Min(step, total)) : step];
        long offset = 0;
        for (;;)
        {
            var n = await data.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            // shortcut: an unseekable stream whose length is an exact multiple of the chunk ends with one empty PUT; pass a seekable stream to avoid it.
            var last = n < buffer.Length || (total >= 0 && offset + n >= total);
            // The upload URL carries its own permission; the API key is not sent to it.
            using var put = new HttpRequestMessage(HttpMethod.Put, $"{ticket.Upload.Url}&offset={offset}")
            {
                Content = new ByteArrayContent(buffer, 0, n),
            };
            put.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            put.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using (var response = await _http.SendAsync(put, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw await Failure(response, ct).ConfigureAwait(false);
            }
            offset += n;
            if (last) break;
        }
        return await Call<Asset>(HttpMethod.Post, $"/api/uploads/{E(ticket.AssetId)}/confirm", new { }, ct, "asset").ConfigureAwait(false);
    }

    public Task<Asset> GetAssetAsync(string id, CancellationToken ct = default) =>
        Call<Asset>(HttpMethod.Get, $"/api/uploads/{E(id)}", null, ct, "asset");

    public Task DeleteAssetAsync(string id, CancellationToken ct = default) =>
        Call(HttpMethod.Delete, $"/api/uploads/{E(id)}", null, ct);

    // ---- meta, usage, embed ---------------------------------------------------

    /// <summary>Every operation, the render parameters, and the presets.</summary>
    public Task<JsonObject> OperationsAsync(CancellationToken ct = default) => Call(HttpMethod.Get, "/api/operations", null, ct);

    /// <summary>The account behind the key: who, and how many credits are left.</summary>
    public Task<JsonObject> AccountAsync(CancellationToken ct = default) => Call(HttpMethod.Get, "/api/account", null, ct);

    public Task<JsonObject> UsageAsync(long? from = null, long? to = null, string? endUserId = null, string? groupBy = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (from is { } f) query.Add($"from={f}");
        if (to is { } t) query.Add($"to={t}");
        if (endUserId is not null) query.Add($"endUserId={E(endUserId)}");
        if (groupBy is not null) query.Add($"groupBy={E(groupBy)}");
        return Call(HttpMethod.Get, "/api/usage" + (query.Count > 0 ? "?" + string.Join("&", query) : ""), null, ct);
    }

    /// <summary>Deletes one end user's renders and uploads, and their id from everything kept.</summary>
    public Task<JsonObject> DeleteEndUserAsync(string endUserId, CancellationToken ct = default) =>
        Call(HttpMethod.Delete, $"/api/usage?endUserId={E(endUserId)}", null, ct);

    /// <summary>Mints an embed session token for one of your end users. Server side only.</summary>
    public Task<JsonObject> CreateEmbedTokenAsync(string pk, string endUserId, int? maxCredits = null, int? ttlSeconds = null,
        string? origin = null, object? features = null, CancellationToken ct = default) =>
        Call(HttpMethod.Post, "/api/embed/tokens", new { pk, endUserId, maxCredits, ttlSeconds, origin, features }, ct);

    // ---- destinations -------------------------------------------------------

    public Task<List<StorageDestination>> ListDestinationsAsync(CancellationToken ct = default) =>
        Call<List<StorageDestination>>(HttpMethod.Get, "/api/destinations", null, ct, "destinations");

    /// <summary>input: name, endpoint, region?, bucket, prefix?, accessKeyId, secretAccessKey, pathStyle?</summary>
    public Task<StorageDestination> CreateDestinationAsync(object input, CancellationToken ct = default) =>
        Call<StorageDestination>(HttpMethod.Post, "/api/destinations", input, ct, "destination");

    public Task<StorageDestination> GetDestinationAsync(string id, CancellationToken ct = default) =>
        Call<StorageDestination>(HttpMethod.Get, $"/api/destinations/{E(id)}", null, ct, "destination");

    public Task<StorageDestination> UpdateDestinationAsync(string id, object patch, CancellationToken ct = default) =>
        Call<StorageDestination>(HttpMethod.Patch, $"/api/destinations/{E(id)}", patch, ct, "destination");

    public Task DeleteDestinationAsync(string id, CancellationToken ct = default) =>
        Call(HttpMethod.Delete, $"/api/destinations/{E(id)}", null, ct);

    /// <summary>Writes a test object. A failed write (422) is an answer — <c>{ok:false,…}</c> — not an exception.</summary>
    public async Task<JsonObject> TestDestinationAsync(string id, CancellationToken ct = default)
    {
        using var response = await _http.SendAsync(Request(HttpMethod.Post, $"/api/destinations/{E(id)}/test"), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 422) throw await Failure(response, ct).ConfigureAwait(false);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject ?? new JsonObject();
    }

    public Task<JsonObject> PresignDestinationAsync(string id, string key, int? expiresIn = null, CancellationToken ct = default) =>
        Call(HttpMethod.Post, $"/api/destinations/{E(id)}/presign", new { key, expiresIn }, ct);

    // ---- webhooks -----------------------------------------------------------

    public Task<List<WebhookEndpoint>> ListWebhooksAsync(CancellationToken ct = default) =>
        Call<List<WebhookEndpoint>>(HttpMethod.Get, "/api/webhooks", null, ct, "webhooks");

    /// <summary>The secret comes back this once.</summary>
    public Task<CreatedWebhook> CreateWebhookAsync(string url, IEnumerable<string>? events = null, CancellationToken ct = default) =>
        Call<CreatedWebhook>(HttpMethod.Post, "/api/webhooks", new { url, events }, ct);

    public Task<WebhookEndpoint> GetWebhookAsync(string id, CancellationToken ct = default) =>
        Call<WebhookEndpoint>(HttpMethod.Get, $"/api/webhooks/{E(id)}", null, ct, "webhook");

    public Task<WebhookEndpoint> UpdateWebhookAsync(string id, string? url = null, IEnumerable<string>? events = null, bool? enabled = null, CancellationToken ct = default) =>
        Call<WebhookEndpoint>(HttpMethod.Patch, $"/api/webhooks/{E(id)}", new { url, events, enabled }, ct, "webhook");

    public Task DeleteWebhookAsync(string id, CancellationToken ct = default) =>
        Call(HttpMethod.Delete, $"/api/webhooks/{E(id)}", null, ct);

    /// <summary>Returns the new secret.</summary>
    public Task<string> RotateWebhookSecretAsync(string id, CancellationToken ct = default) =>
        Call<string>(HttpMethod.Post, $"/api/webhooks/{E(id)}/rotate", new { }, ct, "secret");

    public Task<JsonArray> ListWebhookDeliveriesAsync(string id, int limit = 50, CancellationToken ct = default) =>
        Call<JsonArray>(HttpMethod.Get, $"/api/webhooks/{E(id)}/deliveries?limit={limit}", null, ct, "deliveries");

    /// <summary>Sends a test event; returns <c>{eventId}</c>.</summary>
    public Task<JsonObject> TestWebhookAsync(string id, CancellationToken ct = default) =>
        Call(HttpMethod.Post, $"/api/webhooks/{E(id)}/test", new { }, ct);

    /// <summary>
    /// Checks an <c>X-Snappedcut-Signature</c> header (<c>t=&lt;epoch ms&gt;,v1=&lt;hex&gt;</c>) against the raw body.
    /// False when t is missing or more than 5 minutes from now, or v1 is malformed or does not match.
    /// </summary>
    public static bool VerifyWebhook(string secret, string rawBody, string? signatureHeader, long? nowMs = null)
    {
        string? t = null, v1 = null;
        foreach (var part in (signatureHeader ?? "").Split(','))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var (k, v) = (part[..eq].Trim(), part[(eq + 1)..].Trim());
            if (k == "t") t = v; else if (k == "v1") v1 = v;
        }
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!long.TryParse(t, out var ts) || Math.Abs(now - ts) > 300_000 || v1 is null || !Hex64.IsMatch(v1)) return false;
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{rawBody}"));
        var expected = Encoding.ASCII.GetBytes(Convert.ToHexString(mac).ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(v1));
    }
}
