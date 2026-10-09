# `compendium-adapter-anthropic`

[Anthropic](https://www.anthropic.com/) (Claude) AI provider adapter for the [Compendium](https://github.com/sassy-solutions/compendium) event-sourcing framework. Implements `IAIProvider` from `Compendium.Abstractions.AI` against Anthropic's [Messages API](https://docs.anthropic.com/en/api/messages) — synchronous + streaming (SSE), with typed error mapping and opt-in prompt caching.

Built from [`template-compendium-adapter-dotnet`](https://github.com/sassy-solutions/template-compendium-adapter-dotnet) per [ADR-0006](https://github.com/sassy-solutions/compendium/blob/main/docs/adr/0006-multi-repo-adapter-split.md) (one adapter, one repo).

## Install

```bash
dotnet add package Compendium.Adapters.Anthropic
```

```csharp
// From IConfiguration (binds the "Anthropic" section)
services.AddCompendiumAnthropic(builder.Configuration);

// Or inline
services.AddCompendiumAnthropic(o =>
{
    o.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!;
    o.DefaultModel = "claude-3-7-sonnet-latest";
    o.EnablePromptCaching = true;
});
```

Then inject `IAIProvider`:

```csharp
public sealed class MyService(IAIProvider ai)
{
    public Task<Result<CompletionResponse>> SummariseAsync(string text, CancellationToken ct) =>
        ai.CompleteAsync(new CompletionRequest
        {
            Model = "claude-3-5-haiku-latest",
            SystemPrompt = "You are a concise summariser.",
            Messages = new List<Message> { Message.User(text) },
        }, ct);
}
```

A runnable end-to-end demo (sync + streaming, prompt caching enabled) lives in [`samples/01-chat`](samples/01-chat/Program.cs).

## Configuration

| Option | Default | Notes |
|---|---|---|
| `ApiKey` | _(required)_ | Sent as the `x-api-key` header. Read from a secret store — **never** hard-code. |
| `BaseUrl` | `https://api.anthropic.com` | Override only for proxies or contract tests. |
| `AnthropicVersion` | `2023-06-01` | Sent as the `anthropic-version` header. Bump when consuming newer beta features. |
| `WorkspaceId` | _(none)_ | Sent as `anthropic-workspace-id` on every request when set. Required for a key that can act on several workspaces; leave empty for a single-workspace key (if sent, it must match). Console › Settings › Workspaces, ID column. |
| `DefaultModel` | `claude-3-7-sonnet-latest` | Used when `CompletionRequest.Model` is null/empty. |
| `DefaultMaxTokens` | `4096` | Anthropic requires `max_tokens` on every request — the adapter always sends one. |
| `TimeoutSeconds` | `120` | Wraps the typed HttpClient timeout. |
| `EnableLogging` | `false` | When `true`, raw request/response JSON is logged at `Debug`. Keep off in prod (prompts may carry PII). |
| `EnablePromptCaching` | `false` | When `true`, the adapter adds `cache_control: { type: "ephemeral" }` to the system-prompt block. See *Prompt caching* below. |

Bind from configuration under the `Anthropic` section:

```json
{
  "Anthropic": {
    "ApiKey": "sk-ant-...",
    "DefaultModel": "claude-3-7-sonnet-latest",
    "EnablePromptCaching": true
  }
}
```

## Multi-workspace keys

A key that belongs to one workspace always acts in it: leave `WorkspaceId` empty. A key that can act on several
workspaces (a personal or service-account key not scoped to one workspace) must name the workspace of each request;
Anthropic answers `400 invalid_request_error` without it. Set `WorkspaceId` and the adapter sends it as the
`anthropic-workspace-id` header on every request, Messages and Models alike:

```json
{
  "Anthropic": {
    "ApiKey": "sk-ant-...",
    "WorkspaceId": "wrkspc_..."
  }
}
```

The id starts with `wrkspc_`; it is the **ID** column of Settings › Workspaces in the Claude Console. The adapter does
not check its format: Anthropic does (`400` for a malformed id, `404` for a workspace the key cannot reach). See
[Select a workspace](https://platform.claude.com/docs/en/manage-claude/authentication#select-a-workspace).

## Prompt caching (preview)

When `EnablePromptCaching = true`, the adapter emits a `cache_control: { type: "ephemeral" }` marker on the trailing system-prompt block of every request. Anthropic then caches that prefix for ~5 minutes (the *ephemeral* tier), so repeat calls with the same system prompt only pay the input-token cost for the **delta** (typically the user message + any tools).

This is most useful when:

- Your system prompt is large (≥1024 tokens for Sonnet, ≥2048 for Haiku) **and** stable across calls.
- You are doing high-frequency turns over the same persona/context.

Models that currently support prompt caching include the Claude 3.5/3.7/4.x Sonnet and Opus families. See [Anthropic's prompt-caching docs](https://docs.anthropic.com/en/docs/build-with-claude/prompt-caching) for the authoritative list and pricing. The adapter writes the marker unconditionally when the option is on; Anthropic silently ignores it on unsupported models.

## What's in scope

| Surface | Status |
|---|---|
| Messages API — sync (`CompleteAsync`) | ✅ |
| Messages API — streaming SSE (`StreamCompleteAsync`) | ✅ (parses `message_start` / `content_block_delta` / `message_delta` / `message_stop`) |
| Typed error mapping (auth / rate / payment / model-not-found / 5xx) | ✅ |
| Cancellation tokens honoured end-to-end | ✅ |
| Prompt caching markers (opt-in) | ✅ |
| Model catalog (`ListModelsAsync`) | ✅ — live `GET /v1/models`, every page; `Metadata["created_at"]` (DateTimeOffset, UTC) and `Metadata["line"]`; errors returned as-is (a 404 of `/v1/models`, such as an unknown workspace, is `AI.ProviderError`) |
| Workspace selection (`WorkspaceId`) | ✅ (`anthropic-workspace-id` on every request) |
| Health check (`HealthCheckAsync`) | ✅ (1-token probe against the default model) |
| Embeddings (`EmbedAsync`) | ❌ — Anthropic does not expose embeddings; the adapter returns `AI.InvalidRequest` so callers can fall back to a dedicated provider. |

## Out of scope (this preview)

- **Provider-native tool / function calling.** `IAIProvider` is text-only; the Compendium agent layer (`Compendium.Application.StandardAgent`) implements tool use through ReAct-style prompt injection, which already works against Claude. Native `tool_use` content blocks are deferred until the abstraction grows a tools surface.
- **Vision / image content blocks.** Same reason — `Message.Content` is `string`. Use a multimodal-aware surface (or a `data:` URI inlined in the message body) until the abstraction expands.
- **Extended thinking** (beta-gated reasoning blocks) — deferred behind a feature flag.
- **Computer use** — out of scope for `v1.0.0-preview`.
- **AWS Bedrock / Vertex AI hosted Claude** — those will live in separate adapters.

## SDK choice

Hand-rolled `HttpClient` + `System.Text.Json`, mirroring [`compendium-adapter-openrouter`](https://github.com/sassy-solutions/compendium-adapter-openrouter). Rationale: avoid churn from official SDK pre-1.0 surfaces, reuse the `Microsoft.Extensions.Http.Resilience` policies already pinned by the framework, and keep the runtime graph minimal.

## Repository conventions

| Aspect | Choice |
|---|---|
| Target | .NET 9, C# 13 |
| Test framework | xUnit 2.9.3 + FluentAssertions 6.12.1 + NSubstitute 5.1.0 |
| HTTP mocking | `RichardSzalay.MockHttp` 7.0.0 |
| Result pattern | `Result<T>` from `Compendium.Core` |
| Coverage | **98.8 %** line / 91.2 % branch (77 tests) — gate at 90 % |
| Test naming | `{SUT}Tests` / `{Method}_{Scenario}_{Expected}` + AAA explicit |

## Build & test locally

```bash
dotnet restore
dotnet build -c Release
dotnet test  -c Release --collect:"XPlat Code Coverage"
```

## Releasing

Tag with a `v` prefix on `main` to publish to nuget.org + GitHub Packages:

```bash
git tag v1.0.0-preview.0
git push origin v1.0.0-preview.0
```

See [`docs/RELEASE.md`](docs/RELEASE.md) for the full release procedure and required secrets.

## License

[MIT](LICENSE) — Copyright © 2026 Sassy Solutions.
