# Compendium.Adapters.ZeroEntropy

An `IReranker` (from `Compendium.Abstractions.AI`) over **ZeroEntropy's rerank API**,
`POST https://api.zeroentropy.dev/v1/models/rerank`: the documents are scored against the query by a zerank model and
come back by descending relevance (0 to 1), each with its index in your input list. Search and indexing are not in this
package.

## Quick start

```csharp
services.AddCompendiumZeroEntropyReranker(builder.Configuration);   // section "ZeroEntropyReranker"
// or
services.AddCompendiumZeroEntropyReranker(o => o.ApiKey = builder.Configuration["ZeroEntropyReranker:ApiKey"]!);

var ranked = await reranker.RerankAsync(query, passages, new RerankOptions { TopN = 5 }, ct);
```

```jsonc
{
  "ZeroEntropyReranker": {
    "ApiKey": "...",
    "BaseUrl": "https://eu-api.zeroentropy.dev/v1",   // ZeroEntropyRerankerOptions.EuBaseUrl; the default is the US API
    "Model": "zerank-2",                               // or zerank-1, zerank-1-small
    "TimeoutSeconds": 10
  }
}
```

| Option | Default | |
|---|---|---|
| `BaseUrl` | `https://api.zeroentropy.dev/v1` | `/models/rerank` is appended. `EuBaseUrl` is `https://eu-api.zeroentropy.dev/v1`. |
| `ApiKey` | — | Required. Sent as `Authorization: Bearer <key>`, trimmed; never logged. |
| `Model` | `zerank-2` | The model when the call's `RerankOptions.Model` is unset. |
| `Latency` | unset | `latency`, sent only when set: `Fast` answers 429 rather than fall back to the slow mode past the fast rate limit; `Slow` always takes the slow mode. Unset, ZeroEntropy tries fast and falls back to slow. |
| `MaxBatchSize` | `1000` | Documents per request; more go in several requests, and are merged. |
| `MaxBatchBytes` | `5000000` | Bytes per request as ZeroEntropy counts them: per document, 150 plus the query and the document in UTF-8. 5 000 000 is ZeroEntropy's maximum on every tier. |
| `MaxConcurrentBatches` | `4` | Batches in flight at once; each counts against the key's rate limits (by default 1 000 requests and 500 000 bytes a minute, refreshed every 15 seconds; past the bytes, the slow mode, up to 5 000 000 a minute; past that, 429). |
| `TimeoutSeconds` | `10` | The call's budget, every batch included; past it, `AI.RerankFailed`. |
| `EnableLogging` | `false` | Logs the request bodies at Debug. The documents are in them; the key is not. |

## On the wire

Read from https://docs.zeroentropy.dev/api-reference/models/rerank and https://docs.zeroentropy.dev/models on
2026-10-31.

- Request: `{"model", "query", "documents": [...], "latency"?}`. `top_n` is not sent: every batch comes back whole, so
  that it can be checked, and `TopN` keeps the best after the merge.
- Answer: `{"results": [{"index", "relevance_score"}], "total_bytes", "total_tokens", "actual_latency_mode",
  "e2e_latency", "inference_latency"}`; only `results` is read. The score is between 0 and 1 and "depends only on the
  query and that document", so batches merge on it. A batch whose answer does not rank each of its documents exactly once
  (an index outside the batch, twice, or missing) fails the call. Equal scores keep the input order.
- A batch is closed when it holds `MaxBatchSize` documents or the next one would take it past `MaxBatchBytes`; a document
  past `MaxBatchBytes` on its own fails the call before any request.
- `ReturnDocuments` fills `Document` from your input (the API does not send the texts back). A blank query is
  `AI.InvalidQuery`, `TopN` below 1 `AI.InvalidRequest`, no documents an empty success with no request.
- Every failure is a `Result`: a 4xx or 5xx (ZeroEntropy's `detail` in the error — a string, or for a 422 each
  `loc: msg`), an unreadable answer, a transport error or the budget running out is `AI.RerankFailed`; the first failing
  batch stops the others. Only your own cancellation throws. No retry handler: on a rerank budget, fall back to the order
  you have.
- Options that cannot serve (a `BaseUrl` that is not absolute `http` / `https`, no key or model, a batch size, byte
  limit, concurrency or timeout below 1) fail the start: `AddCompendiumZeroEntropyReranker` validates them on start, and
  a reranker built by hand answers `AI.RerankFailed` before any request.

## License

MIT — same as Compendium itself.
