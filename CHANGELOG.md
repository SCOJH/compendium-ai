# Changelog

All notable changes to the Compendium AI packages (`Compendium.Abstractions.AI` and the `Compendium.Adapters.*` AI
adapters) are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions
follow the `v*` tags. Earlier versions are described in the generated notes of the
[GitHub Releases](https://github.com/SCOJH/compendium-ai/releases).

## [Unreleased]

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
