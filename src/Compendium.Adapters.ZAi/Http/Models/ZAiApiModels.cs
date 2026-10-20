// -----------------------------------------------------------------------
// <copyright file="ZAiApiModels.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.ZAi.Http.Models;

/// <summary>
/// Z.ai chat completion request body (<c>POST /paas/v4/chat/completions</c>,
/// https://docs.z.ai/api-reference/llm/chat-completion.md, read 2026-10-20). OpenAI-compatible, plus <c>thinking</c>,
/// and without the penalties.
/// </summary>
internal sealed class ZAiChatCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("messages")]
    public required List<ZAiRequestMessage> Messages { get; set; }

    /// <summary><c>{type: "enabled"}</c> when reasoning is asked for; never <c>disabled</c> (an error on GLM 5.3).</summary>
    [JsonPropertyName("thinking")]
    public ZAiThinking? Thinking { get; set; }

    /// <summary>
    /// <c>low</c>, <c>high</c> or <c>max</c> (GLM 5.3 and later), <c>high</c> or <c>xhigh</c> (GLM 5.2, which Z.ai runs
    /// at <c>max</c>); left out for the default, <c>max</c>.
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("stop")]
    public List<string>? Stop { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("tools")]
    public List<ZAiToolDefinition>? Tools { get; set; }

    /// <summary>Only <c>auto</c> exists on Z.ai.</summary>
    [JsonPropertyName("tool_choice")]
    public string? ToolChoice { get; set; }

    /// <summary>6 to 64 characters, or left out.</summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    /// <summary>6 to 128 characters, or left out.</summary>
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }
}

internal sealed class ZAiThinking
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }
}

internal sealed class ZAiRequestMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public required string Content { get; set; }
}

internal sealed class ZAiToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public required ZAiFunctionDefinition Function { get; set; }
}

internal sealed class ZAiFunctionDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }
}

/// <summary>Z.ai chat completion response.</summary>
internal sealed class ZAiChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("choices")]
    public List<ZAiChoice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public ZAiUsage? Usage { get; set; }
}

internal sealed class ZAiChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public ZAiResponseMessage? Message { get; set; }

    [JsonPropertyName("delta")]
    public ZAiResponseMessage? Delta { get; set; }

    /// <summary>
    /// <c>stop</c>, <c>tool_calls</c>, <c>length</c>, <c>sensitive</c>, <c>model_context_window_exceeded</c>,
    /// <c>network_error</c>.
    /// </summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>An assistant message, or a stream's delta: the answer and the reasoning apart.</summary>
internal sealed class ZAiResponseMessage
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>The reasoning (a string), in a response and in each delta while the model thinks.</summary>
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<ZAiToolCall>? ToolCalls { get; set; }
}

internal sealed class ZAiToolCall
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("function")]
    public ZAiToolCallFunction? Function { get; set; }
}

internal sealed class ZAiToolCallFunction
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Typed <c>object</c> in Z.ai's response schema, a JSON string in its examples: held raw, read either way.
    /// </summary>
    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; set; }
}

internal sealed class ZAiUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }

    /// <summary>
    /// Not in Z.ai's documentation on 2026-10-20 (no reasoning count is): read if it ever comes, as OpenAI's.
    /// </summary>
    [JsonPropertyName("completion_tokens_details")]
    public JsonElement? CompletionTokensDetails { get; set; }
}

/// <summary>A Z.ai SSE chunk; <c>usage</c> only on the last one (https://docs.z.ai/guides/capabilities/streaming.md).</summary>
internal sealed class ZAiStreamChunk
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<ZAiChoice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public ZAiUsage? Usage { get; set; }
}

/// <summary><c>{"error": {"code": "1214", "message": "..."}}</c> — the code a string in the examples, an integer in the schema.</summary>
internal sealed class ZAiErrorResponse
{
    [JsonPropertyName("error")]
    public ZAiError? Error { get; set; }
}

internal sealed class ZAiError
{
    [JsonPropertyName("code")]
    public JsonElement? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
