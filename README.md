# Compendium AI Domain

The **ai** domain repo of the Compendium framework (repo-per-domain topology, ADR-0007). It hosts the
`Compendium.Abstractions.AI` abstraction, all 13 AI provider adapters and the GLM dialect they share in a single
repository, so a change to the abstraction and its adapters ships as **one atomic PR** and **one release tag**.

- **Framework base packages** (`Compendium.Core`, `Compendium.Abstractions`) are consumed as published
  NuGet packages — this repo does not build the framework.
- **Adapters reference the abstraction via ProjectReference** — no version skew inside the domain.
- **All packages version together** from git tags via MinVer (tag prefix `v`). The domain version train is
  `1.1.x`, above the framework's historical `1.0.x` per-package releases, so domain-repo packages win
  resolution.

## Packages

| Package | Description |
|---|---|
| `Compendium.Abstractions.AI` | Provider-agnostic AI/LLM abstractions: `IAIProvider`, `IPromptRegistry`, `IContextBuilder`, `IReranker`, agent primitives. |
| `Compendium.Adapters.Anthropic` | Anthropic Messages API (Claude family) — streaming SSE, error mapping, prompt caching. |
| `Compendium.Adapters.AzureOpenAI` | Azure OpenAI Service — Entra ID / DefaultAzureCredential and API-key auth. |
| `Compendium.Adapters.Bedrock` | AWS Bedrock via the unified Converse API — Claude, Llama, Mistral, Nova, Titan, Cohere Embed. |
| `Compendium.Adapters.DeepSeek` | DeepSeek direct API (OpenAI-compatible) — chat + reasoning models. |
| `Compendium.Adapters.Gemini` | Google Gemini REST API — chat, streaming, tools, embeddings. |
| `Compendium.Adapters.HuggingFace` | Hugging Face Inference Endpoints + Serverless Inference API. |
| `Compendium.Adapters.LiteLLM` | Self-hostable LiteLLM gateway — 100+ providers behind one endpoint. |
| `Compendium.Adapters.Mercury` | Inception Labs Mercury diffusion LLMs (OpenAI-compatible API). |
| `Compendium.Adapters.Mistral` | Mistral "la Plateforme" — EU-hosted chat, streaming, embeddings. |
| `Compendium.Adapters.Ollama` | Local-LLM inference via the Ollama HTTP API. |
| `Compendium.Adapters.OpenAI` | OpenAI direct API — chat, streaming, embeddings, tools. |
| `Compendium.Adapters.OpenRouter` | OpenRouter — unified access to 100+ hosted models. |
| `Compendium.Adapters.ZAi` | Z.ai direct API (OpenAI-compatible) — the GLM models: reasoning, streaming, native tools. |
| `Compendium.Adapters.ZAi.Glm` | The GLM dialect — no HTTP: GLM ids whatever the host's prefix, effort scale, `max_tokens` room for the reasoning, the reasoning kept apart. Used by the Z.ai, Mistral and OpenRouter adapters ([ADR-0001](docs/adr/0001-glm-dialect.md)). |

Package IDs are unchanged from their source repositories — consumers only bump the version.

## Layout

```
src/Compendium.Abstractions.AI/     # the domain abstraction (packable)
src/Compendium.Adapters.*/          # one project per provider adapter (packable)
src/Compendium.Adapters.ZAi.Glm/    # the GLM dialect the GLM hosts share (packable, no HTTP, not an adapter)
docs/adr/                           # this repository's decisions
tests/Unit/*                        # unit tests (CI-gated, ≥90% line coverage)
tests/Integration/*                 # integration tests (network/Docker-gated, excluded from CI)
```

## Build & test

