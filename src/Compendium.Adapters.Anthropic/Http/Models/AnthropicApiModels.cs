// -----------------------------------------------------------------------
// <copyright file="AnthropicApiModels.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.Anthropic.Http.Models;

/// <summary>
/// Anthropic <c>POST /v1/messages</c> request body.
/// </summary>
internal sealed class AnthropicMessagesRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("messages")]
    public required List<AnthropicMessage> Messages { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("system")]
    public List<AnthropicSystemBlock>? System { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("stop_sequences")]
    public List<string>? StopSequences { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("metadata")]
    public AnthropicRequestMetadata? Metadata { get; set; }

    [JsonPropertyName("thinking")]
    public AnthropicThinking? Thinking { get; set; }

    [JsonPropertyName("output_config")]
    public AnthropicOutputConfig? OutputConfig { get; set; }
}

/// <summary>
/// Extended thinking: <c>{type: "adaptive", display}</c> on Claude Opus/Sonnet 4.6 and later, Fable and Mythos;
/// <c>{type: "enabled", budget_tokens}</c> on the models before them (<c>ClaudeReasoning</c>).
/// </summary>
internal sealed class AnthropicThinking
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "adaptive";

    [JsonPropertyName("budget_tokens")]
    public int? BudgetTokens { get; set; }

    [JsonPropertyName("display")]
    public string? Display { get; set; }
}

/// <summary><c>output_config</c>: the effort of an adaptive model (<c>low</c> … <c>max</c>).</summary>
internal sealed class AnthropicOutputConfig
{
    [JsonPropertyName("effort")]
    public string? Effort { get; set; }
}

/// <summary>A system-prompt block. Anthropic accepts either a single string or an array of blocks.</summary>
internal sealed class AnthropicSystemBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("cache_control")]
    public AnthropicCacheControl? CacheControl { get; set; }
}

/// <summary>Opt-in prompt caching marker.</summary>
internal sealed class AnthropicCacheControl
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "ephemeral";
}

/// <summary>Metadata block — Anthropic only uses <c>user_id</c> today.</summary>
internal sealed class AnthropicRequestMetadata
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }
}

/// <summary>An Anthropic message — role + content (text-only at this layer).</summary>
internal sealed class AnthropicMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public required string Content { get; set; }
}

/// <summary>Synchronous <c>messages</c> response body.</summary>
internal sealed class AnthropicMessagesResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public List<AnthropicContentBlock> Content { get; set; } = new();

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

/// <summary>One content block in a response: text, or thinking (its <c>thinking</c> text, empty when omitted).</summary>
internal sealed class AnthropicContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }
}

/// <summary>Anthropic usage statistics.</summary>
internal sealed class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public int? CacheCreationInputTokens { get; set; }

    [JsonPropertyName("cache_read_input_tokens")]
    public int? CacheReadInputTokens { get; set; }
}

/// <summary>SSE event envelope used by <c>POST /v1/messages</c> with <c>stream=true</c>.</summary>
internal sealed class AnthropicStreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public AnthropicStreamMessage? Message { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("delta")]
    public AnthropicStreamDelta? Delta { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

/// <summary>Message payload nested inside a <c>message_start</c> SSE event.</summary>
internal sealed class AnthropicStreamMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

/// <summary>Delta payload nested inside SSE events.</summary>
internal sealed class AnthropicStreamDelta
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>The fragment of a <c>thinking_delta</c>.</summary>
    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; set; }
}

/// <summary>Anthropic error envelope: <c>{ "type": "error", "error": { "type": "...", "message": "..." } }</c>.</summary>
internal sealed class AnthropicErrorResponse
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("error")]
    public AnthropicError? Error { get; set; }
}

/// <summary>Inner Anthropic error details.</summary>
internal sealed class AnthropicError
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
