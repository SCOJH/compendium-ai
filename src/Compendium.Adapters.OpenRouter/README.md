# `compendium-adapter-openrouter`

[OpenRouter](https://openrouter.ai/) AI provider adapter for the [Compendium](https://github.com/sassy-solutions/compendium) event-sourcing framework. Implements `IAIProvider` from `Compendium.Abstractions.AI` to give access to 100+ LLM models (Claude, GPT-4, Llama, Mistral, etc.) through a single OpenAI-compatible API.

Extracted from `sassy-solutions/compendium` per [ADR-0006](https://github.com/sassy-solutions/compendium/blob/main/docs/adr/0006-multi-repo-adapter-split.md) (multi-repo adapter split). Built from [`template-compendium-adapter-dotnet`](https://github.com/sassy-solutions/template-compendium-adapter-dotnet).

## Install

```bash
dotnet add package Compendium.Adapters.OpenRouter
```

```csharp
services.AddOpenRouter(builder.Configuration.GetSection("OpenRouter"));
```

See [`docs/README.md`](docs/README.md) for full configuration, model routing, fallback behaviour, and cost optimisation.

## GLM models (`z-ai/glm-*`)

The GLM models go through the GLM dialect (`Compendium.Adapters.ZAi.Glm`, shared with the Z.ai and Mistral adapters —
[`docs/adr/0001-glm-dialect.md`](../../docs/adr/0001-glm-dialect.md)): with `CompletionRequest.Reasoning`,
`reasoning: {effort, exclude}` (`low` / `high` / `max` for GLM 5.3, `high` / `xhigh` for GLM 5.2, the efforts the
catalog lists; never `effort: "none"`, GLM 5.3's reasoning is mandatory);
`max_tokens` with room for the reasoning, with or without it; `reasoning` / `reasoning_details` read into
`ReasoningDelta` / `Reasoning` when asked for, never into the answer; `reasoning_tokens` into `UsageStats.ReasoningTokens`;
and the usage frame OpenRouter sends after the last content chunk read into the final chunk. Every other model is sent
and read exactly as before.

## Versioning

This package continues the version sequence of `Compendium.Adapters.OpenRouter` originally published from the framework monorepo (last framework-published version: `1.0.0-preview.8`). The first release from this repo is `v1.0.0-preview.9`. Versions are driven by git tags via [MinVer](https://github.com/adamralph/minver) — see [`docs/RELEASE.md`](docs/RELEASE.md).

## Repository conventions

| Aspect | Choice |
|---|---|
| Target | .NET 9 and .NET 10 (`net9.0;net10.0`), C# 13 |
| Test framework | xUnit 2.9.3 + FluentAssertions 6.12.1 + NSubstitute 5.1.0 |
| Coverage | currently **98.77 %** line / 93.23 % branch (63 tests) — gate at 90 % |
| HTTP mocking | `RichardSzalay.MockHttp` 7.0.0 |
| Result pattern | `Result<T>` from `Compendium.Core` |
| Test naming | `{SUT}Tests` / `{Method}_{Scenario}_{Expected}` + AAA explicit |

## Build & test locally

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release --collect:"XPlat Code Coverage"
```

## Releasing

Tag with a `v` prefix on `main` to publish to nuget.org + GitHub Packages:

```bash
git tag v1.0.0-preview.10
git push origin v1.0.0-preview.10
```

See [`docs/RELEASE.md`](docs/RELEASE.md) for the full release procedure and required secrets.

## License

[MIT](LICENSE) — Copyright © 2026 Sassy Solutions.
