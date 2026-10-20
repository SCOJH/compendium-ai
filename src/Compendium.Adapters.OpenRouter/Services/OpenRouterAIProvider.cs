// -----------------------------------------------------------------------
// <copyright file="OpenRouterAIProvider.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using Compendium.Adapters.OpenRouter.Configuration;
using Compendium.Adapters.OpenRouter.Http;
using Compendium.Adapters.OpenRouter.Http.Models;
using Compendium.Adapters.ZAi.Glm;

namespace Compendium.Adapters.OpenRouter.Services;

/// <summary>
/// OpenRouter implementation of <see cref="IAIProvider"/>.
/// Provides access to 100+ LLM models through a unified API.
/// </summary>
/// <remarks>
/// The GLM models (<c>z-ai/glm-5.3</c>, <c>~z-ai/glm-latest</c>…) go through the GLM dialect (<see cref="GlmDialect"/>):
/// <c>reasoning: {effort, exclude}</c> on GLM's scale, <c>max_tokens</c> with room for the reasoning, the reasoning read
/// from <c>reasoning</c> / <c>reasoning_details</c> apart from the answer, <c>reasoning_tokens</c> reported, and the usage
/// frame OpenRouter sends after the last content chunk read. Every other model is sent and read exactly as before.
/// </remarks>
internal sealed class OpenRouterAIProvider : IAIProvider
{
    private readonly OpenRouterHttpClient _httpClient;
    private readonly OpenRouterOptions _options;
    private readonly ILogger<OpenRouterAIProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenRouterAIProvider"/> class.
    /// </summary>
    public OpenRouterAIProvider(
        OpenRouterHttpClient httpClient,
        IOptions<OpenRouterOptions> options,
        ILogger<OpenRouterAIProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "openrouter";

    /// <inheritdoc />
    public async Task<Result<CompletionResponse>> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var model = request.Model ?? _options.DefaultModel;

        _logger.LogDebug("Sending completion request to model {Model}", model);

        var glm = GlmDialect.Recognise(model);
        var apiRequest = MapToApiRequest(request, model, stream: false, glm);
        var result = await _httpClient.CreateCompletionAsync(apiRequest, cancellationToken);

        return result.Match(
            apiResponse => Result.Success(glm is null
                ? MapToCompletionResponse(apiResponse)
                : MapToGlmCompletionResponse(apiResponse, request.Reasoning)),
            error => Result.Failure<CompletionResponse>(error));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<CompletionChunk>> StreamCompleteAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = request.Model ?? _options.DefaultModel;

        _logger.LogDebug("Sending streaming completion request to model {Model}", model);

        var glm = GlmDialect.Recognise(model);
        var apiRequest = MapToApiRequest(request, model, stream: true, glm);
        if (glm is not null)
        {
            await foreach (var glmChunk in StreamGlmAsync(apiRequest, request.Reasoning, cancellationToken))
            {
                yield return glmChunk;
            }

            yield break;
        }

        var index = 0;
        await foreach (var chunk in _httpClient.CreateCompletionStreamAsync(apiRequest, cancellationToken))
        {
            if (chunk.IsFailure)
            {
                yield return Result.Failure<CompletionChunk>(chunk.Error);
                yield break;
            }

            var completionChunk = MapToCompletionChunk(chunk.Value, index++);
            yield return Result.Success(completionChunk);

            if (completionChunk.IsFinal)
            {
                yield break;
            }
        }
    }

    /// <inheritdoc />
    public async Task<Result<EmbeddingResponse>> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Sending embedding request for {Count} inputs", request.Inputs.Count);

        // OpenRouter doesn't directly support embeddings,
        // but some models via their native APIs do.
        // For now, return not supported error.
        // In future, could route to specific embedding models.
        return await Task.FromResult(
            Result.Failure<EmbeddingResponse>(
                AIErrors.InvalidRequest("Embeddings are not directly supported via OpenRouter. Use a dedicated embedding provider.")));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AIModel>>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching available models from OpenRouter");

        var result = await _httpClient.ListModelsAsync(cancellationToken);

