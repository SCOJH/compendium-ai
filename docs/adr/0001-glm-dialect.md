# 0001. One GLM dialect, shared by every host that serves GLM

* Status: Proposed
* Date: 2026-10-20
* Deciders: @SCOJH maintainers

## Context

GLM, Z.ai's open-weight model family, is served by several hosts this repository already has adapters for, and by Z.ai
itself: Mistral (`zai-glm-5-3`, "served without Mistral modifications"), OpenRouter (`z-ai/glm-5.3`), Z.ai's own API
(`glm-5.3`). The same model should behave the same whichever adapter reaches it.

Two kinds of knowledge are involved, and they do not overlap.

**The wire format, which is each host's.** The same reasoning arrives three different ways:

| | Mistral | Z.ai | OpenRouter |
|---|---|---|---|
| Ask for reasoning | always on; `reasoning_effort`, no `thinking` field | `thinking: {type: "enabled"}` (default); `disabled` is an error on 5.3 | `reasoning: {effort, exclude}`; reasoning mandatory for 5.3 |
| Effort, 5.3 | `low` · `high` · `max` (guide; the OpenAPI enum has no `max`) | `low` · `high` · `max`, default `max`; anything else is an error | `low` · `high` · `max`, default `max` (catalog) |
| Effort, 5.2 | not in the guide; the OpenAPI enum is `none` … `xhigh`, no `max` | `max` (default) … `none`; `low` and `medium` run at `high`, `xhigh` at `max` | `xhigh` · `high` · `none`, default `high` (catalog) |
| Reasoning, response | `message.content[]`: `{type: "thinking", thinking: [{type: "text", text}]}` | `message.reasoning_content` (string) | `message.reasoning` (string), `reasoning_details[]` |
| Answer, response | `message.content[]`: `{type: "text", text}` — always a list for GLM | `message.content` (string) | `message.content` (string) |
| Reasoning, stream | `delta.content` = a list with a thinking chunk | `delta.reasoning_content` | `delta.reasoning`, `delta.reasoning_details` |
| Answer, stream | lists of text chunks, or strings after the turn | `delta.content` (string) | `delta.content` (string) |
| Native tool calls | `message.tool_calls`; `tool_choice` auto / none / any / required / named | `message.tool_calls`, `arguments` typed object, sent as a string in the examples; `tool_choice` = `auto` only | `message.tool_calls` (not wired in this adapter) |
| Usage | on the chunk with the finish reason; no reasoning count | on the last chunk; no reasoning count documented | on a trailing frame that repeats the finish reason; `completion_tokens_details.reasoning_tokens` |
| Extra finish reasons | `model_length`, `error` | `sensitive`, `model_context_window_exceeded`, `network_error` | `error` |

Sources, all read on 2026-10-20:

- Mistral — <https://docs.mistral.ai/studio/conversations/reasoning.md> ("`zai-glm-5-3` supports `low`, `high`, and
  `max` — not `none`. Unlike Mistral models, `message.content` is always returned as a list of chunks (thinking + text)
  regardless of the `reasoning_effort` value; there is no plain-string mode"), <https://docs.mistral.ai/models/zai-glm-5-3.md>,
  <https://docs.mistral.ai/openapi.yaml> (`ReasoningEffort`, `ThinkChunk`, `TextChunk`, `DeltaMessage`, `UsageInfo`),
  <https://docs.mistral.ai/studio/conversations/chat-completion.md>.
- Z.ai — <https://docs.z.ai/api-reference/llm/chat-completion.md>, <https://docs.z.ai/guides/capabilities/thinking.md>,
  <https://docs.z.ai/guides/capabilities/streaming.md>, <https://docs.z.ai/api-reference/api-code.md>,
  <https://docs.z.ai/guides/llm/glm-5.3.md>.
- OpenRouter — <https://openrouter.ai/docs/guides/best-practices/reasoning-tokens.md>,
  <https://openrouter.ai/docs/api_reference/streaming.md>, the public catalog `GET https://openrouter.ai/api/v1/models`.

**The behaviour of the GLM family, which is the same everywhere.** GLM 5.3 always reasons and cannot be told not to;
GLM 4.5 to 5.2 reason by default. GLM 5.2 and later take an effort. From 5.3 on the scale is `low` · `high` · `max`,
and Z.ai publishes how the usual five levels map onto it (low → low, medium and high → high, xhigh and max → max).
GLM 5.2 has no `low` of its own: Z.ai runs `low` and `medium` at `high` and `xhigh` at `max`, OpenRouter lists only
`xhigh` · `high` · `none` for `z-ai/glm-5.2`, and Mistral's enum has no `max`; `high` and `xhigh` are the two values
every host takes for it (Z.ai's thinking guide and chat-completion OpenAPI, OpenRouter's catalog, read 2026-10-20). The
reasoning counts against `max_tokens`, so with an adapter default of 4 096 and a model thinking at `max`, the answer
can be left with no room at all (`finish_reason: length`, empty content, billed). A GLM id must be recognised whatever
the host's prefix
(`glm-5.3`, `zai-glm-5-3`, `z-ai/glm-5.3`, `~z-ai/glm-latest`, `zai-org/GLM-5.3`).

