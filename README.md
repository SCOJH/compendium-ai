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

## Releasing

Push a tag `v*` (e.g. `v1.1.0-preview.2`). The Release workflow packs all 13 packages and publishes to
GitHub Packages (primary feed), then to nuget.org (Trusted Publishing, or `NUGET_API_KEY`): with neither
credential that step fails the run, after the GitHub Packages push.

Record the release in [`CHANGELOG.md`](CHANGELOG.md) before tagging.

## Provenance

This repo was assembled from 14 source repositories (framework subtree + 12 single-adapter repos +
scaffold). See [MIGRATION.md](MIGRATION.md) for the component → source repo → SHA table.
