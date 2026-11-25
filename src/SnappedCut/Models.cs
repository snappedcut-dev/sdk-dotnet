
namespace SnappedCut;

/// <summary>A refusal from the server. Branch on <see cref="IsOutOfCredits"/> and <see cref="IsRateLimited"/>.</summary>
public sealed class SnappedCutException : Exception
{
    public int Status { get; }
    public long RetryAfterMs { get; }
    public string? Code { get; }
    /// <summary>On a 409 from a chunked bundle upload: the byte the server's copy ends at.</summary>
    public long Expected { get; }

    public SnappedCutException(string message, int status, long retryAfterMs = 0, string? code = null, long expected = 0) : base(message)
    {
        Status = status;
        RetryAfterMs = retryAfterMs;
        Code = code;
        Expected = expected;
    }

    public bool IsOutOfCredits => Status == 402;
    public bool IsRateLimited => Status == 429;
}

public sealed class JobOutput
{
    public int Index { get; init; }
    public string? Key { get; init; }
    public string Format { get; init; } = "";
    public string? ContentType { get; init; }
    public long Bytes { get; init; }
    public string Url { get; init; } = "";
    public string? Location { get; init; }
    public bool? Delivered { get; init; }
    public string? DeliveryError { get; init; }
}

public sealed class Job
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string State { get; init; } = "";
    public string? Error { get; init; }
    public double Seconds { get; init; }
    public double CreatedAt { get; init; }
    public double UpdatedAt { get; init; }
    public bool? Ready { get; init; }
    public string? Priority { get; init; }
    public string? IdempotencyKey { get; init; }
    public long? Credits { get; init; }
    public List<JobOutput> Outputs { get; init; } = new();
}

public sealed class Created
{
    public Job Job { get; init; } = new();
    public long Charged { get; init; }
    public long Price { get; init; }
    public long? Credits { get; init; }
    public bool Idempotent { get; init; }
    /// <summary>From <c>RenderAsync</c>: what the bundle was priced on (seconds, project, layers).</summary>
    public System.Text.Json.Nodes.JsonObject? Priced { get; init; }
}

public sealed class Asset
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ContentType { get; init; } = "";
    public long? Bytes { get; init; }
    public string? Sha256 { get; init; }
    public double? DurationSeconds { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public bool? HasAudio { get; init; }
    public bool Confirmed { get; init; }
    public double CreatedAt { get; init; }
    public double ExpiresAt { get; init; }
}

public sealed class WebhookEndpoint
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public List<string> Events { get; init; } = new();
    public bool Enabled { get; init; }
    public double CreatedAt { get; init; }
}

/// <summary>A webhook endpoint as created: the secret comes back this once.</summary>
public sealed class CreatedWebhook
{
    public WebhookEndpoint Webhook { get; init; } = new();
    public string Secret { get; init; } = "";
}

public sealed class StorageDestination
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string? Region { get; init; }
    public string Bucket { get; init; } = "";
    public string? Prefix { get; init; }
    public bool PathStyle { get; init; }
    public string? AccessKeyId { get; init; }
    public double CreatedAt { get; init; }
    public double UpdatedAt { get; init; }
}

internal sealed class UploadTicket
{
    public string AssetId { get; init; } = "";
    public UploadTarget Upload { get; init; } = new();
}

internal sealed class UploadTarget
{
    public string Url { get; init; } = "";
    public long MaxChunkBytes { get; init; }
}

internal sealed class ErrorBody
{
    public string? Error { get; init; }
    public double? RetryAfterMs { get; init; }
    public string? Code { get; init; }
    public double? Expected { get; init; }
}