The first kind of knowledge cannot be shared: delegating Mistral's or OpenRouter's HTTP to a Z.ai client would send the
wrong request and misread the answer. The second kind is what should be written once.

What made it urgent: before the reasoning channel (#30), the Mistral adapter typed a stream delta's `content` as a
string, so a GLM delta — a list — failed to deserialize and was skipped. A caller that asks the model for its tool
calls as text (`<tool_call>…` in the answer, `</tool_call>` as a stop sequence) lost the start of the answer and the
tool call with it. #30 reads the lists; this decision makes the rest of GLM's behaviour explicit and shared.

## Decision

**A GLM dialect package, `Compendium.Adapters.ZAi.Glm`, with no HTTP and no endpoint, depending on
`Compendium.Abstractions.AI` only.** It holds the family's knowledge as pure functions (`GlmDialect`, `GlmModel`):

- `Recognise` / `TryRecognise`: a GLM id whatever the host's prefix or suffix, its version, tier, vision, whether it
  always reasons, reasons by default, takes an effort, its context window and output limit. Anything else is not GLM.
- `EffortFor`: `ReasoningOptions.Effort` on GLM's scale (`low` / `high` / `max` from 5.3 on; for 5.2, low, medium and
  high → `high`, xhigh and max → `xhigh`), nothing when no effort is asked (the host's default stays), and never `none`.
- `MaxTokens` / `ReasoningHeadroom`: the answer's room (`MaxTokens`, else the adapter's default) plus room for the
  reasoning — the caller's `BudgetTokens`, else 4 096 / 16 384 / 32 768 for low / high / max or xhigh (max, the
  default, when no effort is asked; 16 384 for a GLM that takes no effort) — never above the model's output limit
  (131 072 for 5.2+).
- `Chunk`, `Surface`, `MapFinishReason`, `Describe`: the normalisation. The answer alone in `ContentDelta` / `Content`;
  the reasoning in `ReasoningDelta` / `Reasoning` only when asked for, never in the answer; a delta with nothing to pass
  on is skipped (so the same logical stream reads as the same chunks on every host); GLM's extra finish reasons; a host's
  model description completed where it left a value unset.

**Three adapters use it, each keeping its own wire format:**

```
Compendium.Adapters.ZAi        ─┐
Compendium.Adapters.Mistral    ─┼─► Compendium.Adapters.ZAi.Glm ─► Compendium.Abstractions.AI
Compendium.Adapters.OpenRouter ─┘
(each adapter still references Compendium.Abstractions.AI directly; no adapter references another)
```

- `Compendium.Adapters.ZAi` (new): an `IAIProvider` for Z.ai's OpenAI-compatible API — `thinking`, `reasoning_effort`,
  `reasoning_content`, native tool calls (`arguments` read as a string or an object), `tool_choice` limited to `auto`
  (anything else is refused before the request is sent), Z.ai's business error codes (an exhausted balance is a 429 with
  code 1113: `InsufficientCredits`, not a rate limit).
