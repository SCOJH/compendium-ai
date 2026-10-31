# Changelog

All notable changes to the Compendium AI packages (`Compendium.Abstractions.AI` and the `Compendium.Adapters.*` AI
adapters) are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions
follow the `v*` tags. Earlier versions are described in the generated notes of the
[GitHub Releases](https://github.com/SCOJH/compendium-ai/releases).

## [Unreleased]

### Added

- **Compendium.Adapters.HuggingFace: an `IReranker` over Text Embeddings Inference's `/rerank`.** Additive:
  `AddCompendiumTeiReranker(IConfiguration | Action<TeiRerankerOptions>)` registers it, with its own options
  (`TeiReranker` section: `BaseUrl`, `ApiKey` sent as Bearer when set, `Truncate` true, `TruncationDirection` Right,
  `MaxBatchSize` 32 — TEI's own `--max-client-batch-size` default —, `TimeoutSeconds` 10 for the whole call). The
  documents come back by descending relevance (TEI's sigmoid score, 0 to 1), each with its index in the input; more
  than a batch go in several requests, sent together, and are merged (equal scores keep the input order). `TopN` keeps
  the best, `ReturnDocuments` fills `Document` from the input, `Model` is not sent (a TEI server serves one model). A
  4xx or 5xx, an unreadable answer, an index outside its batch, a transport error or the budget running out is
  `Result.Failure(AI.RerankFailed)`, never an exception (the caller's own cancellation still throws); a blank query is
  `AI.InvalidQuery`, `TopN` below 1 `AI.InvalidRequest`, no documents an empty success with no request. No retry
  handler: a rerank runs on a short budget and its caller falls back to the order it has. The chat provider is
  unchanged. (EIS-75, agent-spot/backlog#260)
- **Structured output: a JSON Schema on the request, constrained by the provider itself.** All additive and optional in
  `Compendium.Abstractions.AI`: `CompletionRequest.ResponseSchema` (`ResponseSchema`: `Name`, `SchemaJson`, `Strict`,
  true by default; `Parse()` checks it), and `IAIProvider.SupportsJsonSchema(model)`, a default interface member that
  answers `false`, so a provider written before it still compiles and says it cannot. Without `ResponseSchema`, every
  request is the one it was. Where an adapter sends it, an invalid schema fails with `AI.InvalidRequest` before any
  request; where it does not, the request goes without it, without an error, and the adapter logs a warning. It takes
  precedence over the `*StructuredOutputExtensions` keys, unchanged otherwise. Compendium.Adapters.OpenAI,
  Compendium.Adapters.AzureOpenAI, Compendium.Adapters.Mistral, Compendium.Adapters.LiteLLM:
  `response_format.json_schema` (`name`, `schema`, `strict`); OpenAI's older models (GPT-3.5, GPT-4 / GPT-4 Turbo,
  `gpt-4o-2024-05-13`, o1-mini, o1-preview, `chatgpt-*`) get the warning. Compendium.Adapters.Gemini:
  `responseMimeType: "application/json"` + `responseJsonSchema` (JSON Schema, where the existing key keeps
  `responseSchema`), from Gemini 2.0, the `-latest` aliases included; Gemini 1.x, the first `gemini-pro` /
  `gemini-pro-vision` and Gemma get the warning, and so does a Gemini 2.x request with tools (a schema and function
  calling go together on Gemini 3 only). Compendium.Adapters.Ollama:
  `format` with the schema (the wire field becomes a JSON value; it was never set). Compendium.Adapters.Anthropic:
  `output_config.format: {type: "json_schema", schema}` beside the reasoning's `output_config.effort`, with no beta
  header, on Claude 4.5 and later, Fable and Mythos; older models get the warning. Compendium.Adapters.Bedrock: Converse's
  `outputConfig.textFormat` (`json_schema`, the schema as a JSON string) on the same Claude models, directly or through a
  cross-Region profile, but not Claude Haiku 4.5 through the India profile (`in.`); the other models get the warning. Compendium.Adapters.DeepSeek: its JSON mode
  (`response_format: {type: "json_object"}`) without the schema, and the warning — the one adapter whose request changes
  without the constraint; without "json" in the prompt the answer can come back blank. Compendium.Adapters.HuggingFace,
  Compendium.Adapters.Mercury, Compendium.Adapters.OpenRouter, Compendium.Adapters.ZAi: the warning. No adapter rewrites
  the schema: what the providers require (`additionalProperties: false` on each object, no numeric or length bounds) and
  what is recommended so one schema goes everywhere (every property `required`, optional ones as unions with `null`,
  within Claude's and Bedrock's bound of 16 union-typed properties) is in the README. No model listing changes. (EIS-70, agent-spot/backlog#225)
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
  name, or arguments that open an object once the call's are whole JSON (a header sent again continues the call) —,
  the schema's default id `"null"` never taken as an id, a call with no id given one in Mistral's format (derived from
  the completion, so the same response, read again, gives the same id), a stream that reaches `[DONE]` without a
  `finish_reason` still ending with its calls, and one cut before `[DONE]` with calls under way ending with the
  existing `AI.StreamInterrupted` rather than with partial calls. `function.arguments` is read as a string or as an
  object (an object used to fail the response). `WithParallelToolCalls(bool)` sends `parallel_tool_calls`. The
  `mistral.tool_calls` metadata is unchanged; the other adapters ignore the new fields. (ALF-22,
  agent-spot/backlog#510)

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

- Compendium.Adapters.Mistral: a stream whose read fails mid-way (the connection dropped: an `IOException` from the
  body) now ends with `Result.Failure(AI.StreamInterrupted)`, as Bedrock's does, instead of throwing the exception out
  of the iterator. Cancellation is unchanged. (ALF-22)
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
