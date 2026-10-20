# Compendium.Adapters.ZAi

Direct adapter for **Z.ai's chat completions API** (OpenAI-compatible, `https://api.z.ai/api/paas/v4`) implementing
`IAIProvider` from `Compendium.Abstractions.AI`, for the GLM models: GLM 5.3 (and Flash, FlashX), 5.2, 5.1, 5, 4.7, 4.6.

What a GLM needs — its effort scale, room for its reasoning in `max_tokens`, the reasoning kept apart from the answer —
comes from the GLM dialect, `Compendium.Adapters.ZAi.Glm`, shared with the Mistral and OpenRouter adapters
([`docs/adr/0001-glm-dialect.md`](../../docs/adr/0001-glm-dialect.md)). This package adds Z.ai's wire format.

## Quick start

```csharp
services.AddCompendiumZAi(builder.Configuration);           // section "ZAi"
// or
services.AddCompendiumZAi(o => o.ApiKey = builder.Configuration["ZAi:ApiKey"]!);
```

```jsonc
{
  "ZAi": {
    "ApiKey": "...",
    "BaseUrl": "https://api.z.ai/api/paas/v4",   // ZAiOptions.MainlandChinaBaseUrl for BigModel
    "DefaultModel": "glm-5.3",
    "DefaultMaxTokens": 4096,                     // the answer's room; the reasoning gets its own on top
    "TimeoutSeconds": 300
  }
}
```

The default endpoint is Z.ai's standard, pay-as-you-go API. The GLM Coding Plan has its own endpoint, billing and
behaviour (reasoning kept across turns by default); point `BaseUrl` at it only if that is what you mean to use.

## On the wire

Read from https://docs.z.ai/api-reference/llm/chat-completion.md, https://docs.z.ai/guides/capabilities/thinking.md and
https://docs.z.ai/guides/capabilities/streaming.md on 2026-10-20.

| `CompletionRequest` | Request |
|---|---|
| `Reasoning` unset | no `thinking`, no `reasoning_effort` (Z.ai's defaults: enabled, `max`); `max_tokens` = answer + 32 768, at most 131 072 |
| `Reasoning` set | `thinking: {type: "enabled"}` — never `disabled`, an error on GLM 5.3 — and `reasoning_effort` `low` / `high` / `max` (5.3+), `high` / `xhigh` (5.2, which Z.ai runs at `max`) |
| `MaxTokens` | the answer's room; the reasoning's is added (`BudgetTokens`, else per effort) |
| `Temperature`, `TopP`, `StopSequences` | sent as given (Z.ai: temperature in [0, 1], one stop word) |
| `FrequencyPenalty`, `PresencePenalty`, `Message.Name` | not sent: Z.ai's schema has none |
| `RequestId`, `UserId` | `request_id` (6–64 characters), `user_id` (6–128), left out otherwise |
| `WithTools(tools, "auto")` | `tools`, `tool_choice: "auto"` — any other choice is refused (`AI.InvalidRequest`) before the request is sent |

| Response | `CompletionResponse` / `CompletionChunk` |
|---|---|
| `content` | `Content` / `ContentDelta`: the answer only |
| `reasoning_content` | `Reasoning` / `ReasoningDelta` when `Reasoning.IncludeReasoning`; dropped otherwise, never in the answer |
| `tool_calls` | `GetToolCalls()` (`zai.tool_calls`); `arguments` read as a JSON string or an object |
| `finish_reason` | `sensitive` → `ContentFilter`, `model_context_window_exceeded` → `Length`, `network_error` → `Other` |
| `usage` | `UsageStats`, on the last chunk of a stream; Z.ai documents no reasoning count |

| Error | `Error.Code` |
|---|---|
| 401 (1000, 1001, 1003, 1005) | `AI.InvalidApiKey` |
| 429 with 1113 (no balance), 1309, 1314 (expired package) | `AI.InsufficientCredits` |
| 429 (1302, 1305, usage windows 1308–1321) | `AI.RateLimitExceeded` |
| 429 with 1311, 1315 (model not in the plan, key for another product) | `AI.ProviderError` |
| 400 with 1211 | `AI.ModelNotFound` |
| 400 with 1301 | `AI.ContentFiltered` |
| other | `AI.ProviderError` with Z.ai's code |

`ListModelsAsync` returns the GLM models documented on 2026-10-20 (Z.ai has no model list endpoint on this API).
`HealthCheckAsync` sends a one-token probe to `DefaultModel`, billed as such. `EmbedAsync` is refused (`AI.Unsupported`).

## Not covered

Native tool calls in a stream (the abstraction has no place for them; `tool_stream` is not sent), replaying the
reasoning across turns (Preserved Thinking), web search and retrieval tools, `response_format`.

## License

MIT — same as Compendium itself.
