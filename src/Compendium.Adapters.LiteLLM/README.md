# Compendium.Adapters.LiteLLM

[![NuGet](https://img.shields.io/nuget/v/Compendium.Adapters.LiteLLM.svg)](https://www.nuget.org/packages/Compendium.Adapters.LiteLLM)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

`IAIProvider` adapter for the [LiteLLM](https://github.com/BerriAI/litellm) proxy — a
self-hostable gateway that exposes 100+ LLM providers behind an OpenAI-compatible REST API.

LiteLLM lets you put **one** endpoint in front of OpenAI, Anthropic, Bedrock, Azure OpenAI,
Vertex / Gemini, Cohere, Mistral, Ollama, and dozens more, plus its own routing primitives:
budget caps, virtual keys per team, per-model rate limits, semantic caching, and request tagging.
This adapter speaks that endpoint from `Compendium`.

## Why LiteLLM (vs OpenRouter, vs direct provider adapters)

| Question | LiteLLM (this adapter) | OpenRouter | Direct adapters |
|---|---|---|---|
| Where does the gateway run? | **You host it** (local / k8s) | Vendor-hosted SaaS | No gateway |
| Cost model | Free + your provider costs | Markup on every token | Provider list price |
| Virtual keys / team budgets | Yes (built in) | Limited | No |
| Custom routing / fallbacks | Yes (config-driven) | Limited | No |
| Egress / data residency control | You own it | Egress via OpenRouter | Direct to provider |
| Spin-up time | `pip install litellm[proxy]` | Sign up | Per-provider keys |

Pick **this adapter** when you want self-hostable multi-provider routing with first-class observability hooks. Pick `compendium-adapter-openrouter` when you'd rather not host anything yourself. Pick a direct adapter (`compendium-adapter-openai`, `-anthropic`, …) when you need just one provider with no gateway in the path.

## Quick start

### 1. Run a LiteLLM proxy

```bash
pip install "litellm[proxy]"

# Single-model dev mode (uses your real provider API keys)
export OPENAI_API_KEY=sk-...
export ANTHROPIC_API_KEY=sk-ant-...
litellm --model gpt-4o-mini --model claude-haiku-4.5
# Listening on http://localhost:4000
```

For production, point the proxy at a config file with virtual keys, fallbacks, rate limits, and
observability. See [LiteLLM docs](https://docs.litellm.ai/docs/proxy/configs).

### 2. Install the adapter

```bash
dotnet add package Compendium.Adapters.LiteLLM
```

### 3. Register the provider

```csharp
using Compendium.Adapters.LiteLLM.DependencyInjection;

services.AddCompendiumLiteLLM(opt =>
{
    opt.BaseUrl     = "http://localhost:4000";        // your proxy
    opt.VirtualKey  = "sk-litellm-team-eng";          // optional team key
    opt.DefaultModel = "openai/gpt-4o-mini";          // provider-prefixed
});
```

…or bind from configuration:

```jsonc
{
  "LiteLLM": {
    "BaseUrl": "https://litellm.internal/v1",
    "VirtualKey": "sk-litellm-team-eng",
    "DefaultModel": "openai/gpt-4o-mini",
    "PassThroughHeaders": {
      "x-litellm-tags": "env=prod,team=ai"
    }
  }
}
```

```csharp
services.AddCompendiumLiteLLM(builder.Configuration);
```

### 4. Use it

```csharp
public sealed class AssistantService(IAIProvider ai)
{
    public async Task<string> AnswerAsync(string question, CancellationToken ct)
    {
        var result = await ai.CompleteAsync(new CompletionRequest
        {
            Model = "anthropic/claude-haiku-4.5", // route this call to Anthropic
            Messages = new() { Message.User(question) },
            MaxTokens = 512
        }, ct);

        return result.IsSuccess
            ? result.Value.Content
            : $"AI error: {result.Error.Code}";
    }
}
```

The same `IAIProvider` instance can hit `openai/gpt-4o-mini`, `anthropic/claude-sonnet-4`,
`bedrock/anthropic.claude-3-haiku`, `gemini/gemini-1.5-pro` — anything your proxy is configured
for. Routing happens server-side; your code only changes the model id.

See [`samples/01-multi-provider-routing`](samples/01-multi-provider-routing) for a runnable demo.

## Configuration reference

| Option | Default | Notes |
|---|---|---|
| `BaseUrl` | `http://localhost:4000` | Proxy base URL. |
| `ApiKey` | `""` | Plain bearer credential. Optional — many self-hosted proxies run open. |
| `VirtualKey` | `null` | LiteLLM virtual key (`sk-litellm-...`). Overrides `ApiKey` on the Authorization header. |
| `DefaultModel` | `openai/gpt-4o-mini` | Provider-prefixed model id, applied when the request omits `Model`. |
| `DefaultEmbeddingModel` | `openai/text-embedding-3-small` | Same convention for embeddings. |
| `DefaultMaxTokens` | `4096` | Applied when the request omits `MaxTokens`. |
| `TimeoutSeconds` | `120` | Per-request HTTP timeout. |
| `RetryAttempts` | `3` | Used by the standard resilience handler. |
| `EnableLogging` | `false` | Emits raw request/response JSON at debug level (do NOT enable in prod). |
| `UseStructuredOutputsByDefault` | `false` | Forces `response_format = {"type":"json_object"}` on every call. |
| `MaxEmbeddingsBatchSize` | `2048` | Inputs are chunked into batches no larger than this. |
| `PassThroughHeaders` | `{}` | Case-insensitive map of extra headers forwarded on every outbound request (e.g. `x-litellm-cache-key`, `x-litellm-tags`). |

### Pass-through headers — common LiteLLM primitives

| Header | What it does |
|---|---|
| `x-litellm-cache-key` | Forces a cache key the proxy uses to dedupe identical prompts. |
| `x-litellm-tags` | Tags this request in proxy logs / billing (`env=prod,team=ai`). |
| `x-user-id` | Per-user budget accounting on the proxy side. |
| Custom auth (mTLS proxies, internal ingress) | Anything your gateway expects. |

The pass-through bag is case-insensitive and is **silently overridden** by the configured
`ApiKey` / `VirtualKey` for the `Authorization` header — you can't shoot yourself in the foot by
clobbering credentials from configuration.

## Tool / function calling

```csharp
using Compendium.Adapters.LiteLLM.Tools;

var tools = new List<AgentTool>
{
    new("get_weather", "Returns the weather for a city.",
        """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""")
};

var request = new CompletionRequest
{
    Model = "openai/gpt-4o-mini",
    Messages = new() { Message.User("What's the weather in Paris?") }
}.WithTools(tools, toolChoice: "auto");

var result = await ai.CompleteAsync(request);

foreach (var call in result.Value.GetToolCalls())
{
    Console.WriteLine($"{call.ToolName}({call.ArgumentsJson})");
}
```

Whether a given upstream model honours the tool list depends on the model. The LiteLLM proxy
passes the OpenAI-style `tools` / `tool_choice` fields straight through.

## Structured outputs

```csharp
using Compendium.Adapters.LiteLLM.StructuredOutputs;

var schema = """
{ "type":"object","properties":{"answer":{"type":"string"}},"required":["answer"] }
""";

var result = await ai.CompleteAsync(
    new CompletionRequest { Model = "openai/gpt-4o-mini", Messages = [...] }
        .WithStructuredOutput(schema, schemaName: "Answer", strict: true));
```

…or plain JSON mode:

```csharp
var result = await ai.CompleteAsync(request.WithJsonMode());
```

The provider-agnostic way is `CompletionRequest.ResponseSchema` (`new ResponseSchema { Name = "Weather", SchemaJson = schema }`):
the same `response_format.json_schema` here, the same request on every adapter, and `IAIProvider.SupportsJsonSchema(model)` to ask first. It
takes precedence over the keys above. See the repository README, "Structured output".

## Production checklist

- [ ] **TLS termination** — never run the proxy on plain HTTP across a public network. Front it
  with NGINX / Envoy / your cloud LB.
- [ ] **Virtual-key rotation** — rotate `sk-litellm-...` keys per team on a schedule. They live in
  your proxy DB; the adapter just forwards them as `Authorization: Bearer`.
- [ ] **Observability** — turn on LiteLLM's Langfuse / Helicone / Prometheus hooks server-side.
  Do NOT set `EnableLogging = true` in prod — that dumps raw prompts to your app logs.
- [ ] **Per-tenant tagging** — set `x-litellm-tags` via `PassThroughHeaders` so per-tenant spend
  is queryable on the proxy.
- [ ] **Budget caps** — configure proxy-side budget limits per virtual key. The adapter surfaces
  402 / `AI.InsufficientCredits` when a key is over budget.
- [ ] **Fallbacks** — define provider fallbacks (`openai/gpt-4o-mini` → `azure/gpt-4o-mini`) in
  the proxy config; the adapter is unaware and benefits automatically.

## Error mapping

| HTTP | Error code | Notes |
|---|---|---|
| 401 | `AI.InvalidApiKey` | Wrong `ApiKey` / `VirtualKey`. |
| 402 | `AI.InsufficientCredits` | Proxy-side budget exhausted. |
| 404 | `AI.ModelNotFound` | Model id not declared in the proxy config. |
| 429 | `AI.RateLimitExceeded` | Per-key or per-model rate limit. |
| 5xx | `AI.ProviderError` | Upstream failure surfaced by the proxy. |
| timeout | `AI.Timeout` | Caller-cancelled `CancellationToken` rethrows; HttpClient timeout maps. |

Caller cancellation always re-throws `TaskCanceledException` so the surrounding code knows the
shutdown wasn't a server-side fault.

## Development

```bash
dotnet build  -c Release
dotnet test   -c Release --filter "FullyQualifiedName!~IntegrationTests"
```

CI gate: ≥ 90 % line coverage. The unit tests use `RichardSzalay.MockHttp` — no LiteLLM proxy
required to run them. The optional integration tests (skipped when `LITELLM_BASE_URL` is unset)
exercise the real proxy.

## License

MIT — same as Compendium itself.
