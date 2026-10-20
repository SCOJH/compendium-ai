// -----------------------------------------------------------------------
// <copyright file="OpenRouterApiModels.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.OpenRouter.Http.Models;

/// <summary>
/// OpenRouter completion request.
/// </summary>
internal sealed class OpenRouterCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("messages")]
    public required List<OpenRouterMessage> Messages { get; set; }

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

    /// <summary>
    /// OpenRouter's reasoning control, sent for a GLM model only (<c>effort</c>, <c>exclude</c>); left out otherwise, so
    /// every other request is the one it was.
    /// </summary>
    [JsonPropertyName("reasoning")]
    public OpenRouterReasoning? Reasoning { get; set; }
}

/// <summary>
/// OpenRouter's <c>reasoning</c> request object
/// (https://openrouter.ai/docs/guides/best-practices/reasoning-tokens.md, read 2026-10-20).
/// </summary>
internal sealed class OpenRouterReasoning
{
    /// <summary><c>low</c>, <c>high</c> or <c>max</c> for a GLM; left out for the model's default.</summary>
    [JsonPropertyName("effort")]
    public string? Effort { get; set; }

    /// <summary><c>true</c>: the model still reasons, but the reasoning is not returned.</summary>
    [JsonPropertyName("exclude")]
    public bool? Exclude { get; set; }
}

/// <summary>
/// OpenRouter message.
/// </summary>
internal sealed class OpenRouterMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public required string Content { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// On a response: the reasoning text (a string or null), read for a GLM model only. Held as raw JSON so that a model
    /// that sends anything else there reads as before. Never set on a request.
    /// </summary>
    [JsonPropertyName("reasoning")]
    public JsonElement? Reasoning { get; set; }

    /// <summary>On a response: <c>reasoning_details[]</c>, read for a GLM model when <see cref="Reasoning"/> is absent.</summary>
    [JsonPropertyName("reasoning_details")]
    public JsonElement? ReasoningDetails { get; set; }
}

/// <summary>
/// OpenRouter completion response.
/// </summary>
internal sealed class OpenRouterCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("choices")]
    public List<OpenRouterChoice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public OpenRouterUsage? Usage { get; set; }
}

/// <summary>
/// OpenRouter choice.
/// </summary>
internal sealed class OpenRouterChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public OpenRouterMessage? Message { get; set; }

    [JsonPropertyName("delta")]
    public OpenRouterDelta? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>
/// OpenRouter streaming delta.
/// </summary>
internal sealed class OpenRouterDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>The reasoning delta (a string or null), read for a GLM model only; raw JSON, as on the message.</summary>
    [JsonPropertyName("reasoning")]
    public JsonElement? Reasoning { get; set; }

    /// <summary><c>reasoning_details[]</c> of the delta, read for a GLM model when <see cref="Reasoning"/> is absent.</summary>
    [JsonPropertyName("reasoning_details")]
    public JsonElement? ReasoningDetails { get; set; }
}

/// <summary>
/// OpenRouter usage statistics.
/// </summary>
internal sealed class OpenRouterUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }

    /// <summary><c>completion_tokens_details</c> (<c>reasoning_tokens</c>), read for a GLM model only; raw JSON.</summary>
    [JsonPropertyName("completion_tokens_details")]
    public JsonElement? CompletionTokensDetails { get; set; }
}

/// <summary>
/// OpenRouter streaming chunk.
/// </summary>
internal sealed class OpenRouterStreamChunk
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<OpenRouterChoice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public OpenRouterUsage? Usage { get; set; }
}

/// <summary>
/// OpenRouter models list response.
/// </summary>
internal sealed class OpenRouterModelsResponse
{
    [JsonPropertyName("data")]
    public List<OpenRouterModel> Data { get; set; } = new();
}

/// <summary>
/// OpenRouter model information.
/// </summary>
internal sealed class OpenRouterModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("context_length")]
    public int? ContextLength { get; set; }

    [JsonPropertyName("architecture")]
    public OpenRouterArchitecture? Architecture { get; set; }

    [JsonPropertyName("pricing")]
    public OpenRouterPricing? Pricing { get; set; }

    [JsonPropertyName("top_provider")]
    public OpenRouterTopProvider? TopProvider { get; set; }
}

/// <summary>
/// OpenRouter model architecture.
/// </summary>
internal sealed class OpenRouterArchitecture
{
    [JsonPropertyName("modality")]
    public string? Modality { get; set; }

    [JsonPropertyName("tokenizer")]
    public string? Tokenizer { get; set; }
}

/// <summary>
/// OpenRouter pricing information.
/// </summary>
internal sealed class OpenRouterPricing
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("completion")]
    public string? Completion { get; set; }
}

/// <summary>
/// OpenRouter top provider information.
/// </summary>
internal sealed class OpenRouterTopProvider
{
    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }
}

/// <summary>
/// OpenRouter error response.
/// </summary>
internal sealed class OpenRouterErrorResponse
{
    [JsonPropertyName("error")]
    public OpenRouterError? Error { get; set; }
}

/// <summary>
/// OpenRouter error details.
/// </summary>
internal sealed class OpenRouterError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
