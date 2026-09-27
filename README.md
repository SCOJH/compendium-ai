# Compendium AI Domain

The **ai** domain repo of the Compendium framework (repo-per-domain topology, ADR-0007). It hosts the
`Compendium.Abstractions.AI` abstraction and all 12 AI provider adapters in a single repository, so a change
to the abstraction and its adapters ships as **one atomic PR** and **one release tag**.

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

Package IDs are unchanged from their source repositories — consumers only bump the version.

## Layout

```
src/Compendium.Abstractions.AI/     # the domain abstraction (packable)
src/Compendium.Adapters.*/          # one project per provider adapter (packable)
tests/Unit/*                        # unit tests (CI-gated, ≥90% line coverage)
tests/Integration/*                 # integration tests (network/Docker-gated, excluded from CI)
```

## Build & test

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release --filter "FullyQualifiedName!~IntegrationTests"
```

Integration tests (`tests/Integration/*`) require live provider credentials and/or Docker
(Ollama uses Testcontainers) and are skipped in CI.

## Reasoning channel (next: `v1.1.0-preview.5`, not released)

`CompletionRequest.Reasoning` (`ReasoningOptions`: `Effort` low…max, `BudgetTokens`, `IncludeReasoning`) asks the model for
its own reasoning. Unset, every request is the one it was. The reasoning comes back apart from the answer —
`CompletionChunk.ReasoningDelta` / `CompletionResponse.Reasoning`, only when asked for — and `UsageStats.ReasoningTokens`
when the provider counts them. Never send it back as a conversation turn.

| Adapter | On the wire |
|---|---|
| Anthropic | Opus/Sonnet 4.6+, Fable, Mythos: `thinking: {type: "adaptive", display}` + `output_config.effort` (`xhigh` from Opus 4.7; a budget is refused there, so it is added to `max_tokens` as headroom). Earlier 4.x and Sonnet 3.7: `thinking: {type: "enabled", budget_tokens}` (≥ 1 024), `max_tokens` raised by it. No temperature, no top_p with thinking. Older models: unchanged. `thinking_delta` → `ReasoningDelta`. |
| OpenAI | `reasoning_effort`, `max_completion_tokens` instead of `max_tokens`, no temperature / top_p / penalties; `reasoning_tokens` → `ReasoningTokens`. |
| Mistral | `reasoning_effort: "high"`; `thinking` content chunks (a list, where the answer is a string) → `ReasoningDelta` / `Reasoning`. |
| DeepSeek | `reasoning_content` → `ReasoningDelta` / `Reasoning` (unasked, dropped as before; `InlineReasoningInContent` unchanged). |
| Others | ignored: the request is sent without it. |

The version is set by the tag (MinVer): this lands as `v1.1.0-preview.5` when that tag is pushed.

## Releasing

Push a tag `v*` (e.g. `v1.1.0-preview.2`). The Release workflow packs all 13 packages and publishes to
GitHub Packages (primary feed), then to nuget.org (Trusted Publishing, or `NUGET_API_KEY`): with neither
credential that step fails the run, after the GitHub Packages push.

## Provenance

This repo was assembled from 14 source repositories (framework subtree + 12 single-adapter repos +
scaffold). See [MIGRATION.md](MIGRATION.md) for the component → source repo → SHA table.