- `Compendium.Adapters.Mistral`: for a GLM id, the effort and `max_tokens` come from the dialect (no `thinking` field:
  Mistral has none) and the chunk lists, read as before, go through the dialect's normalisation; a text part without a
  `type` (the schema's default is `text`) is kept in a stream as it already was in a response.
- `Compendium.Adapters.OpenRouter`: for a GLM id, `reasoning: {effort, exclude}` (nothing at all when there is nothing
  to say: an empty object would ask OpenRouter's own default), `max_tokens` from the dialect, `reasoning` /
  `reasoning_details` read apart, `reasoning_tokens` reported, and the trailing usage frame read: the finish reason of
  the last content chunk is held until it.

**Every other model is sent and read exactly as before.** Golden tests pin, to the byte, the JSON the Mistral and
OpenRouter adapters put on the wire and what they read back for non-GLM models; the goldens were produced by the
adapters before the dialect existed, and are committed on their own ahead of it.

## Consequences

### Positive

- The GLM knowledge is written, tested and mutation-checked once, without HTTP; a fix to it reaches every host.
- A Mistral or OpenRouter consumer pulls pure logic, not a Z.ai client: the Mistral adapter's EU-hosting story is not
  muddied by an unused client for another region in its dependency tree.
- A later host (LiteLLM, Ollama, Hugging Face, Bedrock), in this repository or outside it, can reuse the dialect.
- The same logical GLM stream reads as the same `CompletionChunk` sequence on Z.ai, Mistral and OpenRouter (the tests
  of the three adapters read one scenario and expect one result).

### Negative / Trade-offs

- **Two new packages** (15 in all), and a new kind of project — a model-family dialect — beside the abstraction and the
  adapters. Its name says it belongs with Z.ai's family, but it is not an adapter: no `IAIProvider`, no endpoint.
  Compendium's ADR-0013 ("an adapter references exactly the port it implements") is bent: Mistral and OpenRouter now
  reference a second package, a dialect, never another adapter.
- **A public API to keep in semver**: `GlmDialect`, `GlmModel`. The single release train (one tag, MinVer) keeps the
  versions aligned inside the repository; a third party that pins it takes it on.
- **NuGet ids are permanent**: `Compendium.Adapters.ZAi` and `Compendium.Adapters.ZAi.Glm` should be confirmed before
  the first publish.
- **`max_tokens` changes for a GLM even without `ReasoningOptions`**, against the letter of `ReasoningOptions`
  ("unset, the request is the one it was"): the model reasons unasked, and without room the answer can be empty. The
  room is a cap, not a spend; the figures (4 096 / 16 384 / 32 768) are a starting point to measure, not a measurement.

## Alternatives considered

- **Mistral and OpenRouter reference `Compendium.Adapters.ZAi`, the dialect public inside it.** The letter of "nest the
  Z.ai adapter in Mistral", one new package. Rejected: the first adapter-to-adapter link in the repository, a Z.ai client
  in every Mistral consumer's tree, and an HTTP fix to Z.ai moving Mistral consumers. Kept as the fallback if a single
  new package is preferred.
- **Shared source files**: the dialect `internal`, compiled into each host (`<Compile Include="…" Link="…"/>`). No new
  package and no public API. Rejected: the types in three assemblies, coverage to hold on each copy, no reuse outside
  the repository. The fallback if neither a new package nor a public API is wanted.
- **The dialect in `Compendium.Abstractions.AI`.** Rejected: the abstraction is interfaces and DTOs, no implementation.
- **Mistral delegating its HTTP to a Z.ai provider.** Rejected: the wire formats differ (table above).

## Out of scope, and known gaps

- **Native tool calls in a stream** (`delta.tool_calls`): `CompletionChunk` has no place for them, and no adapter of the
  repository reads them; `tool_stream` is not sent to Z.ai. Native tool calls are read in responses only.
- **Multi-turn tool use and replayed reasoning**: `Message` has no `tool_call_id`, no `tool_calls`, no reasoning; an
  `AgentToolInvocation` has no call id. Mistral recommends replaying its thinking chunks, Z.ai offers Preserved Thinking
  (`clear_thinking: false`), OpenRouter `reasoning_details`; the abstraction forbids sending reasoning back. An
  abstraction change, not a dialect one.
- **`max` on Mistral**: the reasoning guide says GLM 5.3 takes it, Mistral's OpenAPI enum does not list it. It is sent
  only to GLM 5.3 and later, when the caller asks `XHigh` or `Max` (GLM 5.2 gets `xhigh`, which the enum lists); a
  400/422 there would answer the question. Mistral's default effort for GLM is not documented.
- **Z.ai's other limits are passed through, not enforced**: `temperature` in [0, 1] (Mistral takes up to 1.5), one stop
  word, no penalties and no message `name` (not sent). The GLM Coding Plan endpoint (other billing, reasoning kept across
  turns by default) is a `BaseUrl` away, not a default.
- **Reasoning inline in the answer** (`…</think>answer`), as a host without a reasoning parser would return it: not
  parsed.
- **Text tool calls on GLM**: `<tool_call>` and `</tool_call>` are GLM's own tool-call tokens, whose native format is not
  JSON (`<tool_call>name<arg_key>…</arg_key><arg_value>…</arg_value></tool_call>`, chat template of `zai-org/GLM-5.3` on
  Hugging Face). A caller that asks for JSON between those tags, or stops on `</tool_call>`, should measure that the model
  keeps to it and that the stop does not fire inside the reasoning.
- **Left as they were for non-GLM models**: the Mistral stream dropping a text part without `type`, and the OpenRouter
  stream ending before its usage frame. Fixing them would change non-GLM output; each is a one-line follow-up if wanted.
