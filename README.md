# SnappedCut for .NET

A dependency-free client for the [Snapped Cut API](https://snappedcut.com/api/openapi.json):
`HttpClient` and `System.Text.Json`, nothing else. Targets .NET 8 and later.

## Install

```sh
dotnet add package SnappedCut
```

## Quick start

```csharp
using SnappedCut;

using var snapcut = new SnappedCutClient(Environment.GetEnvironmentVariable("SNAPCUT_KEY")!);
var clip = await snapcut.UploadFileAsync("clip.mp4", "video/mp4");
var design = new { width = 1080, height = 1920, clips = new[] { new { type = "video", asset = clip.Id } } };
var created = await snapcut.RenderDesignAsync(design, new
{
    outputs = new object[] { new { format = "mp4", preset = "landscape" }, new { format = "webm", preset = "vertical" } },
}, idempotencyKey: "launch-video-1");
var job = await snapcut.WaitForJobAsync(created.Job.Id);
for (var i = 0; i < job.Outputs.Count; i++)
    await File.WriteAllBytesAsync($"out-{i}.{job.Outputs[i].Format}", await snapcut.DownloadOutputAsync(job.Id, i));
```

Already have an exported project bundle? Send the zip as it is:

```csharp
var queued = await snapcut.RenderAsync(await File.ReadAllBytesAsync("project.zip"), idempotencyKey: "launch-video-2");
var done = await snapcut.WaitForJobAsync(queued.Job.Id);
```

Presets: `source`, `vertical`, `square`, `portrait`, `landscape`, `hd720`, `uhd2160`.

Refusals throw `SnappedCutException` with `Status`, `Message`, `RetryAfterMs`, `Code`,
and `IsOutOfCredits` (402) / `IsRateLimited` (429) to branch on.

## Verifying webhooks

Verify against the **raw** request body, before parsing it:

```csharp
var raw = await new StreamReader(Request.Body).ReadToEndAsync();
var header = Request.Headers["X-Snappedcut-Signature"].ToString();
if (!SnappedCutClient.VerifyWebhook(webhookSecret, raw, header)) return Results.Unauthorized();
```

It returns false when the signature does not match, is malformed, or is more than five
minutes old.

## Tests

```sh
dotnet test
```
