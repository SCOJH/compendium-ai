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

/// <summary>One content block in a response (text only at this layer).</summary>
internal sealed class AnthropicContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; set; }
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

/// <summary>One page of <c>GET /v1/models</c>.</summary>
internal sealed class AnthropicModelsPage
{
    [JsonPropertyName("data")]
    public List<AnthropicModelInfo> Data { get; set; } = new();

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    [JsonPropertyName("first_id")]
    public string? FirstId { get; set; }

    [JsonPropertyName("last_id")]
    public string? LastId { get; set; }
}

/// <summary>A <c>ModelInfo</c> of <c>GET /v1/models</c>; fields the adapter does not map are left out.</summary>
internal sealed class AnthropicModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("max_input_tokens")]
    public int? MaxInputTokens { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("line")]
    public string? Line { get; set; }

    [JsonPropertyName("capabilities")]
    public AnthropicModelCapabilities? Capabilities { get; set; }
}

/// <summary>The <c>capabilities</c> of a <c>ModelInfo</c>; only what the adapter maps.</summary>
internal sealed class AnthropicModelCapabilities
{
    [JsonPropertyName("image_input")]
    public AnthropicCapabilitySupport? ImageInput { get; set; }
}

/// <summary>A capability flag of a <c>ModelInfo</c>: <c>{ "supported": true }</c>.</summary>
internal sealed class AnthropicCapabilitySupport
{
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }
}
