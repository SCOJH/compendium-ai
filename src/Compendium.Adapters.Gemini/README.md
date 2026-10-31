# Compendium.Adapters.Gemini

Direct adapter for the **Google Gemini REST API** (`https://generativelanguage.googleapis.com`) that implements [`IAIProvider`](https://www.nuget.org/packages/Compendium.Abstractions.AI) from `Compendium.Abstractions.AI`.

| What | Value |
|---|---|
| Target framework | net9.0;net10.0 |
| Abstractions pin | `Compendium.Abstractions.AI` 1.0.1 |
| HTTP stack | Typed `HttpClient` + `Microsoft.Extensions.Http.Resilience` (standard pipeline) + `System.Text.Json` |
| Auth | `?key=<ApiKey>` query parameter on every request |
| Surface | Chat completions (sync + SSE streaming), embeddings (single + batched), tool / function calling, structured outputs, system instructions, vision (inline & file-URI) |
| Tests | xUnit 2.9 + FluentAssertions + NSubstitute + RichardSzalay.MockHttp |

Sits alongside [`Compendium.Adapters.OpenAI`](https://github.com/sassy-solutions/compendium-adapter-openai) and [`Compendium.Adapters.Anthropic`](https://github.com/sassy-solutions/compendium-adapter-anthropic). Use this adapter when you want direct, billable access to Google's Gemini models (1.5 Pro, 2.0 Flash, the embedding family, etc.) — for cost, latency, or Gemini-specific capabilities (long context, native multimodal). For Vertex AI Gemini (Google Cloud auth, different endpoints), use the separate `compendium-adapter-vertex-gemini` package once it lands.

## Installation

```bash
dotnet add package Compendium.Adapters.Gemini
```

## Configuration

Register at startup:

```csharp
services.AddCompendiumGemini(opt =>
{
    opt.ApiKey = builder.Configuration["Gemini:ApiKey"]!;
    opt.DefaultModel = "gemini-2.0-flash";
    opt.DefaultEmbeddingModel = "text-embedding-004";
});
```

…or bind from `IConfiguration` (section `Gemini`):

```csharp
services.AddCompendiumGemini(builder.Configuration);
```

```jsonc
{
  "Gemini": {
    "ApiKey": "...",
    "BaseUrl": "https://generativelanguage.googleapis.com",
    "ApiVersion": "v1beta",
    "DefaultModel": "gemini-2.0-flash",
    "DefaultEmbeddingModel": "text-embedding-004",
    "TimeoutSeconds": 120,
    "MaxEmbeddingsBatchSize": 100
  }
}
```

The standard resilience pipeline (`AddStandardResilienceHandler`) is applied automatically; tune retry/timeout behaviour by configuring `GeminiOptions.TimeoutSeconds` and `RetryAttempts`.

## Usage

### Chat (sync)

```csharp
var provider = sp.GetRequiredService<IAIProvider>();
var result = await provider.CompleteAsync(new CompletionRequest
{
    Model = "gemini-2.0-flash",
    SystemPrompt = "Be concise.",
    Messages = new List<Message> { Message.User("Pick a colour.") },
    MaxTokens = 64
});
if (result.IsSuccess) Console.WriteLine(result.Value.Content);
```

`SystemPrompt` is sent as Gemini's top-level `systemInstruction` field — not as a content part — exactly per Google's spec.

### Chat (streaming)

```csharp
await foreach (var chunk in provider.StreamCompleteAsync(request))
{
    if (chunk.IsSuccess) Console.Write(chunk.Value.ContentDelta);
}
```

Implemented via `streamGenerateContent?alt=sse`; the final chunk carries `IsFinal=true`, `FinishReason`, and `Usage` (token counts).

### Embeddings

```csharp
var result = await provider.EmbedAsync(new EmbeddingRequest
{
    Model = "text-embedding-004",
    Inputs = manyTexts,
    Dimensions = 768 // optional — sent as outputDimensionality
});
```

- Single input goes through `embedContent`.
- Multiple inputs are batched through `batchEmbedContents` in chunks of `MaxEmbeddingsBatchSize` (default 100, Gemini's documented cap).

### Tool / function calling

```csharp
using Compendium.Adapters.Gemini.Tools;
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
    Model = "gemini-2.0-flash",
    Messages = new List<Message> { Message.User("What's the weather in Paris?") }
}.WithTools(tools, toolChoice: "auto");

var result = await provider.CompleteAsync(request);
foreach (var call in result.Value.GetToolCalls())
{
    Console.WriteLine($"{call.ToolName}({call.ArgumentsJson})");
}
```

`toolChoice` maps to Gemini's `functionCallingConfig.mode`:

| Value | Mode |
|---|---|
| `"auto"` | `AUTO` |
| `"any"` / `"required"` | `ANY` |
| `"none"` | `NONE` |
| any other string | `ANY` + `allowedFunctionNames: [<name>]` |

To feed a tool result back to the model, send a `Message` with `Role = MessageRole.Tool` and `Name = "<function_name>"`; the adapter translates it into a `functionResponse` content. The `Content` may be a JSON object (passed through verbatim) or plain text (wrapped as `{"result": "<text>"}`).

### Structured outputs

```csharp
using Compendium.Adapters.Gemini.StructuredOutputs;

var schema = """
{
  "type":"object",
  "properties":{ "city": {"type":"string"}, "tempC": {"type":"number"} },
  "required":["city","tempC"]
}
""";

var request = new CompletionRequest
{
    Model = "gemini-2.0-flash",
    Messages = new List<Message> { Message.User("Weather in Paris as JSON.") }
}.WithStructuredOutput(schema);

// or: plain JSON mode (no schema)
request = request.WithJsonMode();
```

Schema mode sets `responseMimeType: "application/json"` + `responseSchema`. JSON mode sets only the mime type. Globally opt every request into JSON mode with `GeminiOptions.UseStructuredOutputsByDefault = true`.

The provider-agnostic way is `CompletionRequest.ResponseSchema` (`new ResponseSchema { Name = "Weather", SchemaJson = schema }`):
the same request on every adapter, and `IAIProvider.SupportsJsonSchema(model)` to ask first. Here it goes as
`responseMimeType: "application/json"` + `responseJsonSchema`, which takes JSON Schema (the key above sends
`responseSchema`, Gemini's OpenAPI subset), from Gemini 2.0. It takes precedence over the keys above. See the repository
README, "Structured output".

### Vision (images & files)

`CompletionRequest` has no first-class image field, so attach a list of pre-built `GeminiPart` entries under the `gemini.vision_parts` key in `AdditionalParameters`:

```csharp
using Compendium.Adapters.Gemini.Http.Models;

var visionParts = new List<GeminiPart>
{
    new() { InlineData = new GeminiInlineData { MimeType = "image/png", Data = Convert.ToBase64String(bytes) } },
    new() { FileData = new GeminiFileData { MimeType = "image/jpeg", FileUri = "https://files.example/img.jpg" } }
};

var request = new CompletionRequest
{
    Model = "gemini-2.0-flash",
    Messages = new List<Message> { Message.User("Describe the images.") },
    AdditionalParameters = new Dictionary<string, object>
    {
        ["gemini.vision_parts"] = (IReadOnlyList<GeminiPart>)visionParts
    }
};
```

These parts are appended to the last user content. Files larger than ~20 MB should be uploaded via the Files API and referenced by `fileUri`.

### Cancellation

Every async surface accepts a `CancellationToken`. The adapter:

- re-throws `OperationCanceledException` when the caller cancelled, so callers can `await` cleanly;
- maps non-caller cancellations (timeouts) into `Result.Failure(AIErrors.Timeout(...))`;
- aborts streaming loops on cancellation between SSE chunks.

### Error mapping

| HTTP | `Error.Code` |
|---|---|
| 401 / 403 | `AI.InvalidApiKey` |
| 402 | `AI.InsufficientCredits` |
| 404 | `AI.ModelNotFound` |
| 429 | `AI.RateLimitExceeded` |
| 5xx / other | `AI.ProviderError` |
| Timeout (non-cancellation) | `AI.Timeout` |

Gemini's structured error body (`{ "error": { "code", "message", "status" } }`) is parsed when present and surfaced through the `Error` payload.

## Observability

`GeminiOptions.EnableLogging = true` emits the raw request/response bodies at `Debug` level via the injected `ILogger<GeminiHttpClient>`. Keep it off in production — payloads can contain user PII and the API key is on the URL.

## Sample

[`samples/01-chat-with-tool`](samples/01-chat-with-tool) wires up a 1-tool agent and dumps the model's tool-call decision. Run with `GEMINI_API_KEY=... dotnet run --project samples/01-chat-with-tool`.

## Integration tests

Live tests live in [`tests/Integration/Compendium.Adapters.Gemini.IntegrationTests`](tests/Integration). They:

- skip automatically when `GEMINI_API_KEY` is unset;
- are excluded from the default CI run via `--filter "FullyQualifiedName!~IntegrationTests"`;
- hit `gemini-2.0-flash` / `text-embedding-004` so they cost cents per full run.

```bash
export GEMINI_API_KEY=...
dotnet test -c Release --filter "FullyQualifiedName~IntegrationTests"
```

## SDK choice (ADR)

Decision: **hand-rolled HttpClient + System.Text.Json**, no `Mscc.GenerativeAI` (community SDK) dependency.

Reasons:

- Avoids the version churn and indirect-deps surface of a community SDK that doesn't track the official spec.
- Lets the framework's pinned `Microsoft.Extensions.Http.Resilience` pipeline handle retries / circuit breaking.
- Mirrors the proven `Compendium.Adapters.OpenAI` / `Compendium.Adapters.Anthropic` patterns.
- Gemini's REST surface is small and stable — `generateContent`, `streamGenerateContent`, `embedContent`, `batchEmbedContents`, `models`.

If Google ships an official, stable .NET SDK with a sane transitive graph, we may pivot — document any switch with an updated ADR in `docs/`.

## Out of scope (first preview)

- **Vertex AI Gemini** — different endpoints + Google Cloud OAuth — separate `Compendium.Adapters.VertexGemini` package.
- **Live API** (low-latency voice / multimodal streaming) — separate adapter.
- **Code execution tool** — feature-flagged, v2.
- **Caching API** (`cachedContents`) — v2.

## License

MIT — same as Compendium itself.
