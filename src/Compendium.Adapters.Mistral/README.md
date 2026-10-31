# Compendium.Adapters.Mistral

Direct adapter for the **Mistral AI "la Plateforme" REST API** (`https://api.mistral.ai/v1`) that implements [`IAIProvider`](https://www.nuget.org/packages/Compendium.Abstractions.AI) from `Compendium.Abstractions.AI`.

| What | Value |
|---|---|
| Target framework | net9.0;net10.0 |
| Abstractions pin | `Compendium.Abstractions.AI` 1.0.1 |
| HTTP stack | Typed `HttpClient` + `Microsoft.Extensions.Http.Resilience` (standard pipeline) + `System.Text.Json` |
| Auth | `Authorization: Bearer <ApiKey>` |
| Surface | Chat completions (sync + SSE streaming), embeddings (batched), tool calling, structured outputs, Pixtral vision |
| Tests | xUnit 2.9 + FluentAssertions + NSubstitute + RichardSzalay.MockHttp |

## EU data residency — the headline value prop

Mistral AI hosts inference in **France (EU)**. Payloads, including PII, never leave the EU. For European tenants subject to GDPR (or contractually required to keep data on EU soil), this adapter is the strongest default in the Compendium ecosystem. Pair it with `Compendium.Adapters.OpenAI` only when you need OpenAI-specific models.

| Need | Pick |
|---|---|
| GDPR / EU data residency | **`Compendium.Adapters.Mistral`** |
| OpenAI-only models (e.g. `o1`) | `Compendium.Adapters.OpenAI` |
| Gateway / model routing | `Compendium.Adapters.OpenRouter` |
| EU + Azure compliance overlay | `Compendium.Adapters.AzureOpenAI` (EU region) |

## Installation

```bash
dotnet add package Compendium.Adapters.Mistral
```

## Configuration

Register at startup:

```csharp
services.AddCompendiumMistral(opt =>
{
    opt.ApiKey = builder.Configuration["Mistral:ApiKey"]!;
    opt.DefaultModel = "mistral-large-latest";
    opt.DefaultEmbeddingModel = "mistral-embed";
});
```

…or bind from `IConfiguration` (section `Mistral`):

```csharp
services.AddCompendiumMistral(builder.Configuration);
```

```jsonc
{
  "Mistral": {
    "ApiKey": "...",
    "DefaultModel": "mistral-large-latest",
    "DefaultEmbeddingModel": "mistral-embed",
    "TimeoutSeconds": 120,
    "MaxEmbeddingsBatchSize": 512
  }
}
```

The standard resilience pipeline (`AddStandardResilienceHandler`) is applied automatically; tune retry / timeout behaviour by configuring `MistralOptions.TimeoutSeconds` and `RetryAttempts`.

## Model catalog

| Model | Use case |
|---|---|
| `mistral-large-latest` | Top-tier reasoning, default chat model |
| `mistral-small-latest` | Cheap reasoning, integration tests |
| `mistral-medium-latest` | Balanced cost/quality |
| `open-mistral-7b`, `open-mixtral-8x7b` | Open-weight tiers |
| `codestral-latest` | Code-specialised (chat endpoint, same wire format) |
| `pixtral-12b-latest`, `pixtral-large-latest` | Vision (multimodal text + image) |
| `mistral-embed` | Embeddings (1024-dim) |
| `zai-glm-5-3` (`zai-glm-5`, `zai-glm-latest`) | GLM 5.3, Z.ai's open-weight model hosted by Mistral — always reasons; see below |

### GLM (`zai-glm-*`)

The GLM models Mistral hosts go through the GLM dialect (`Compendium.Adapters.ZAi.Glm`, shared with the Z.ai and
OpenRouter adapters — [`docs/adr/0001-glm-dialect.md`](../../docs/adr/0001-glm-dialect.md)):

- their content is always a list of `thinking` and `text` chunks, in a stream too: the thinking goes to
  `ReasoningDelta` / `Reasoning` when `CompletionRequest.Reasoning` asks for it, never into the answer;
- `reasoning_effort` is `low` or `high` for GLM 5.3 — `Low` → `low`; `Medium`, `High`, `XHigh` and `Max` → `high` —
  and `high` or `xhigh` for GLM 5.2 (from `ReasoningOptions.Effort`; none when no effort is asked). Mistral's reasoning
  guide lists `max` for `zai-glm-5-3`, but its API's `ReasoningEffort` enum (`none` … `xhigh`) does not, so `max` is not
  sent. No `thinking` field is sent (Mistral has none);
- `max_tokens` gets room for the reasoning on top of the answer's, with or without `Reasoning` (GLM 5.3 reasons
  anyway): the caller's `BudgetTokens`, else room for the effort actually sent — 4 096 for `low`, 16 384 for `high`,
  32 768 for `xhigh` or when no effort is sent;
- `ListModelsAsync` fills their context window (1M), output limit (128K) and `Metadata["reasoning"] = "always"`.

