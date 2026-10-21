# Compendium.Adapters.OpenAI

Direct adapter for the **OpenAI REST API** (`https://api.openai.com/v1`) that implements [`IAIProvider`](https://www.nuget.org/packages/Compendium.Abstractions.AI) from `Compendium.Abstractions.AI`.

| What | Value |
|---|---|
| Target framework | net9.0;net10.0 |
| Abstractions pin | `Compendium.Abstractions.AI` 1.0.1 |
| HTTP stack | Typed `HttpClient` + `Microsoft.Extensions.Http.Resilience` (standard pipeline) + `System.Text.Json` |
| Auth | `Authorization: Bearer <ApiKey>`, optional `OpenAI-Organization` / `OpenAI-Project` headers |
| Surface | Chat completions (sync + SSE streaming), embeddings (batched), tool calling, structured outputs |
| Tests | xUnit 2.9 + FluentAssertions + NSubstitute + RichardSzalay.MockHttp |

Sits alongside [`Compendium.Adapters.OpenRouter`](https://github.com/sassy-solutions/compendium-adapter-openrouter) (gateway routing). Use this adapter when you want to talk directly to OpenAI — for latency, billing isolation, or OpenAI-only models (e.g. `text-embedding-3-large`).

## Installation

```bash
dotnet add package Compendium.Adapters.OpenAI
```

## Configuration

Register at startup:

```csharp
services.AddCompendiumOpenAI(opt =>
{
    opt.ApiKey = builder.Configuration["OpenAI:ApiKey"]!;
    opt.DefaultModel = "gpt-4o-mini";
    opt.DefaultEmbeddingModel = "text-embedding-3-small";
});
```

…or bind from `IConfiguration` (section `OpenAI`):

```csharp
services.AddCompendiumOpenAI(builder.Configuration);
```

```jsonc
{
  "OpenAI": {
    "ApiKey": "sk-...",
    "DefaultModel": "gpt-4o-mini",
    "DefaultEmbeddingModel": "text-embedding-3-small",
    "Organization": "org-...",     // optional
    "Project": "proj_...",         // optional
    "TimeoutSeconds": 120,
    "MaxEmbeddingsBatchSize": 2048
  }
}
```

The standard resilience pipeline (`AddStandardResilienceHandler`) is applied automatically; tune retry/timeout behaviour by configuring `OpenAIOptions.TimeoutSeconds` and `RetryAttempts`.

## Usage

### Chat (sync)

```csharp
var provider = sp.GetRequiredService<IAIProvider>();
var result = await provider.CompleteAsync(new CompletionRequest
{
    Model = "gpt-4o-mini",
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

### Embeddings (batched)

`EmbedAsync` chunks the input list into requests no larger than `MaxEmbeddingsBatchSize` (default 2048, OpenAI's hard limit) and aggregates the embeddings, re-mapping indices to the original input order.

```csharp
var result = await provider.EmbedAsync(new EmbeddingRequest
{
    Model = "text-embedding-3-small",
    Inputs = manyTexts
});
```

### Tool calling

```csharp
using Compendium.Adapters.OpenAI.Tools;
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
    Model = "gpt-4o-mini",
    Messages = new List<Message> { Message.User("What's the weather in Paris?") }
}.WithTools(tools, toolChoice: "auto");

var result = await provider.CompleteAsync(request);
foreach (var call in result.Value.GetToolCalls())
{
    Console.WriteLine($"{call.ToolName}({call.ArgumentsJson})");
}
```

Tool definitions are serialised into OpenAI's `tools[]` array; the assistant's `tool_calls` are surfaced as `IReadOnlyList<AgentToolInvocation>` via `CompletionResponse.GetToolCalls()`. `ResultText` / `Latency` on each invocation are left empty/zero — your tool registry fills them in when it executes the call.

### Structured outputs

```csharp
using Compendium.Adapters.OpenAI.StructuredOutputs;

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
    Model = "gpt-4o-mini",
    Messages = new List<Message> { Message.User("Weather in Paris as JSON.") }
}.WithStructuredOutput(schema, schemaName: "Weather");

// or: plain json_object mode (no schema)
request = request.WithJsonMode();
```

Globally opt every request into `response_format: { type: "json_object" }` with `OpenAIOptions.UseStructuredOutputsByDefault = true`.

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

## Observability

`OpenAIOptions.EnableLogging = true` emits the raw request/response bodies at `Debug` level via the injected `ILogger<OpenAIHttpClient>`. Keep it off in production — payloads can contain user PII.

## Sample

[`samples/01-chat-with-tool`](samples/01-chat-with-tool) wires up a 1-tool agent and dumps the model's tool-call decision. Run with `OPENAI_API_KEY=sk-... dotnet run --project samples/01-chat-with-tool`.

## Integration tests

Live tests live in [`tests/Integration/Compendium.Adapters.OpenAI.IntegrationTests`](tests/Integration). They:

- skip automatically when `OPENAI_API_KEY` is unset;
- are excluded from the default CI run via `--filter "FullyQualifiedName!~IntegrationTests"`;
- hit `gpt-4o-mini` / `text-embedding-3-small` so they cost cents per full run.

```bash
export OPENAI_API_KEY=sk-...
dotnet test -c Release --filter "FullyQualifiedName~IntegrationTests"
```

## SDK choice (ADR)

Decision: **hand-rolled HttpClient + System.Text.Json**, no `OpenAI` official SDK dependency.

Reasons:

- Avoids the heavy transitive graph and rapid version churn of the official SDK.
- Lets the framework's pinned `Microsoft.Extensions.Http.Resilience` pipeline handle retries / circuit breaking.
- Mirrors the proven `Compendium.Adapters.OpenRouter` pattern (98.7 % coverage).
- The wire format is small and stable — chat / embeddings / tools / `response_format`.

If the official SDK stabilises (sane transitive graph, stable type surface), we may pivot — document any switch with an updated ADR in `docs/`.

## Out of scope (first preview)

- Realtime API, Assistants API, fine-tuning, batch API.
- Image generation / vision — separate `Compendium.Adapters.OpenAI.Images` package if/when needed.
- Azure OpenAI — separate adapter (different auth and URL shape).

## License

MIT — same as Compendium itself.
