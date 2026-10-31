# Changelog

All notable changes to the Compendium AI packages (`Compendium.Abstractions.AI` and the `Compendium.Adapters.*` AI
adapters) are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions
follow the `v*` tags. Earlier versions are described in the generated notes of the
[GitHub Releases](https://github.com/SCOJH/compendium-ai/releases).

## [Unreleased]

### Added

- **A reasoning channel: an effort or a budget on the request, the reasoning apart from the answer.** All additive and
  optional in `Compendium.Abstractions.AI`: `CompletionRequest.Reasoning` (`ReasoningOptions`: `Effort` low · medium ·
  high · xhigh · max, `BudgetTokens`, `IncludeReasoning`, true by default), `CompletionChunk.ReasoningDelta`,
  `CompletionResponse.Reasoning`, `UsageStats.ReasoningTokens`. The reasoning never comes in `Content` /
  `ContentDelta`, is returned only when asked for, and is never to be sent back as a conversation turn. Without
  `Reasoning`, every request and result is the one it was. Compendium.Adapters.Anthropic: adaptive thinking
  (`thinking: {type: "adaptive"}` + `output_config.effort`) on the models that take it, `thinking: {type: "enabled",
  budget_tokens}` with `max_tokens` raised by the budget on earlier ones, no temperature or top_p with thinking,
  `thinking_delta` → `ReasoningDelta`, a model's unasked thinking left out. Compendium.Adapters.OpenAI:
  `reasoning_effort`, `max_completion_tokens` instead of `max_tokens`, no sampling parameters, reasoning tokens
  reported. Compendium.Adapters.Mistral: `reasoning_effort`, and `thinking` content chunks split into `ReasoningDelta` /
  `Reasoning` (such a stream used to fail to deserialise). Compendium.Adapters.DeepSeek: `reasoning_content` →
  `ReasoningDelta` / `Reasoning` when asked for. The other adapters send the request without it. (ASP-36,
  agent-spot/backlog#36)
- **A GLM dialect shared by the Z.ai, Mistral and OpenRouter adapters, and a Z.ai adapter.** A GLM model behaves the
  same whichever host serves it. `Compendium.Adapters.ZAi.Glm` (new, no HTTP, depends on `Compendium.Abstractions.AI`
  only) recognises a GLM id whatever the host's prefix or suffix (`glm-5.3`, `zai-glm-5-3`, `z-ai/glm-5.3:batch`,
  `zai-org/GLM-5.3`…), knows each version's capabilities, puts the effort on GLM's scale and gives `max_tokens` room
  for the reasoning on top of the answer's; the reasoning is read apart, only when asked for, never in `Content` /
  `ContentDelta`, and GLM's finish reasons are mapped. `Compendium.Adapters.ZAi` (new): an `IAIProvider` for Z.ai's
  OpenAI-compatible API — `thinking: {type: "enabled"}`, `reasoning_effort`, `reasoning_content` read into
  `ReasoningDelta` / `Reasoning`, native tool calls (`tool_choice` limited to `auto`, anything else refused before the
  request is sent), Z.ai's business error codes (429 + 1113 → `InsufficientCredits`), `AddCompendiumZAi`.
  Compendium.Adapters.Mistral (`zai-glm-*`) and Compendium.Adapters.OpenRouter (`z-ai/glm-*`) send and read GLM models
  through the dialect: on Mistral, GLM 5.3 gets `low` or `high`, never `max` (Mistral's enum stops at `xhigh`), and a
  stream's text part with no `type` is kept; on OpenRouter, `reasoning: {effort, exclude}`, `reasoning` /
  `reasoning_details` read apart and the trailing usage frame read, so the final chunk carries the usage. Every other
  model is sent and read exactly as before (golden tests pin it to the byte). Design: `docs/adr/0001-glm-dialect.md`.
  (ZAI-01, agent-spot/backlog#493)
- **Native tool calls in the abstraction and the Mistral adapter, in a stream too.** All optional and additive in
  `Compendium.Abstractions.AI`: `ToolCall` (`Id`, `Name`, `ArgumentsJson`), `Message.ToolCalls` (an assistant turn's
  calls), `Message.ToolCallId` (on a `Tool` message, the call answered), `Message.Assistant(content, toolCalls)` and
  `Message.ToolResult(toolCallId, content)`, and the calls read back in `CompletionResponse.ToolCalls` and on the final
  `CompletionChunk.ToolCalls`, each call whole. Unset, every record serialises as before (`WhenWritingNull`).
  Compendium.Adapters.Mistral sends an assistant turn's calls as `tool_calls` and a tool message as `tool_call_id`
  (plus the called tool's `name`, as Mistral's guide shows it), reads `tool_calls` from a response and, put together
  from the deltas, from a stream: grouped by `index`, a new id starting a new call — or, with no id, another tool's
  name, or the same once the first call's arguments are whole JSON (a header sent again continues the call),
  the schema's default id `"null"` never taken as an id, a call with no id given one in Mistral's format (derived from
  the completion, so the same response, read again, gives the same id), a stream that reaches `[DONE]` without a
  `finish_reason` still ending with its calls, and one cut before `[DONE]` with calls under way ending with
  `AI.StreamInterrupted` rather than with partial calls. `function.arguments` is read as a string or as an object (an object used to fail the
  response). `WithParallelToolCalls(bool)` sends `parallel_tool_calls`. The `mistral.tool_calls` metadata is unchanged;
  the other adapters ignore the new fields. (ALF-22, agent-spot/backlog#510)

### Changed

- **Every package now targets `net9.0` and `net10.0`.** .NET 9 (STS) leaves support on 2026-11-10; .NET 10 is the
  LTS consumers are moving to, and `net9.0` stays for the transition only. `global.json` moves to the .NET 10 SDK
  (10.0.100 or any later 10.0 feature band), which builds both targets; the CI and release workflows install the
  .NET 9 SDK beside it for its runtime, so every test runs once per target. `Microsoft.Extensions.*` is pinned per
  target: 9.0.20 on `net9.0`, 10.0.13 on `net10.0`. On `net9.0` that is a lower floor than before — the single line
  was a highest-wins mix that asked a .NET 9 consumer for 10.0.12 of Configuration, Configuration.Abstractions,
  Configuration.Binder, DependencyInjection, DependencyInjection.Abstractions, Logging.Abstractions and Options, 10.0.8
  of Logging, and 9.0.16 of Http, Options.ConfigurationExtensions and Options.DataAnnotations; a consumer that pins
  10.0.x keeps it, one that pins nothing now restores the 9.0.x line of its runtime. `Microsoft.Extensions.Http.Resilience`
  stays at 10.6.0 for both targets (it is not versioned with the runtime; its `net9.0` group asks for 9.0.x packages).
  `System.Text.Json` is no longer a dependency of the adapters: both runtimes ship it, and the .NET 10 SDK flags the
  reference as redundant (NU1510); a `net9.0` consumer that received the 10.0.8 package through an adapter now uses the
  runtime's copy unless it references the package itself (the one `System.Text.Json` node left in such a graph is
  6.0.10, under `Azure.Identity` in `Compendium.Adapters.AzureOpenAI`, below the runtime's copy, which wins).
  `Compendium.Core` and `Compendium.Abstractions` stay at 1.0.5-preview.5 (`lib/net9.0`, consumed by both targets).
  (NET10-01, agent-spot/backlog#363)

### Fixed

- Every streaming adapter (the 11 `*HttpClient` SSE / NDJSON read loops, plus Ollama's pull-progress loop): the loop
  now stops when `StreamReader.ReadLineAsync` returns `null` instead of testing `StreamReader.EndOfStream` before each
  read. `EndOfStream` can block the thread synchronously on a network stream while waiting for the next byte; the .NET
  10 SDK's analyzer flags it as CA2024, an error in this build. Same lines read, same events emitted; only the
  end-of-stream test changes. (NET10-01)

## [1.1.0-preview.5] - 2026-10-10

### Added

- Compendium.Adapters.Anthropic: `AnthropicOptions.WorkspaceId` — sent as `anthropic-workspace-id` on every request
  (keys that act on several workspaces).

### Changed

- Compendium.Adapters.Anthropic: `ListModelsAsync` reads `GET /v1/models` (all pages) and maps `display_name`,
  `created_at` (`Metadata["created_at"]`), `max_input_tokens`, `max_tokens`; a provider error is returned as it is.
  A 404 of `/v1/models` is `AI.ProviderError`, not `AI.ModelNotFound`.
- Compendium.Adapters.Gemini: `ListModelsAsync` reads every page (`pageSize=1000`, `nextPageToken`).

### Removed

- Compendium.Adapters.Anthropic: the internal static model catalog (it listed retired models).

[Unreleased]: https://github.com/SCOJH/compendium-ai/compare/v1.1.0-preview.5...HEAD
[1.1.0-preview.5]: https://github.com/SCOJH/compendium-ai/compare/v1.1.0-preview.4...v1.1.0-preview.5
