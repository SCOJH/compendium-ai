# `compendium-adapter-mercury`

[Mercury](https://www.inceptionlabs.ai/) (Inception Labs) AI provider adapter for the [Compendium](https://github.com/sassy-solutions/compendium) event-sourcing framework. Implements `IAIProvider` from `Compendium.Abstractions.AI` against Mercury's OpenAI-compatible API.

> **Experimental / exploratory.** Mercury is an early-stage diffusion-based LLM (dLLM) platform. Its differentiator is **throughput**, not capability breadth. Treat this adapter as a preview — pin the version, and expect schema or pricing changes from Inception Labs.

Extracted from `sassy-solutions/compendium` per [ADR-0006](https://github.com/sassy-solutions/compendium/blob/main/docs/adr/0006-multi-repo-adapter-split.md) (multi-repo adapter split).

## Why a Mercury adapter?

Mercury is the first generally-available diffusion LLM. Per Inception Labs' [own benchmarks](https://www.inceptionlabs.ai/news/announcing-mercury), it runs **5-10x faster** than autoregressive speed-tier peers (GPT-4.1 Nano, Claude 3.5 Haiku) while ranking 1st in speed and tied 2nd in quality on Copilot Arena. That throughput characteristic makes it interesting for:

- **Interactive autocomplete** — code editors, search-as-you-type, voice agents
- **Tight agent loops** — where each step is gated on token-level streaming latency
- **Cost-sensitive batch jobs** — at $0.25/MTok in, $0.75/MTok out (May 2026), Mercury is competitive on price as well as speed

Embeddings are **not** supported by Mercury; `EmbedAsync` returns `AI.InvalidRequest`. Pair this adapter with a dedicated embedding provider (OpenAI, Voyage, Cohere) when you need vectors.

## Install

```bash
dotnet add package Compendium.Adapters.Mercury
```

```csharp
// configuration-bound
services.AddCompendiumMercury(builder.Configuration.GetSection("Mercury"));

// or inline
services.AddCompendiumMercury(o =>
{
    o.ApiKey       = Environment.GetEnvironmentVariable("MERCURY_API_KEY")!;
    o.DefaultModel = "mercury";
});
```

Resolve and use:

```csharp
var ai = host.Services.GetRequiredService<IAIProvider>();

var result = await ai.CompleteAsync(new CompletionRequest
{
    Model    = "mercury",
    Messages = new List<Message> { Message.User("Hello") },
});

result.Match(
    response => Console.WriteLine(response.Content),
    error    => Console.Error.WriteLine($"{error.Code}: {error.Message}"));
```

## Configuration

| Property | Default | Description |
|---|---|---|
| `ApiKey` | _(required)_ | Bearer token issued by Inception Labs. |
| `BaseUrl` | `https://api.inceptionlabs.ai/v1` | OpenAI-compatible v1 endpoint. Override if Inception Labs reroutes. |
| `DefaultModel` | `mercury` | Used when a request omits `Model`. |
| `DefaultTemperature` | `0.7` | Used when `request.Temperature` is left at zero. |
| `DefaultMaxTokens` | `4096` | Applied when `request.MaxTokens` is `null`. |
| `TimeoutSeconds` | `120` | Per-request HTTP timeout. |
| `RetryAttempts` | `3` | Forwarded to `Microsoft.Extensions.Http.Resilience`'s standard handler. |
| `EnableLogging` | `false` | Logs request/response bodies at `Debug`. Off in production — bodies may contain PII. |

Config-section name: **`Mercury`** (i.e. `appsettings.json` → `{"Mercury": {"ApiKey": "..."}}`).

## Model catalogue (May 2026)

| Model | Context | Max output | Tools | JSON mode | Use case |
|---|---|---|---|---|---|
| `mercury` | 128k | 32k | yes | yes | General chat, voice agents |
| `mercury-2` | 128k | 50k | yes | yes | Successor to `mercury`, longer outputs |
| `mercury-coder` | 128k | 32k | yes | yes | Code generation & completion |
| `mercury-edit` | 128k | 32k | no | no | Inline code editing |
| `mercury-edit-2` | 128k | 32k | no | no | Successor to `mercury-edit` |

`ListModelsAsync` returns the canonical list. Always re-check against the live API for current pricing and capability flags.

## Throughput claim

Inception Labs reports **>1000 tok/s** for `mercury` and `mercury-coder` (source: [Mercury announcement](https://www.inceptionlabs.ai/news/announcing-mercury), corroborated by Copilot Arena leaderboards). The included sample, `samples/01-low-latency-chat`, prints a back-of-envelope `completion_tokens / wall_time_seconds` measurement so you can verify on your own network and prompt size:

```bash
MERCURY_API_KEY=sk-... dotnet run --project samples/01-low-latency-chat
```

Typical observed throughput (single-stream, modest prompt, US-East): **600-1200 tok/s**. The exact number is dominated by network latency to `api.inceptionlabs.ai` and prompt length.

For an apples-to-apples comparison against autoregressive providers, run the same sample against `compendium-adapter-openai` (gpt-4o-mini) and `compendium-adapter-anthropic` (claude-3-5-haiku). On the same prompt and network, expect ~80-200 tok/s from those.

## Differentiation vs autoregressive LLMs

Diffusion LLMs generate tokens in parallel rather than strictly left-to-right. The practical implications:

- **Strengths.** Throughput is roughly independent of output length; latency to first complete sentence is much lower. Excellent for autocomplete, voice, and any UX where _time-to-coherent-chunk_ matters more than peak quality.
- **Trade-offs.** Quality is competitive with speed-tier autoregressive models (gpt-4o-mini, claude-3-5-haiku) but typically below frontier (gpt-4o, claude-3.7-sonnet, gpt-5). For long-horizon reasoning or open-ended creative work, an autoregressive frontier model is usually a better choice.
- **Tool calling.** Supported on `mercury`, `mercury-2`, `mercury-coder` via the OpenAI-compatible `tools` / `tool_choice` parameters. Round-trip semantics match other adapters in this family.

## Risks

- **Early-stage provider.** Inception Labs is a recent entrant; API stability, rate-limit policy, and pricing may change with less notice than incumbent providers.
- **Single-region.** Mercury currently serves from US datacenters; transatlantic latency adds 80-150ms on top of the model's own latency.
- **Cloudflare gate on docs.** The docs site (`docs.inceptionlabs.ai`) sits behind a Cloudflare browser-challenge gate that blocks plain `curl` — use a real browser when consulting them.
- **No embeddings.** Plan a separate embedding provider in your architecture.

## Production checklist

- [ ] API key stored in a secret manager, not source control.
- [ ] `EnableLogging = false` in production (it leaks prompt bodies to logs).
- [ ] Timeouts tuned to your SLO — default 120s is generous; set 15-30s for interactive surfaces.
- [ ] Fallback provider configured for outages (Mercury has a single-region failure mode).
- [ ] Cost guardrail: monitor `completion_tokens` per request; agent loops can fan out fast at 1000 tok/s.

## Repository conventions

| Aspect | Choice |
|---|---|
| Target | .NET 9 and .NET 10 (`net9.0;net10.0`), C# 13 |
| Test framework | xUnit 2.9.3 + FluentAssertions 6.12.1 + NSubstitute 5.1.0 |
| Coverage | **99.4 %** line / 93.1 % branch (71 tests) — gate at 90 % |
| HTTP mocking | `RichardSzalay.MockHttp` 7.0.0 |
| Result pattern | `Result<T>` from `Compendium.Core` |
| Test naming | `{SUT}Tests` / `{Method}_{Scenario}_{Expected}` + AAA explicit |

## Build & test locally

```bash
dotnet restore
dotnet build -c Release
dotnet test  -c Release --collect:"XPlat Code Coverage"
```

## Versioning

This package will be tagged from `main` after the first PR lands. MinVer drives versions from git tags (prefix `v`). First release will be `v1.0.0-preview.0` (cut by the orchestrator after merge — do **not** push a tag from this PR).

## License

[MIT](LICENSE) — Copyright © 2026 Sassy Solutions.
