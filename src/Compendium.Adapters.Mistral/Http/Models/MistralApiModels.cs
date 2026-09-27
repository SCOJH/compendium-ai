// -----------------------------------------------------------------------
// <copyright file="MistralApiModels.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.Mistral.Http.Models;

/// <summary>
/// Mistral chat completion request body. Mistral's REST API is OpenAI-compatible at
/// <c>/v1/chat/completions</c> with a small handful of vendor-specific knobs.
/// </summary>
internal sealed class MistralChatCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("messages")]
    public required List<MistralChatMessage> Messages { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("stop")]
    public List<string>? Stop { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("tools")]
    public List<MistralToolDefinition>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public object? ToolChoice { get; set; }

    [JsonPropertyName("response_format")]
    public MistralResponseFormat? ResponseFormat { get; set; }

    [JsonPropertyName("random_seed")]
    public int? RandomSeed { get; set; }

    [JsonPropertyName("safe_prompt")]
    public bool? SafePrompt { get; set; }

    /// <summary>
    /// <c>"high"</c>: the model returns a thinking chunk before its answer (Magistral, and the models that take the
    /// parameter). Left out, the model's default.
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }
}

/// <summary>
/// Mistral chat message — supports plain-text content or a list of content parts
/// (text + image URL) for Pixtral-style vision inputs.
/// </summary>
internal sealed class MistralChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    /// <summary>
    /// String for plain text messages, or a <see cref="List{MistralContentPart}"/> when emitting
    /// multimodal (vision) content. We hold it as <see cref="object"/> because Mistral's wire format
    /// accepts both shapes on the same field.
    /// </summary>
    [JsonPropertyName("content")]
    public object? Content { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<MistralToolCall>? ToolCalls { get; set; }
}

/// <summary>
/// One element of a multimodal content array. <c>type</c> is either <c>"text"</c> or
/// <c>"image_url"</c>; depending on the type, either <see cref="Text"/> or <see cref="ImageUrl"/> is set.
/// </summary>
internal sealed class MistralContentPart
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("image_url")]
    public MistralImageUrl? ImageUrl { get; set; }
}

internal sealed class MistralImageUrl
{
    [JsonPropertyName("url")]
    public required string Url { get; set; }
}

internal sealed class MistralToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public required MistralFunctionDefinition Function { get; set; }
}

internal sealed class MistralFunctionDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }
}

internal sealed class MistralToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public MistralToolCallFunction? Function { get; set; }
}

internal sealed class MistralToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}

/// <summary>
/// Mistral response_format. Mistral supports <c>"text"</c>, <c>"json_object"</c>, and (recently)
/// <c>"json_schema"</c> with an attached schema.
/// </summary>
internal sealed class MistralResponseFormat
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("json_schema")]
    public MistralJsonSchemaFormat? JsonSchema { get; set; }
}

internal sealed class MistralJsonSchemaFormat
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("schema")]
    public JsonElement Schema { get; set; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; set; }
}

/// <summary>
/// Mistral chat completion response.
/// </summary>
internal sealed class MistralChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("choices")]
    public List<MistralChatChoice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public MistralUsage? Usage { get; set; }
}

internal sealed class MistralChatChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public MistralChatMessage? Message { get; set; }

    [JsonPropertyName("delta")]
    public MistralChatDelta? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal sealed class MistralChatDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>
    /// A string, or — while a reasoning model thinks, and at the turn from thinking to answering — a list of chunks:
    /// <c>{type: "thinking", thinking: [{type: "text", text}]}</c> and <c>{type: "text", text}</c>.
    /// </summary>
    [JsonPropertyName("content")]
    public JsonElement? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<MistralToolCall>? ToolCalls { get; set; }
}

internal sealed class MistralUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

/// <summary>
/// Mistral streaming SSE chunk.
/// </summary>
internal sealed class MistralStreamChunk
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<MistralChatChoice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public MistralUsage? Usage { get; set; }
}

/// <summary>
/// Mistral embeddings request body.
/// </summary>
internal sealed class MistralEmbeddingsRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("input")]
    public required List<string> Input { get; set; }

    [JsonPropertyName("encoding_format")]
    public string EncodingFormat { get; set; } = "float";
}

internal sealed class MistralEmbeddingsResponse
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public List<MistralEmbeddingData> Data { get; set; } = new();

    [JsonPropertyName("usage")]
    public MistralEmbeddingsUsage? Usage { get; set; }
}

internal sealed class MistralEmbeddingData
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = Array.Empty<float>();
}

internal sealed class MistralEmbeddingsUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

/// <summary>
/// Mistral list-models response.
/// </summary>
internal sealed class MistralModelsResponse
{
    [JsonPropertyName("data")]
    public List<MistralModelInfo> Data { get; set; } = new();
}

internal sealed class MistralModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; set; }

    [JsonPropertyName("created")]
    public long? Created { get; set; }
}

/// <summary>
/// Mistral error envelope. Two shapes are possible:
/// <list type="bullet">
///   <item><description>OpenAI-compat: <c>{ "error": { "message": "...", "code": "...", "type": "..." } }</c></description></item>
///   <item><description>Native shape: <c>{ "message": "..." | { "detail": "..." }, "type": "...", "code": "..." }</c></description></item>
/// </list>
/// We try both during deserialisation.
/// </summary>
internal sealed class MistralErrorResponse
{
    [JsonPropertyName("error")]
    public MistralError? Error { get; set; }

    /// <summary>
    /// Top-level <c>message</c> field for Mistral's native error shape — may be a string OR an object
    /// containing <c>detail</c>. We accept either via <see cref="JsonElement"/>.
    /// </summary>
    [JsonPropertyName("message")]
    public JsonElement? Message { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

internal sealed class MistralError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
