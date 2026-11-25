# Changelog

## 0.2.0

The first published .NET release, at the same API surface as the other 0.2.0 SDKs.

- `RenderAsync(bundle)`: queue a render of a project bundle (bytes or a stream), chunked
  and resumable over 64 MiB; returns what the server priced on in `Priced`.
- JSON jobs: `CreateJobAsync` with an `Idempotency-Key`, `CreateBatchAsync`, `GetJobAsync`,
  `ListJobsAsync`, `DownloadOutputAsync(id, index)`, `WaitForJobAsync`.
- Designs: `CompileDesignAsync`, `RenderDesignAsync`.
- Direct uploads: `UploadAsync` / `UploadFileAsync` (chunked, the API key never sent to the
  upload URL), `GetAssetAsync`, `DeleteAssetAsync`.
- `OperationsAsync`, `AccountAsync`, `UsageAsync` (with `groupBy`), `DeleteEndUserAsync`,
  `CreateEmbedTokenAsync`.
- Storage destinations: list, create, get, update, delete, test, presign.
- Webhook endpoints: list, create, get, update, delete, rotate secret, deliveries, test.
- `SnappedCutClient.VerifyWebhook` for the `X-Snappedcut-Signature` header.
- `SnappedCutException` with `Status`, `RetryAfterMs`, `Code`, `Expected`,
  `IsOutOfCredits` and `IsRateLimited`.
