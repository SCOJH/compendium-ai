# Compendium.Adapters.ZAi.Glm

The **GLM dialect** of the Compendium AI adapters: what a GLM model (Z.ai's open-weight family) is and how a request to
it is shaped, whatever host serves it. Pure logic — no HTTP, no endpoint, no `IAIProvider` — depending on
`Compendium.Abstractions.AI` only.

It is used by `Compendium.Adapters.ZAi` (Z.ai's own API), `Compendium.Adapters.Mistral` (`zai-glm-*`) and
`Compendium.Adapters.OpenRouter` (`z-ai/glm-*`). Each of them keeps its own wire format and asks the dialect what the
GLM family needs. Why it is a package of its own, and the wire formats it sits beside:
[`docs/adr/0001-glm-dialect.md`](../../docs/adr/0001-glm-dialect.md).

## What it knows

| | |
|---|---|
| Recognition | `GlmDialect.Recognise("zai-glm-5-3")`, `"glm-5.3"`, `"z-ai/glm-5.3:batch"`, `"~z-ai/glm-latest"`, `"zai-org/GLM-5.3"` → a `GlmModel`; anything else → `null`. Mistral's `zai-glm-5` and the `latest` aliases are GLM 5.3. |
| Capabilities | `AlwaysReasons` (5.3+), `ReasonsByDefault` (4.5+), `TakesReasoningEffort` (5.2+), `ContextWindow` (1 048 576 for 5.2+), `MaxOutputTokens` (131 072 for 5.2+), `Vision` (`v` models, 5.3 Flash / FlashX). |
| Effort | `GlmDialect.EffortFor(model, reasoning)`: low → `low`, medium and high → `high`, xhigh and max → `max`; nothing when no effort is asked, nothing for a GLM before 5.2, never `none`. |
| `max_tokens` | `GlmDialect.MaxTokens(model, requested, reasoning, adapterDefault)`: the answer's room plus room for the reasoning (the caller's `BudgetTokens`, else 4 096 / 16 384 / 32 768 for low / high / max — max when unset), capped at the model's output limit. With or without `ReasoningOptions`: a GLM reasons unasked. |
| Normalisation | `GlmDialect.Chunk(...)`: the answer alone in `ContentDelta`, the reasoning in `ReasoningDelta` only when asked for, `null` for a delta with nothing to pass on. `Surface`, `MapFinishReason` (`sensitive`, `model_context_window_exceeded`, `network_error`, `model_length`), `Describe` (fills a host's `AIModel` where it left values unset). |

The values are the family's, read from Z.ai's, Mistral's and OpenRouter's public documentation on 2026-10-20; a host's
own catalog wins where it says otherwise.

## License

MIT — same as Compendium itself.
