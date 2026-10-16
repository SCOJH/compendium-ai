# Changelog

All notable changes to the Compendium AI packages (`Compendium.Abstractions.AI` and the `Compendium.Adapters.*` AI
adapters) are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions
follow the `v*` tags. Earlier versions are described in the generated notes of the
[GitHub Releases](https://github.com/SCOJH/compendium-ai/releases).

## [Unreleased]

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