Every other model is sent and read exactly as before.

## Usage

### Chat (sync)

```csharp
var provider = sp.GetRequiredService<IAIProvider>();
var result = await provider.CompleteAsync(new CompletionRequest
{
    Model = "mistral-large-latest",
    SystemPrompt = "Be concise.",
    Messages = new List<Message> { Message.User("Pick a colour.") },
    MaxTokens = 64
});
if (result.IsSuccess) Console.WriteLine(result.Value.Content);
```

### Chat (streaming)

```csharp
await foreach (var chunk in provider.StreamCompleteAsync(request))
{
    if (chunk.IsSuccess) Console.Write(chunk.Value.ContentDelta);
}
```

The final chunk carries `IsFinal=true`, `FinishReason`, and `Usage` (token counts).

### Codestral (code-specialised chat)

Codestral exposes the same `/v1/chat/completions` surface as the general chat models. Switch by setting `DefaultModel`:

```csharp
services.AddCompendiumMistral(opt =>
{
    opt.ApiKey = "...";
    opt.DefaultModel = "codestral-latest";
});
```

`ListModelsAsync` marks Codestral with `AIModel.Metadata["specialty"] = "code"` so callers can route automatically.

### Embeddings (batched)

`EmbedAsync` chunks the input list into requests no larger than `MaxEmbeddingsBatchSize` (default 512) and aggregates the embeddings, re-mapping indices to the original input order.

```csharp
var result = await provider.EmbedAsync(new EmbeddingRequest
{
    Model = "mistral-embed",
    Inputs = manyTexts
});
```

### Tool calling

```csharp
using Compendium.Adapters.Mistral.Tools;
using Compendium.Abstractions.AI.Agents.Models;

var tools = new List<AgentTool>
{
    new(
        Name: "get_weather",
        Description: "Returns the current weather for the named city.",
        InputSchemaJson: """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""")
};

var request = new CompletionRequest
{
    Model = "mistral-large-latest",
    Messages = new List<Message> { Message.User("Weather in Paris?") }
}.WithTools(tools, toolChoice: "auto");   // "auto" | "any" | "none" | <tool-name>

var result = await provider.CompleteAsync(request);
foreach (var call in result.Value.GetToolCalls())
{
    Console.WriteLine($"{call.ToolName}({call.ArgumentsJson})");
}
```

Tool definitions are serialised into Mistral's `tools[]` array; the assistant's `tool_calls` are surfaced as `IReadOnlyList<AgentToolInvocation>` via `CompletionResponse.GetToolCalls()`.

#### Native tool calls, with their ids, in a stream too

