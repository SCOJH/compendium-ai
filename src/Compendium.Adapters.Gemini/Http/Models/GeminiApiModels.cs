// -----------------------------------------------------------------------
// <copyright file="GeminiApiModels.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.Gemini.Http.Models;

/// <summary>
/// Gemini <c>generateContent</c> / <c>streamGenerateContent</c> request body.
/// </summary>
internal sealed class GeminiGenerateContentRequest
{
    [JsonPropertyName("contents")]
    public required List<GeminiContent> Contents { get; set; }

    [JsonPropertyName("systemInstruction")]
    public GeminiContent? SystemInstruction { get; set; }

    [JsonPropertyName("tools")]
    public List<GeminiTool>? Tools { get; set; }

    [JsonPropertyName("toolConfig")]
    public GeminiToolConfig? ToolConfig { get; set; }

    [JsonPropertyName("generationConfig")]
    public GeminiGenerationConfig? GenerationConfig { get; set; }
}

/// <summary>
/// A single Gemini content payload — role + parts. Role is "user" or "model"
/// (or omitted for system instructions; assistant messages map to "model").
/// </summary>
internal sealed class GeminiContent
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("parts")]
    public required List<GeminiPart> Parts { get; set; }
}

/// <summary>
/// A single part inside a content. Exactly one field is populated.
/// </summary>
internal sealed class GeminiPart
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("inlineData")]
    public GeminiInlineData? InlineData { get; set; }

    [JsonPropertyName("fileData")]
    public GeminiFileData? FileData { get; set; }

    [JsonPropertyName("functionCall")]
    public GeminiFunctionCall? FunctionCall { get; set; }

    [JsonPropertyName("functionResponse")]
    public GeminiFunctionResponse? FunctionResponse { get; set; }
}

internal sealed class GeminiInlineData
{
    [JsonPropertyName("mimeType")]
    public required string MimeType { get; set; }

    [JsonPropertyName("data")]
    public required string Data { get; set; }
}

internal sealed class GeminiFileData
{
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("fileUri")]
    public required string FileUri { get; set; }
}

internal sealed class GeminiFunctionCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public JsonElement? Args { get; set; }
}

internal sealed class GeminiFunctionResponse
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("response")]
    public required JsonElement Response { get; set; }
}

internal sealed class GeminiTool
{
    [JsonPropertyName("functionDeclarations")]
    public List<GeminiFunctionDeclaration>? FunctionDeclarations { get; set; }
}

internal sealed class GeminiFunctionDeclaration
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }
}

internal sealed class GeminiToolConfig
{
    [JsonPropertyName("functionCallingConfig")]
    public GeminiFunctionCallingConfig? FunctionCallingConfig { get; set; }
}

internal sealed class GeminiFunctionCallingConfig
{
    [JsonPropertyName("mode")]
    public required string Mode { get; set; }

    [JsonPropertyName("allowedFunctionNames")]
    public List<string>? AllowedFunctionNames { get; set; }
}

internal sealed class GeminiGenerationConfig
{
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("topP")]
    public float? TopP { get; set; }

    [JsonPropertyName("maxOutputTokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("frequencyPenalty")]
    public float? FrequencyPenalty { get; set; }

    [JsonPropertyName("presencePenalty")]
    public float? PresencePenalty { get; set; }

    [JsonPropertyName("stopSequences")]
    public List<string>? StopSequences { get; set; }

    [JsonPropertyName("responseMimeType")]
    public string? ResponseMimeType { get; set; }

    [JsonPropertyName("responseSchema")]
    public JsonElement? ResponseSchema { get; set; }
}

/// <summary>
/// Gemini <c>generateContent</c> / streaming chunk response.
/// </summary>
internal sealed class GeminiGenerateContentResponse
{
    [JsonPropertyName("candidates")]
    public List<GeminiCandidate>? Candidates { get; set; }

    [JsonPropertyName("usageMetadata")]
    public GeminiUsageMetadata? UsageMetadata { get; set; }

    [JsonPropertyName("modelVersion")]
    public string? ModelVersion { get; set; }

    [JsonPropertyName("promptFeedback")]
    public GeminiPromptFeedback? PromptFeedback { get; set; }
}

internal sealed class GeminiCandidate
{
    [JsonPropertyName("content")]
    public GeminiContent? Content { get; set; }

    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; }
}

internal sealed class GeminiUsageMetadata
{
    [JsonPropertyName("promptTokenCount")]
    public int PromptTokenCount { get; set; }

    [JsonPropertyName("candidatesTokenCount")]
    public int CandidatesTokenCount { get; set; }

    [JsonPropertyName("totalTokenCount")]
    public int TotalTokenCount { get; set; }
}

internal sealed class GeminiPromptFeedback
{
    [JsonPropertyName("blockReason")]
    public string? BlockReason { get; set; }
}

/// <summary>
/// Gemini <c>embedContent</c> request body.
/// </summary>
internal sealed class GeminiEmbedContentRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("content")]
    public required GeminiContent Content { get; set; }

    [JsonPropertyName("outputDimensionality")]
    public int? OutputDimensionality { get; set; }
}

internal sealed class GeminiEmbedContentResponse
{
    [JsonPropertyName("embedding")]
    public GeminiEmbedding? Embedding { get; set; }
}

internal sealed class GeminiEmbedding
{
    [JsonPropertyName("values")]
    public float[] Values { get; set; } = Array.Empty<float>();
}

/// <summary>
/// Gemini <c>batchEmbedContents</c> request body.
/// </summary>
internal sealed class GeminiBatchEmbedContentsRequest
{
    [JsonPropertyName("requests")]
    public required List<GeminiEmbedContentRequest> Requests { get; set; }
}

internal sealed class GeminiBatchEmbedContentsResponse
{
    [JsonPropertyName("embeddings")]
    public List<GeminiEmbedding> Embeddings { get; set; } = new();
}

/// <summary>
/// Gemini list-models response (<c>GET /v1beta/models</c>).
/// </summary>
internal sealed class GeminiModelsResponse
{
    [JsonPropertyName("models")]
    public List<GeminiModelInfo> Models { get; set; } = new();

    /// <summary>The token of the next page; null or empty on the last page.</summary>
    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }
}

internal sealed class GeminiModelInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("inputTokenLimit")]
    public int? InputTokenLimit { get; set; }

    [JsonPropertyName("outputTokenLimit")]
    public int? OutputTokenLimit { get; set; }

    [JsonPropertyName("supportedGenerationMethods")]
    public List<string>? SupportedGenerationMethods { get; set; }
}

internal sealed class GeminiErrorResponse
{
    [JsonPropertyName("error")]
    public GeminiError? Error { get; set; }
}

internal sealed class GeminiError
{
    [JsonPropertyName("code")]
    public int? Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}
