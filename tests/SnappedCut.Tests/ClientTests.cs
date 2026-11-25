using System.Net;
using System.Text;
using SnappedCut;
using Xunit;

public class ClientTests
{
    /// <summary>Answers each request from a script and records what was sent, bodies included.</summary>
    sealed class Fake : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, byte[] Body)> Sent = new();
        readonly Queue<(int Status, string Body)> _answers;
        public Fake(params (int, string)[] answers) => _answers = new(answers);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
            Sent.Add((request, body));
            var (status, text) = _answers.Dequeue();
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    static SnappedCutClient Client(Fake fake) => new("sk_snapcut_test", "https://api.test", handler: fake) { PollDelay = TimeSpan.FromMilliseconds(1) };

    const string Secret = "whsec_snapcut_test";
    const string Body = "{\"id\":\"evt_1\",\"type\":\"job.succeeded\"}";
    const string Header = "t=1760000000000,v1=54c2adf2a351242de19b156794d65c56bebf9fc56cadac990c56b979a7a61ab2";

    [Fact]
    public void WebhookVector()
    {
        Assert.True(SnappedCutClient.VerifyWebhook(Secret, Body, Header, 1760000001000));
        Assert.False(SnappedCutClient.VerifyWebhook(Secret, Body, Header, 1760000400000));
        Assert.False(SnappedCutClient.VerifyWebhook(Secret, Body + " ", Header, 1760000001000));
        Assert.False(SnappedCutClient.VerifyWebhook(Secret, Body, "v1=" + Header.Split("v1=")[1], 1760000001000));
        Assert.False(SnappedCutClient.VerifyWebhook(Secret, Body, Header.ToUpperInvariant().Replace("T=", "t=").Replace("V1=", "v1="), 1760000001000));
    }

    [Fact]
    public async Task CreateJobSendsKeyBearerAndJson()
    {
        var fake = new Fake((202, "{\"job\":{\"id\":\"job_1\",\"kind\":\"cloud-render\",\"state\":\"queued\",\"seconds\":4,\"createdAt\":1,\"outputs\":[],\"brandNew\":true},\"charged\":4,\"price\":1,\"credits\":96}"));
        var created = await Client(fake).CreateJobAsync(new { tool = "compress", inputs = new[] { "ast_1" } }, "idem-1");

        var (request, body) = fake.Sent.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.test/api/jobs", request.RequestUri!.ToString());
        Assert.Equal("idem-1", request.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("Bearer sk_snapcut_test", request.Headers.Authorization!.ToString());
        Assert.Equal("snappedcut-dotnet/0.2.0", string.Join(" ", request.Headers.GetValues("User-Agent")));
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("{\"tool\":\"compress\",\"inputs\":[\"ast_1\"]}", Encoding.UTF8.GetString(body));
        Assert.Equal("job_1", created.Job.Id);
        Assert.Equal(4, created.Charged);
        Assert.Equal(96, created.Credits);
    }

    const string QueuedBundle = "{\"job\":{\"id\":\"job_2\",\"kind\":\"cloud-render\",\"state\":\"queued\",\"seconds\":4,\"createdAt\":1,\"outputs\":[]},\"charged\":4,\"credits\":96,\"priced\":{\"seconds\":4,\"project\":\"p\",\"layers\":1}}";

    [Fact]
    public async Task RenderSendsTheBundleAsTheBody()
    {
        var fake = new Fake((202, QueuedBundle));
        var created = await Client(fake).RenderAsync(Encoding.ASCII.GetBytes("PK-zip"), idempotencyKey: "idem-2");

        var (request, body) = fake.Sent.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.test/api/jobs?kind=cloud-render", request.RequestUri!.ToString());
        Assert.Equal("application/zip", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("idem-2", request.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("Bearer sk_snapcut_test", request.Headers.Authorization!.ToString());
        Assert.Equal("PK-zip", Encoding.ASCII.GetString(body));
        Assert.Equal("job_2", created.Job.Id);
        Assert.Equal(4, created.Priced!["seconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task RenderChunksAndResumesFromTheServersCount()
    {
        var fake = new Fake(
            (200, "{\"upload\":\"up_1\",\"received\":4}"),
            (409, "{\"error\":\"Offset mismatch.\",\"expected\":2}"),
            (200, "{\"upload\":\"up_1\",\"received\":6}"),
            (202, QueuedBundle));
        var client = Client(fake);
        client.RenderChunkBytes = 4;
        var created = await client.RenderAsync(new MemoryStream(Encoding.ASCII.GetBytes("0123456789")), idempotencyKey: "idem-3");

        var urls = fake.Sent.Select(s => s.Request.RequestUri!.PathAndQuery).ToList();
        Assert.Equal(new[]
        {
            "/api/jobs?kind=cloud-render&offset=0",
            "/api/jobs?kind=cloud-render&offset=4&upload=up_1",
            "/api/jobs?kind=cloud-render&offset=2&upload=up_1",
            "/api/jobs?kind=cloud-render&offset=6&upload=up_1&final=1",
        }, urls);
        Assert.Equal(new[] { "0123", "4567", "2345", "6789" }, fake.Sent.Select(s => Encoding.ASCII.GetString(s.Body)));
        // The key goes on the request that queues the job, and only that one.
        Assert.Equal(new[] { false, false, false, true }, fake.Sent.Select(s => s.Request.Headers.Contains("Idempotency-Key")));
        Assert.Equal("job_2", created.Job.Id);
    }

    [Fact]
    public async Task OutOfCreditsIsTheSdkError()
    {
        var fake = new Fake((402, "{\"error\":\"Not enough credits.\",\"retryAfterMs\":0}"));
        var error = await Assert.ThrowsAsync<SnappedCutException>(() => Client(fake).CreateJobAsync(new { tool = "compress" }));
        Assert.True(error.IsOutOfCredits);
        Assert.False(error.IsRateLimited);
        Assert.Equal(402, error.Status);
        Assert.Equal("Not enough credits.", error.Message);
        Assert.Equal(0, error.RetryAfterMs);
    }

    [Fact]
    public async Task UploadChunksWithoutTheKeyThenConfirms()
    {
        var fake = new Fake(
            (201, "{\"assetId\":\"ast_1\",\"upload\":{\"url\":\"https://up.test/api/uploads/ast_1?token=tok\",\"maxChunkBytes\":6}}"),
            (200, "{\"received\":6}"),
            (200, "{\"received\":10}"),
            (200, "{\"asset\":{\"id\":\"ast_1\",\"name\":\"clip.webm\",\"contentType\":\"video/webm\",\"bytes\":10,\"confirmed\":true}}"));
        var data = Encoding.ASCII.GetBytes("0123456789");
        var asset = await Client(fake).UploadAsync(data, "video/webm", "clip.webm");

        Assert.Equal(4, fake.Sent.Count);
        var puts = fake.Sent.Where(s => s.Request.Method == HttpMethod.Put).ToList();
        Assert.Equal(2, puts.Count);
        Assert.Equal("https://up.test/api/uploads/ast_1?token=tok&offset=0", puts[0].Request.RequestUri!.ToString());
        Assert.Equal("https://up.test/api/uploads/ast_1?token=tok&offset=6", puts[1].Request.RequestUri!.ToString());
        Assert.Equal("012345", Encoding.ASCII.GetString(puts[0].Body));
        Assert.Equal("6789", Encoding.ASCII.GetString(puts[1].Body));
        Assert.All(puts, p => Assert.Null(p.Request.Headers.Authorization));
        Assert.Equal("https://api.test/api/uploads/ast_1/confirm", fake.Sent[3].Request.RequestUri!.ToString());
        Assert.True(asset.Confirmed);
    }

    const string Queued = "{\"id\":\"job_1\",\"kind\":\"cloud-render\",\"state\":\"running\",\"seconds\":4,\"createdAt\":1,\"outputs\":[]}";

    [Fact]
    public async Task WaitReturnsOnDone()
    {
        var fake = new Fake((200, Queued), (200, Queued.Replace("running", "done")));
        var job = await Client(fake).WaitForJobAsync("job_1");
        Assert.Equal("done", job.State);
        Assert.Equal(2, fake.Sent.Count);
    }

    [Fact]
    public async Task WaitThrowsOnFailed()
    {
        var fake = new Fake((200, Queued), (200, Queued.Replace("\"running\"", "\"failed\",\"error\":\"Decode failed.\"")));
        var error = await Assert.ThrowsAsync<SnappedCutException>(() => Client(fake).WaitForJobAsync("job_1"));
        Assert.Equal("Decode failed.", error.Message);
    }

    [Fact]
    public async Task DestinationTest422IsAnAnswer()
    {
        var fake = new Fake((422, "{\"ok\":false,\"key\":\"k\",\"error\":\"Access denied\"}"));
        var result = await Client(fake).TestDestinationAsync("dst_1");
        Assert.False(result["ok"]!.GetValue<bool>());
    }
}