Every package targets `net9.0` and `net10.0` (NET10-01; `net9.0` stays until .NET 9 leaves support on
2026-11-10). Building needs the .NET 10 SDK (`global.json`); running the `net9.0` half of the tests also
needs the .NET 9 runtime, which the .NET 9 SDK brings — the CI installs both.

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release --filter "FullyQualifiedName!~IntegrationTests"
```

Integration tests (`tests/Integration/*`) require live provider credentials and/or Docker
(Ollama uses Testcontainers) and are skipped in CI.

## Reasoning channel (not released)

`CompletionRequest.Reasoning` (`ReasoningOptions`: `Effort` low…max, `BudgetTokens`, `IncludeReasoning`) asks the model for
its own reasoning. Unset, every request is the one it was. The reasoning comes back apart from the answer —
`CompletionChunk.ReasoningDelta` / `CompletionResponse.Reasoning`, only when asked for — and `UsageStats.ReasoningTokens`
when the provider counts them. Never send it back as a conversation turn.

| Adapter | On the wire |
|---|---|
| Anthropic | Opus/Sonnet 4.6+, Fable, Mythos: `thinking: {type: "adaptive", display}` + `output_config.effort` (`xhigh` from Opus 4.7; a budget is refused there, so it is added to `max_tokens` as headroom). Earlier 4.x and Sonnet 3.7: `thinking: {type: "enabled", budget_tokens}` (≥ 1 024), `max_tokens` raised by it. No temperature, no top_p with thinking. Older models: unchanged. `thinking_delta` → `ReasoningDelta`. |
| OpenAI | `reasoning_effort`, `max_completion_tokens` instead of `max_tokens`, no temperature / top_p / penalties; `reasoning_tokens` → `ReasoningTokens`. |
| Mistral | `reasoning_effort: "high"`; `thinking` content chunks (a list, where the answer is a string) → `ReasoningDelta` / `Reasoning`. GLM (`zai-glm-*`): through the GLM dialect — `reasoning_effort` `low` / `high` for GLM 5.3 (`XHigh` and `Max` go as `high`: Mistral's API enum has no `max`), `high` / `xhigh` for GLM 5.2 (none unless an effort is asked), `max_tokens` with room for the reasoning, content always chunk lists. |
| DeepSeek | `reasoning_content` → `ReasoningDelta` / `Reasoning` (unasked, dropped as before; `InlineReasoningInContent` unchanged). |
| Z.ai | Through the GLM dialect: `thinking: {type: "enabled"}` (never `disabled`), `reasoning_effort` `low` / `high` / `max`, `max_tokens` with room for the reasoning; `reasoning_content` → `ReasoningDelta` / `Reasoning`. |
| OpenRouter | GLM (`z-ai/glm-*`) only, through the GLM dialect: `reasoning: {effort, exclude}`, `max_tokens` with room for the reasoning; `reasoning` → `ReasoningDelta` / `Reasoning`, `reasoning_tokens` → `ReasoningTokens`. Other models: ignored. |
| Others | ignored: the request is sent without it. |

A GLM reasons unasked (GLM 5.3 always), so its `max_tokens` gets room for the reasoning with or without
`Reasoning`: a deliberate exception, for GLM only, to "unset, every request is the one it was". Every non-GLM request
and response is pinned to the byte by golden tests.

The version is set by the tag (MinVer): this lands with the next `v1.1.0-preview.*` tag (`v1.1.0-preview.5` exists).

## Native tool calls (not released)

A conversation that uses the provider's own tool calling (the request's `tools`) carries its calls in the abstraction:

- `Message.ToolCalls`: the calls an assistant turn made (`ToolCall`: `Id`, `Name`, `ArgumentsJson`);
- `Message.ToolCallId`: on a `Tool` message, the id of the call whose result its `Content` is;
- `Message.Assistant(content, toolCalls)` and `Message.ToolResult(toolCallId, content)` build both.

The calls come back in `CompletionResponse.ToolCalls` and, in a stream, on the final `CompletionChunk.ToolCalls`: each
call is whole there, its arguments joined from every delta that carried a piece. All four properties are optional and
left out of JSON when `null`. Unset, every request is the one it was.

| Adapter | On the wire |
|---|---|
| Mistral | An assistant turn's calls go as `tool_calls`, with the arguments as a string. A tool message goes with `tool_call_id`, plus the called tool's `name` (taken from the call when the message has none), as Mistral's guide shows it. `tool_calls` are read from a response and from a stream, whether a call comes whole in one delta or in pieces: the pieces are grouped by `index`, and a new id starts a new call (with no id, a second name does). A call sent without an id gets one, in Mistral's format; a stream that ends without a `finish_reason` still ends with its calls. The arguments can be a string or an object. `finish_reason: "tool_calls"` → `FinishReason.ToolCall`. `WithParallelToolCalls(false)` sends `parallel_tool_calls: false` (one call at most per turn); without it, the field is not sent. GLM (`zai-glm-*`) takes the same path. |
| Others | Ignored: the message is sent as before, and no call is read into `ToolCalls`. An adapter's own tool-call metadata (its `GetToolCalls()`, where it has one) is unchanged. |

A conversation in Mistral's native mode:

```csharp
var request = new CompletionRequest { Model = "mistral-large-latest", Messages = conversation }
    .WithTools(tools, toolChoice: "auto");

var text = new StringBuilder();
IReadOnlyList<ToolCall> calls = [];
await foreach (var chunk in provider.StreamCompleteAsync(request))
{
    if (chunk.IsFailure) break;
    text.Append(chunk.Value.ContentDelta);
    if (chunk.Value.IsFinal) calls = chunk.Value.ToolCalls ?? [];   // the calls, whole, on the final chunk
}

conversation.Add(Message.Assistant(text.ToString(), calls));        // the turn, sent back as it came
foreach (var call in calls)
{
    conversation.Add(Message.ToolResult(call.Id, Run(call)));       // one result per call, by its id
}
```

## Releasing

Push a tag `v*` (e.g. `v1.1.0-preview.2`). The Release workflow packs all 15 packages and publishes to
GitHub Packages (primary feed), then to nuget.org (Trusted Publishing, or `NUGET_API_KEY`): with neither
credential that step fails the run, after the GitHub Packages push.

Record the release in [`CHANGELOG.md`](CHANGELOG.md) before tagging.

## Provenance

This repo was assembled from 14 source repositories (framework subtree + 12 single-adapter repos +
scaffold). See [MIGRATION.md](MIGRATION.md) for the component → source repo → SHA table.