The calls also come back as the abstraction's `ToolCall` (`Id`, `Name`, `ArgumentsJson`): in `CompletionResponse.ToolCalls`,
and on the final chunk of a stream (`CompletionChunk.ToolCalls`, with `FinishReason.ToolCall`). A stream's `tool_calls`
deltas are put together: a call whole in one delta (what Mistral usually sends) or in pieces, several calls, arguments
as a string or an object. A call that comes with no id (absent, or the schema's default `"null"`) is told apart from the
one before it by another tool's name, or by arguments that open an object once the first call's are whole JSON (with
no name, the new call is the tool of the last header); a header sent again continues the call. It gets an id from the
adapter: nine letters and digits (the format Mistral's validator requires), derived from the completion's id, the
call's place, its name and its arguments, so the same response, read again, gives the same id (a completion with no id
gets a random part instead). A stream that reaches `[DONE]` without a `finish_reason` still ends with the calls, on a
final chunk of its own. One cut before `[DONE]` while calls were under way, or cut by a read error (the connection
dropped), ends with `AI.StreamInterrupted` (the calls may be partial); a cancelled one ends with nothing more. To
continue, send the turn back, then one result per call:

```csharp
conversation.Add(Message.Assistant(text, calls));              // → {"role":"assistant","content":…,"tool_calls":[…]}
conversation.Add(Message.ToolResult(calls[0].Id, resultJson)); // → {"role":"tool","content":…,"name":…,"tool_call_id":…}
```

Mistral may call several tools in one turn, and each call needs its own result. To get one call at most per turn, add
`.WithParallelToolCalls(false)` (Mistral's `parallel_tool_calls`); without it, the field is not sent.

The tool message's `name` is the called tool's, taken from the call in the conversation unless the message sets `Name`.
Send the ids back exactly as Mistral gave them (nine letters and digits, such as `D681PevKs` in its guide): an id it
did not give can be refused. A conversation without these fields is sent exactly as before.

> **Mistral specifics:** Mistral uses `"any"` (not `"required"`) to force the model to emit a tool call. The adapter passes the value verbatim.

### Structured outputs

```csharp
using Compendium.Adapters.Mistral.StructuredOutputs;

var schema = """
{
  "type":"object",
  "properties":{ "city": {"type":"string"}, "tempC": {"type":"number"} },
  "required":["city","tempC"],
  "additionalProperties": false
}
""";

var request = new CompletionRequest
{
    Model = "mistral-large-latest",
    Messages = new List<Message> { Message.User("Weather in Paris as JSON.") }
}.WithStructuredOutput(schema, schemaName: "Weather");

// or: plain json_object mode (no schema)
request = request.WithJsonMode();
```

Globally opt every request into `response_format: { type: "json_object" }` with `MistralOptions.UseStructuredOutputsByDefault = true`.

### Vision (Pixtral)

```csharp
using Compendium.Adapters.Mistral.Vision;

var request = new CompletionRequest
{
    Model = "pixtral-12b-latest",
    Messages = new List<Message> { Message.User("Describe this image.") }
}.WithImages("https://example.com/cat.jpg");   // or a data:image/...;base64,... URL

var result = await provider.CompleteAsync(request);
```

`WithImages(...)` promotes the **last user message** into a Pixtral-style multimodal content-parts array (`[{type:"text", text}, {type:"image_url", image_url:{url}}]`). If no user message exists, a fresh one is appended.

### Cancellation

Every async surface accepts a `CancellationToken`. The adapter:

- re-throws `OperationCanceledException` when the caller cancelled, so callers can `await` cleanly;
- maps non-caller cancellations (timeouts) into `Result.Failure(AIErrors.Timeout(...))`;
- aborts streaming loops on cancellation between SSE chunks.

### Error mapping

| HTTP | `Error.Code` |
|---|---|
| 401 | `AI.InvalidApiKey` |
| 402 | `AI.InsufficientCredits` |
| 404 | `AI.ModelNotFound` |
| 429 | `AI.RateLimitExceeded` |
| 5xx / other | `AI.ProviderError` |
| Timeout (non-cancellation) | `AI.Timeout` |

Mistral returns two error envelope shapes and the adapter parses both:

```jsonc
// OpenAI-compat shape:
{ "error": { "message": "...", "code": "...", "type": "..." } }

// Native Mistral shape (most common from the platform today):
{ "message": "..." | { "detail": "..." }, "type": "...", "code": "..." }
```

## Observability

`MistralOptions.EnableLogging = true` emits the raw request/response bodies at `Debug` level via the injected `ILogger<MistralHttpClient>`. **Keep it off in production** — payloads may contain user PII (the very thing you picked Mistral for keeping inside the EU).

## Production checklist

- [ ] **EU region**: `https://api.mistral.ai` resolves to a French (eu-west) endpoint by default — TLS handled by the runtime. Document this in your tenant DPA.
- [ ] **API key rotation**: store `Mistral:ApiKey` in your Compendium secret store; rotate quarterly. Mistral supports listing+revoking keys via the platform UI.
- [ ] **Resilience**: `AddStandardResilienceHandler` covers retries, jitter, and total-attempts budget. Increase `TimeoutSeconds` only after confirming Mistral isn't rate-limiting you.
- [ ] **PII**: leave `EnableLogging = false` in production unless debugging a specific tenant under a DPA carve-out.
- [ ] **Pricing**: `mistral-large-latest` is the priciest tier — gate it behind a tenant feature flag and fall back to `mistral-small-latest` for non-premium workloads.

## Sample

[`samples/01-eu-chat-with-pixtral`](samples/01-eu-chat-with-pixtral) wires up a Pixtral vision call and prints the EU-hosted reply. Run with `MISTRAL_API_KEY=... dotnet run --project samples/01-eu-chat-with-pixtral`.

## Integration tests

Live tests live in [`tests/Integration/Compendium.Adapters.Mistral.IntegrationTests`](tests/Integration). They:

- skip automatically when `MISTRAL_API_KEY` is unset;
- are excluded from the default CI run via `--filter "FullyQualifiedName!~IntegrationTests"`;
- hit `mistral-small-latest` / `mistral-embed` so they cost cents per full run.

```bash
export MISTRAL_API_KEY=...
dotnet test -c Release --filter "FullyQualifiedName~IntegrationTests"
```

## SDK choice (ADR)

Decision: **hand-rolled HttpClient + System.Text.Json**, no Mistral official SDK dependency.

Reasons:

- Mirrors the proven `Compendium.Adapters.OpenAI` and `Compendium.Adapters.Deepseek` patterns.
- Lets the framework's pinned `Microsoft.Extensions.Http.Resilience` pipeline handle retries / circuit breaking.
- Mistral's wire format is small, stable, and OpenAI-compat at `/v1/chat/completions` and `/v1/embeddings` — the only real divergence is the dual error envelope, which we handle inline.

## Out of scope (first preview)

- FIM (fill-in-the-middle) `/v1/fim/completions` — Codestral-specific, separate API shape; deferred to v1.1.
- Agents API (`/v1/agents`) — Mistral's bespoke agent orchestration; deferred to v1.1.
- Custom embeddings `dimensions` parameter — not exposed by `Compendium.Abstractions.AI` 1.0.1's `EmbeddingRequest`. Will follow when the abstraction adds it.

## License

MIT — same as Compendium itself.
