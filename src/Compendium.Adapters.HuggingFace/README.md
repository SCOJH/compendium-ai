# `compendium-adapter-huggingface`

[Hugging Face](https://huggingface.co/) AI provider adapter for the [Compendium](https://github.com/sassy-solutions/compendium) event-sourcing framework. Implements `IAIProvider` from `Compendium.Abstractions.AI` against the two main Hugging Face inference surfaces — dedicated **Inference Endpoints** (paid, managed) and the shared **Serverless Inference API** — through the OpenAI-compatible Messages API and the feature-extraction task.

Built from [`template-compendium-adapter-dotnet`](https://github.com/sassy-solutions/template-compendium-adapter-dotnet) per [ADR-0006](https://github.com/sassy-solutions/compendium/blob/main/docs/adr/0006-multi-repo-adapter-split.md) (multi-repo adapter split).

## Install

```bash
dotnet add package Compendium.Adapters.HuggingFace
```

## Quick start — dedicated Inference Endpoint (production)

```csharp
services.AddCompendiumHuggingFace(opt =>
{
    opt.Mode = HuggingFaceMode.InferenceEndpoint;
    opt.HfToken = Environment.GetEnvironmentVariable("HF_TOKEN")!;
    opt.EndpointUrl = "https://<your-endpoint>.endpoints.huggingface.cloud";
});

var ai = sp.GetRequiredService<IAIProvider>();
var result = await ai.CompleteAsync(new CompletionRequest
{
    Model = string.Empty, // endpoint is bound to one model at deploy time
    Messages = new() { Message.User("What is event sourcing?") }
});
```

## Quick start — Serverless Inference (prototyping)

```csharp
services.AddCompendiumHuggingFace(opt =>
{
    opt.Mode = HuggingFaceMode.ServerlessInference;
    opt.HfToken = Environment.GetEnvironmentVariable("HF_TOKEN")!;
    opt.DefaultChatModel = "meta-llama/Meta-Llama-3.1-8B-Instruct";
    opt.DefaultEmbeddingModel = "sentence-transformers/all-MiniLM-L6-v2";
});
```

## Mode comparison

| Aspect | `InferenceEndpoint` | `ServerlessInference` |
|---|---|---|
| URL shape | `https://<name>.endpoints.huggingface.cloud` (one per endpoint) | `https://api-inference.huggingface.co/models/<id>` |
| Cost model | Per-hour compute (CPU / GPU / accelerator) | Free tier + Pro (per-token, shared org quota) |
| Rate limits | Your endpoint's compute capacity only | Aggressive shared limits; HTTP 429 common |
| Cold starts | None (warm) or autoscale-to-zero opt-in (~30 s warm-up) | Frequent — first call to a model often loads it |
| Latency | Predictable | Variable |
| Model selection | One model per endpoint, fixed at deploy time | Any model in the Hugging Face Hub |
| Production fit | Yes (with autoscaling, regions, EU residency) | No — prototyping / spike work only |

## Options

| Option | Required | Default | Notes |
|---|---|---|---|
| `Mode` | yes | `InferenceEndpoint` | One of `InferenceEndpoint`, `ServerlessInference` |
| `HfToken` | yes | — | A `hf_*` user access token from <https://huggingface.co/settings/tokens> |
| `EndpointUrl` | endpoint mode | — | Full URL of your dedicated endpoint |
| `ServerlessBaseUrl` | serverless mode | `https://api-inference.huggingface.co/models` | Override if HF moves the API |
| `DefaultChatModel` | — | `meta-llama/Meta-Llama-3.1-8B-Instruct` | Used as fallback when request has no `Model`. In endpoint mode it's sent informationally; the server uses what the endpoint is bound to |
| `DefaultEmbeddingModel` | — | `sentence-transformers/all-MiniLM-L6-v2` | Same caveat |
| `DefaultMaxTokens` | — | `1024` | Floor for chat completions |
| `TimeoutSeconds` | — | `120` | Per-request HTTP timeout |
| `RetryAttempts` | — | `3` | Applied by `Microsoft.Extensions.Http.Resilience` |
| `EnableLogging` | — | `false` | Logs request/response bodies at `Debug` |

Bind from configuration with `services.AddCompendiumHuggingFace(configuration)` (binds section `HuggingFace`).

## Model recommendations

For **dedicated Inference Endpoints**, the deploy-time model choice matters more than anything in this adapter. Good starting points:

- **Llama-3.1-8B-Instruct** — broad chat, tool-calling support via TGI.
- **Qwen-2.5-7B-Instruct** — strong English + Chinese, fast on a single L4.
- **Mistral-7B-Instruct-v0.3** — small footprint, lower cost-to-serve.
- **DeepSeek-V3 / R1** — when you need reasoning depth and can afford the compute.

For **embeddings**, `BAAI/bge-large-en-v1.5` (general English) and `sentence-transformers/all-MiniLM-L6-v2` (fast, small) cover most needs.

## Tool / function calling — caveat

Tool calling is **best-effort**. The OpenAI-compatible `tools` + `tool_choice` body fields are passed through unchanged, and modern TGI-backed endpoints (Llama-3.1+, Qwen-2.5+, Mistral instruct families) generally honor them. **Non-TGI endpoints** (vision, audio, classification, older runtimes) silently ignore the fields and return a plain assistant message. There is no advance signal of capability — defensive consumers should check `response.GetToolCalls().Count > 0` rather than assume.

## Reranking with Text Embeddings Inference (`IReranker`)

`AddCompendiumTeiReranker` registers an `IReranker` over a [Text Embeddings Inference](https://github.com/huggingface/text-embeddings-inference)
server's `POST /rerank`: a self-hosted or dedicated endpoint that serves one cross-encoder (for instance
`BAAI/bge-reranker-v2-m3`, multilingual). It is apart from the chat provider above: its own options, its own URL, no HF
token required.

```csharp
services.AddCompendiumTeiReranker(opt =>
{
    opt.BaseUrl = "http://tei-reranker:8080";   // /rerank is appended
    opt.ApiKey = null;                           // the server's --api-key, sent as Bearer; unset: no header
    opt.TimeoutSeconds = 3;                      // the whole call, every batch included
});

var ranked = await sp.GetRequiredService<IReranker>()
    .RerankAsync(query, passages, new RerankOptions { TopN = 5, ReturnDocuments = true }, ct);
// ranked.Value: by descending relevance (0 to 1), each with its Index in `passages`.
```

| Option (`TeiReranker` section) | Default | Meaning |
|---|---|---|
| `BaseUrl` | — | The server's URL. Required. |
| `ApiKey` | none | The server's `--api-key`, sent as `Authorization: Bearer`. |
| `Truncate` | `true` | TEI cuts a pair longer than the model takes, rather than answering 422. |
| `TruncationDirection` | `Right` | Which end TEI cuts. |
| `MaxBatchSize` | `32` | Documents per request; more go in several, sent together, and are merged. TEI refuses more than its `--max-client-batch-size` (32 by default) with a 422: raise both together. |
| `TimeoutSeconds` | `10` | The call's budget; past it, `AI.RerankFailed`. |

- On the wire: `{"query", "texts", "raw_scores": false, "return_text": false, "truncate", "truncation_direction"}`; TEI
  answers `[{"index", "score"}]`, the score through a sigmoid (0 to 1). A cross-encoder scores each pair on its own, so
  batches compare and merge; equal scores keep the input order.
- `TopN` keeps the best (at least 1); `ReturnDocuments` fills `Document` from your input (the texts do not travel back);
  `Model` is not sent: the server serves one model. A blank query is `AI.InvalidQuery`; no documents, an empty success.
- Every failure is a `Result`: a 4xx or 5xx (TEI's `{error, error_type}` in the message), an unreadable answer, an
  index outside its batch, a transport error or the budget running out is `AI.RerankFailed`. Only your own
  cancellation throws. No retry handler: on a rerank budget, fall back to the order you have.

## Production checklist

- **Pin to a dedicated Inference Endpoint** (not serverless) for any user-facing workload.
- **Enable autoscale-to-zero** if traffic is bursty — cuts idle bill to near-zero in exchange for a one-time 30-second warm-up on the first request after idle.
- **Pick the EU region** (`region: eu-west-1` at deploy time) if you're subject to GDPR data-residency requirements.
- **Set a Pro account budget alert** — serverless can burst into the paid tier silently.
- **Cache the typed `HttpClient`** via DI (`AddCompendiumHuggingFace` does this automatically).
- **Test the fallback path** — your code should still function (degraded) if the endpoint is autoscaled-to-zero during cold start (HF returns HTTP 503 with `{"error":"model is currently loading"}`).
- **Watch for HTTP 429** on serverless and back off; `AIErrors.RateLimitExceeded` is surfaced as a typed `Result.Failure` for clean retry logic.
- **Don't ship the HF token in source.** Use environment variables, secret managers, or Compendium's secret store.

## Repository conventions

| Aspect | Choice |
|---|---|
| Target | .NET 9 and .NET 10 (`net9.0;net10.0`), C# 13 |
| Test framework | xUnit 2.9.3 + FluentAssertions 6.12.1 + NSubstitute 5.1.0 |
| Coverage | 98.4 % line / 94.3 % branch (102 tests) — gate at 90 % |
| HTTP mocking | `RichardSzalay.MockHttp` 7.0.0 |
| Result pattern | `Result<T>` from `Compendium.Core` |
| Test naming | `{SUT}Tests` / `{Method}_{Scenario}_{Expected}` + AAA explicit |

## Build & test locally

```bash
dotnet restore
dotnet build -c Release
dotnet test  -c Release --filter "FullyQualifiedName!~IntegrationTests"
```

## Releasing

Tag with a `v` prefix on `main` to publish to nuget.org + GitHub Packages:

```bash
git tag v1.0.0-preview.0
git push origin v1.0.0-preview.0
```

(Tagging is done by the orchestrator after merge; do not push tags from feature branches.)

## License

[MIT](LICENSE) — Copyright © 2026 Sassy Solutions.
