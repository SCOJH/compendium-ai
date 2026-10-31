// -----------------------------------------------------------------------
// <copyright file="DeepSeekApiModels.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.DeepSeek.Http.Models;

/// <summary>
/// DeepSeek chat completion request body. Matches OpenAI's <c>/v1/chat/completions</c> shape
/// verbatim; the only DeepSeek-specific extension is the <c>reasoning_content</c> field on
/// response messages/deltas (see <see cref="DeepSeekChatMessage"/> / <see cref="DeepSeekChatDelta"/>).
/// </summary>
internal sealed class DeepSeekChatCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("messages")]
    public required List<DeepSeekChatMessage> Messages { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public float? FrequencyPenalty { get; set; }

    [JsonPropertyName("presence_penalty")]
    public float? PresencePenalty { get; set; }

    [JsonPropertyName("stop")]
    public List<string>? Stop { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("stream_options")]
    public DeepSeekStreamOptions? StreamOptions { get; set; }

    [JsonPropertyName("tools")]
    public List<DeepSeekToolDefinition>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public object? ToolChoice { get; set; }

    [JsonPropertyName("response_format")]
    public DeepSeekResponseFormat? ResponseFormat { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }
}

/// <summary><c>response_format</c>: DeepSeek's JSON mode, <c>{ "type": "json_object" }</c>, with no schema.</summary>
internal sealed class DeepSeekResponseFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "json_object";
}

internal sealed class DeepSeekStreamOptions
{
    [JsonPropertyName("include_usage")]
    public bool IncludeUsage { get; set; }
}

/// <summary>
/// DeepSeek chat message. Identical to OpenAI's shape plus the optional
/// <c>reasoning_content</c> field surfaced by <c>deepseek-reasoner</c>.
/// </summary>
internal sealed class DeepSeekChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<DeepSeekToolCall>? ToolCalls { get; set; }

    /// <summary>
    /// DeepSeek-specific. Reasoning traces emitted by <c>deepseek-reasoner</c>.
    /// </summary>
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }
}

internal sealed class DeepSeekToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public required DeepSeekFunctionDefinition Function { get; set; }
}

internal sealed class DeepSeekFunctionDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }
}

internal sealed class DeepSeekToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public DeepSeekToolCallFunction? Function { get; set; }
}

internal sealed class DeepSeekToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}

/// <summary>
/// DeepSeek chat completion response.
/// </summary>
internal sealed class DeepSeekChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("choices")]
    public List<DeepSeekChatChoice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public DeepSeekUsage? Usage { get; set; }
}

internal sealed class DeepSeekChatChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public DeepSeekChatMessage? Message { get; set; }

    [JsonPropertyName("delta")]
    public DeepSeekChatDelta? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal sealed class DeepSeekChatDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>
    /// DeepSeek-specific. Streaming reasoning trace delta.
    /// </summary>
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<DeepSeekToolCall>? ToolCalls { get; set; }
}

internal sealed class DeepSeekUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

/// <summary>
/// DeepSeek streaming SSE chunk.
/// </summary>
internal sealed class DeepSeekStreamChunk
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<DeepSeekChatChoice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public DeepSeekUsage? Usage { get; set; }
}

/// <summary>
/// DeepSeek list-models response — same shape as OpenAI's <c>/models</c>.
/// </summary>
internal sealed class DeepSeekModelsResponse
{
    [JsonPropertyName("data")]
    public List<DeepSeekModelInfo> Data { get; set; } = new();
}

internal sealed class DeepSeekModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; set; }

    [JsonPropertyName("created")]
    public long? Created { get; set; }
}

internal sealed class DeepSeekErrorResponse
{
    [JsonPropertyName("error")]
    public DeepSeekError? Error { get; set; }
}

internal sealed class DeepSeekError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
