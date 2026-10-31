# Compendium.Abstractions.AI

Provider-agnostic AI/LLM abstractions for the Compendium framework:

- `IAIProvider` — completions, streaming, embeddings against any provider.
- `IPromptRegistry` / `IContextBuilder` — prompt and context management.
- `IReranker` — document reranking.
- Agent primitives (`IAgent`, `IAgentToolRegistry`, request/turn/tool models).
- Native tool calls: `ToolCall`, `Message.ToolCalls` / `Message.ToolCallId`, and the calls read back in
  `CompletionResponse.ToolCalls` / `CompletionChunk.ToolCalls` (final chunk). All optional; an adapter that does not
  support them ignores them.
- `AIErrors` — canonical Result-pattern error codes for the AI domain.

Implementations live in the `Compendium.Adapters.*` packages shipped from the same
repository ([SCOJH/ai](https://github.com/SCOJH/ai)): Anthropic, Azure OpenAI, Bedrock,
DeepSeek, Gemini, Hugging Face, LiteLLM, Mercury, Mistral, Ollama, OpenAI, OpenRouter.

All types return `Result<T>` (`Compendium.Core`) — no exceptions for control flow.