        return result.Match(
            apiModels => Result.Success<IReadOnlyList<AIModel>>(
                apiModels.Select(MapToAIModel).ToList()),
            error => Result.Failure<IReadOnlyList<AIModel>>(error));
    }

    /// <inheritdoc />
    public async Task<Result> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _httpClient.ListModelsAsync(cancellationToken);
            return result.IsSuccess
                ? Result.Success()
                : Result.Failure(result.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check failed for OpenRouter provider");
            return Result.Failure(AIErrors.ProviderUnavailable("openrouter"));
        }
    }

    /// <summary>
    /// A GLM stream. OpenRouter ends a Chat Completions stream with a usage frame that repeats the finish reason
    /// (https://openrouter.ai/docs/api_reference/streaming.md, "The final usage chunk", read 2026-10-20): the finish
    /// reason of the last content chunk is held until that frame, so the final chunk carries the usage and
    /// <c>reasoning_tokens</c>. A stream that ends without one still ends with a final chunk.
    /// </summary>
    private async IAsyncEnumerable<Result<CompletionChunk>> StreamGlmAsync(
        OpenRouterCompletionRequest apiRequest,
        ReasoningOptions? reasoning,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var index = 0;
        string? id = null;
        string? heldFinish = null;
        await foreach (var chunk in _httpClient.CreateCompletionStreamAsync(apiRequest, cancellationToken))
        {
            if (chunk.IsFailure)
            {
                yield return Result.Failure<CompletionChunk>(chunk.Error);
                yield break;
            }

            id = chunk.Value.Id;
            var choice = chunk.Value.Choices.FirstOrDefault();
            var usage = MapGlmUsage(chunk.Value.Usage);
            var finish = choice?.FinishReason;
            if (finish is not null && usage is null)
            {
                heldFinish = finish;
                finish = null;
            }
            else
            {
                finish ??= usage is not null ? heldFinish : null;
            }

            var mapped = GlmDialect.Chunk(
                id, index, choice?.Delta?.Content, ReadReasoning(choice?.Delta?.Reasoning, choice?.Delta?.ReasoningDetails), finish, usage, reasoning);
            if (mapped is null)
            {
                continue;
            }

            yield return Result.Success(mapped);
            index++;
            if (mapped.IsFinal)
            {
                yield break;
            }
        }

        if (heldFinish is not null && GlmDialect.Chunk(id!, index, null, null, heldFinish, null, reasoning) is { } last)
        {
            yield return Result.Success(last);
        }
    }

    private OpenRouterCompletionRequest MapToApiRequest(CompletionRequest request, string model, bool stream, GlmModel? glm)
    {
        var messages = new List<OpenRouterMessage>();

        // Add system prompt if present
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new OpenRouterMessage { Role = "system", Content = request.SystemPrompt });
        }

        // Add conversation messages
        foreach (var msg in request.Messages)
        {
            messages.Add(new OpenRouterMessage
            {
                Role = msg.Role.ToString().ToLowerInvariant(),
                Content = msg.Content,
                Name = msg.Name
            });
        }

        return new OpenRouterCompletionRequest
        {
            Model = model,
            Messages = messages,
            Temperature = request.Temperature ?? CompletionRequest.FallbackTemperature,
            MaxTokens = glm is null
                ? request.MaxTokens ?? _options.DefaultMaxTokens
                : GlmDialect.MaxTokens(glm, request.MaxTokens, request.Reasoning, _options.DefaultMaxTokens),
            TopP = request.TopP,
            FrequencyPenalty = request.FrequencyPenalty,
            PresencePenalty = request.PresencePenalty,
            Stop = request.StopSequences?.ToList(),
            Stream = stream,
            Reasoning = glm is null ? null : MapGlmReasoning(glm, request.Reasoning)
        };
    }

    /// <summary>
    /// <c>reasoning</c> for a GLM: the effort on GLM's scale, <c>exclude</c> when the reasoning is not wanted back. Never
    /// <c>effort: "none"</c> nor <c>enabled: false</c> (GLM 5.3's reasoning is mandatory there), never a
    /// <c>max_tokens</c> budget (OpenRouter turns it into an effort for GLM), and nothing at all when there is nothing to
    /// say: an empty object would ask OpenRouter's own default effort.
    /// </summary>
    private static OpenRouterReasoning? MapGlmReasoning(GlmModel glm, ReasoningOptions? reasoning)
    {
        if (reasoning is null)
        {
            return null;
        }

        var effort = GlmDialect.EffortFor(glm, reasoning);
        var exclude = reasoning.IncludeReasoning ? (bool?)null : true;
        return effort is null && exclude is null ? null : new OpenRouterReasoning { Effort = effort, Exclude = exclude };
    }

    private static CompletionResponse MapToGlmCompletionResponse(OpenRouterCompletionResponse apiResponse, ReasoningOptions? reasoning)
    {
        var choice = apiResponse.Choices.FirstOrDefault();
        var usage = MapGlmUsage(apiResponse.Usage);

        return new CompletionResponse
        {
            Id = apiResponse.Id,
            Model = apiResponse.Model,
            Content = choice?.Message?.Content ?? string.Empty,
            Reasoning = GlmDialect.Surface(ReadReasoning(choice?.Message?.Reasoning, choice?.Message?.ReasoningDetails), reasoning),
            FinishReason = GlmDialect.MapFinishReason(choice?.FinishReason),
            Usage = usage ?? new UsageStats { PromptTokens = 0, CompletionTokens = 0 },
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(apiResponse.Created).UtcDateTime
        };
    }

    /// <summary>
    /// The reasoning text OpenRouter returns: <c>reasoning</c> (a string), else the <c>reasoning.text</c> entries of
    /// <c>reasoning_details[]</c> — the same text, so never both. Anything else reads as no reasoning.
    /// </summary>
    private static string? ReadReasoning(JsonElement? reasoning, JsonElement? details)
    {
        if (reasoning is { ValueKind: JsonValueKind.String } text && text.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        if (details is not { ValueKind: JsonValueKind.Array } items)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "reasoning.text"
                && item.TryGetProperty("text", out var part) && part.ValueKind == JsonValueKind.String)
            {
                sb.Append(part.GetString());
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static UsageStats? MapGlmUsage(OpenRouterUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        int? reasoningTokens = usage.CompletionTokensDetails is { ValueKind: JsonValueKind.Object } details
            && details.TryGetProperty("reasoning_tokens", out var tokens)
            && tokens.ValueKind == JsonValueKind.Number
            && tokens.TryGetInt32(out var count)
                ? count
                : null;

        return new UsageStats
        {
            PromptTokens = usage.PromptTokens,
            CompletionTokens = usage.CompletionTokens,
            ReasoningTokens = reasoningTokens
        };
    }

    private static CompletionResponse MapToCompletionResponse(OpenRouterCompletionResponse apiResponse)
    {
        var choice = apiResponse.Choices.FirstOrDefault();

        return new CompletionResponse
        {
            Id = apiResponse.Id,
            Model = apiResponse.Model,
            Content = choice?.Message?.Content ?? string.Empty,
            FinishReason = MapFinishReason(choice?.FinishReason),
            Usage = new UsageStats
            {
                PromptTokens = apiResponse.Usage?.PromptTokens ?? 0,
                CompletionTokens = apiResponse.Usage?.CompletionTokens ?? 0,
                EstimatedCostUsd = null // OpenRouter provides this in a separate field
            },
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(apiResponse.Created).UtcDateTime
        };
    }

    private static CompletionChunk MapToCompletionChunk(OpenRouterStreamChunk chunk, int index)
    {
        var choice = chunk.Choices.FirstOrDefault();
        var isFinal = choice?.FinishReason != null;

        return new CompletionChunk
        {
            Id = chunk.Id,
            ContentDelta = choice?.Delta?.Content ?? string.Empty,
            Index = index,
            IsFinal = isFinal,
            FinishReason = isFinal ? MapFinishReason(choice?.FinishReason) : null,
            Usage = chunk.Usage != null
                ? new UsageStats
                {
                    PromptTokens = chunk.Usage.PromptTokens,
                    CompletionTokens = chunk.Usage.CompletionTokens
                }
                : null
        };
    }

    private static FinishReason MapFinishReason(string? reason) => reason?.ToLowerInvariant() switch
    {
        "stop" => FinishReason.Stop,
        "length" => FinishReason.Length,
        "content_filter" => FinishReason.ContentFilter,
        "tool_calls" or "function_call" => FinishReason.ToolCall,
        null => FinishReason.InProgress,
        _ => FinishReason.Other
    };

    private static AIModel MapToAIModel(OpenRouterModel model)
    {
        return new AIModel
        {
            Id = model.Id,
            Name = model.Name ?? model.Id,
            Provider = ExtractProvider(model.Id),
            ContextWindow = model.ContextLength,
            MaxOutputTokens = model.TopProvider?.MaxCompletionTokens,
            SupportsStreaming = true,
            SupportsEmbeddings = false,
            SupportsVision = model.Architecture?.Modality?.Contains("image") ?? false,
            SupportsTools = true, // Most models support function calling
            PricingInputPerMillion = ParsePricing(model.Pricing?.Prompt),
            PricingOutputPerMillion = ParsePricing(model.Pricing?.Completion)
        };
    }

    private static string ExtractProvider(string modelId)
    {
        var slashIndex = modelId.IndexOf('/');
        return slashIndex > 0 ? modelId[..slashIndex] : "unknown";
    }

    private static decimal? ParsePricing(string? pricing)
    {
        if (string.IsNullOrEmpty(pricing)) return null;

        // OpenRouter returns pricing per token, we want per million
        if (decimal.TryParse(pricing, out var perToken))
        {
            return perToken * 1_000_000;
        }
        return null;
    }
}
