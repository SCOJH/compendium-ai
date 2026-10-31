# Compendium.Adapters.Cohere

An `IReranker` (from `Compendium.Abstractions.AI`) over **Cohere's Rerank API**, `POST https://api.cohere.com/v2/rerank`:
the documents are scored against the query by a Cohere rerank model and come back by descending relevance (0 to 1),
each with its index in your input list. Chat and embeddings are not in this package.

## Quick start

```csharp
services.AddCompendiumCohereReranker(builder.Configuration);   // section "CohereReranker"
// or
services.AddCompendiumCohereReranker(o => o.ApiKey = builder.Configuration["CohereReranker:ApiKey"]!);

var ranked = await reranker.RerankAsync(query, passages, new RerankOptions { TopN = 5 }, ct);
```

```jsonc
{
  "CohereReranker": {
    "ApiKey": "...",
    "Model": "rerank-v5.0-pro",       // or rerank-v5.0-fast, rerank-v4.0-pro, rerank-v4.0-fast, rerank-v3.5…
    "MaxTokensPerDoc": 8192,          // optional; unset, Cohere cuts at 4 096
    "TimeoutSeconds": 10
  }
}
```

| Option | Default | |
|---|---|---|
| `BaseUrl` | `https://api.cohere.com` | `/v2/rerank` is appended. |
| `ApiKey` | — | Required. Sent as `Authorization: Bearer <key>`, trimmed; never logged. |
| `Model` | `rerank-v5.0-pro` | The model when the call's `RerankOptions.Model` is unset. |
| `MaxTokensPerDoc` | unset | `max_tokens_per_doc`, sent only when set. The v4.0 / v5.0 models take 32k tokens (query and document together); v3.5 and the v3.0 models, 4k. |
| `MaxBatchSize` | `1000` | Documents per request; Cohere recommends against sending more than 1 000. More go in several requests, and are merged. |
| `MaxConcurrentBatches` | `4` | Batches in flight at once; each counts against the key's rate limit (1 000 requests a minute on a production key, 10 on a trial key). |
| `TimeoutSeconds` | `10` | The call's budget, every batch included; past it, `AI.RerankFailed`. |
| `EnableLogging` | `false` | Logs the request bodies at Debug. The documents are in them; the key is not. |

## On the wire

Read from https://docs.cohere.com/reference/rerank, https://docs.cohere.com/docs/rerank and
https://docs.cohere.com/docs/rate-limits on 2026-10-31.

- Request: `{"model", "query", "documents": [...], "max_tokens_per_doc"?}`. `top_n` is not sent: every batch comes back
  whole, so that it can be checked, and `TopN` keeps the best after the merge. `priority` and `relevant_spans` are not
  sent.
- Answer: `{"id", "results": [{"index", "relevance_score"}], "meta"}`; the score is normalized to `[0, 1]`. A batch whose
  answer does not rank each of its documents exactly once (an index outside the batch, twice, or missing) fails the
  call. Equal scores keep the input order.
- `ReturnDocuments` fills `Document` from your input (the v2 API does not send the texts back). A blank query is
  `AI.InvalidQuery`, `TopN` below 1 `AI.InvalidRequest`, no documents an empty success with no request.
- Every failure is a `Result`: a 4xx or 5xx (Cohere's `message` in the error), an unreadable answer, a transport error or
  the budget running out is `AI.RerankFailed`; the first failing batch stops the others. Only your own cancellation
  throws. No retry handler: on a rerank budget, fall back to the order you have.
- Options that cannot serve (a `BaseUrl` that is not absolute `http` / `https`, no key or model, a batch size,
  concurrency or timeout below 1, `MaxTokensPerDoc` below 1) fail the start: `AddCompendiumCohereReranker` validates them
  on start, and a reranker built by hand answers `AI.RerankFailed` before any request.

Batches are scored apart and merged on their scores: Cohere documents each score as the relevance of one document to
the query, normalized to `[0, 1]`, but does not say in so many words that it is independent of the other documents of
the request. Keep `MaxBatchSize` at or above your usual candidate count and a rerank is one request.

## License

MIT — same as Compendium itself.
