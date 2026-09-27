// -----------------------------------------------------------------------
// <copyright file="MercuryAIProvider.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Mercury.Configuration;
using Compendium.Adapters.Mercury.Http;
using Compendium.Adapters.Mercury.Http.Models;

namespace Compendium.Adapters.Mercury.Services;

/// <summary>
/// Mercury (Inception Labs) implementation of <see cref="IAIProvider"/>.
/// </summary>
/// <remarks>
/// Mercury is a diffusion-based LLM with very high reported throughput
/// (5-10x autoregressive peers, per Inception Labs). The HTTP surface is
/// OpenAI-compatible; this adapter therefore mirrors the OpenAI / OpenRouter
/// adapters very closely.
///
/// <para>Embeddings are not supported by Mercury — <see cref="EmbedAsync"/>
/// returns <c>AI.InvalidRequest</c>.</para>
/// </remarks>
internal sealed class MercuryAIProvider : IAIProvider
{
    private readonly MercuryHttpClient _httpClient;
    private readonly MercuryOptions _options;
    private readonly ILogger<MercuryAIProvider> _logger;

    /// <summary>
    /// Initialises a new instance of the <see cref="MercuryAIProvider"/> class.
    /// </summary>
    public MercuryAIProvider(
        MercuryHttpClient httpClient,
        IOptions<MercuryOptions> options,
        ILogger<MercuryAIProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "mercury";

    /// <inheritdoc />
    public async Task<Result<CompletionResponse>> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var model = request.Model ?? _options.DefaultModel;

        _logger.LogDebug("Sending Mercury completion request to model {Model}", model);

        var apiRequest = MapToApiRequest(request, model, stream: false);
        var result = await _httpClient.CreateCompletionAsync(apiRequest, cancellationToken);

        return result.Match(
            apiResponse => Result.Success(MapToCompletionResponse(apiResponse)),
            error => Result.Failure<CompletionResponse>(error));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<CompletionChunk>> StreamCompleteAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = request.Model ?? _options.DefaultModel;

        _logger.LogDebug("Sending Mercury streaming completion request to model {Model}", model);

        var apiRequest = MapToApiRequest(request, model, stream: true);

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
    public Task<Result<EmbeddingResponse>> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Mercury does not expose an embeddings endpoint; refusing request for {Count} inputs",
            request.Inputs.Count);

        return Task.FromResult(
            Result.Failure<EmbeddingResponse>(
                AIErrors.InvalidRequest(
                    "Mercury does not expose an embeddings endpoint. Use a dedicated embedding provider (e.g. OpenAI, Voyage, Cohere).")));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AIModel>>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching available models from Mercury");

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
            _logger.LogWarning(ex, "Health check failed for Mercury provider");
            return Result.Failure(AIErrors.ProviderUnavailable("mercury"));
        }
    }

    private MercuryCompletionRequest MapToApiRequest(CompletionRequest request, string model, bool stream)
    {
        var messages = new List<MercuryMessage>();

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new MercuryMessage { Role = "system", Content = request.SystemPrompt });
        }

        foreach (var msg in request.Messages)
        {
            messages.Add(new MercuryMessage
            {
                Role = msg.Role.ToString().ToLowerInvariant(),
                Content = msg.Content,
                Name = msg.Name,
            });
        }

        return new MercuryCompletionRequest
        {
            Model = model,
            Messages = messages,
            Temperature = request.Temperature ?? CompletionRequest.FallbackTemperature,
            MaxTokens = request.MaxTokens ?? _options.DefaultMaxTokens,
            TopP = request.TopP,
            FrequencyPenalty = request.FrequencyPenalty,
            PresencePenalty = request.PresencePenalty,
            Stop = request.StopSequences?.ToList(),
            Stream = stream,
        };
    }

    private static CompletionResponse MapToCompletionResponse(MercuryCompletionResponse apiResponse)
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
                EstimatedCostUsd = null,
            },
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(apiResponse.Created).UtcDateTime,
        };
    }

    private static CompletionChunk MapToCompletionChunk(MercuryStreamChunk chunk, int index)
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
                    CompletionTokens = chunk.Usage.CompletionTokens,
                }
                : null,
        };
    }

    private static FinishReason MapFinishReason(string? reason) => reason?.ToLowerInvariant() switch
    {
        "stop" => FinishReason.Stop,
        "length" => FinishReason.Length,
        "content_filter" => FinishReason.ContentFilter,
        "tool_calls" or "function_call" => FinishReason.ToolCall,
        null => FinishReason.InProgress,
        _ => FinishReason.Other,
    };

    private static AIModel MapToAIModel(MercuryModel model)
    {
        var features = model.SupportedFeatures ?? new List<string>();
        return new AIModel
        {
            Id = model.Id,
            Name = model.Name ?? model.Id,
            Provider = "mercury",
            ContextWindow = model.ContextLength,
            MaxOutputTokens = model.MaxOutputLength,
            SupportsStreaming = true,
            SupportsEmbeddings = false,
            SupportsVision = (model.InputModalities ?? new List<string>())
                .Any(m => m.Contains("image", StringComparison.OrdinalIgnoreCase)),
            SupportsTools = features.Contains("tools", StringComparer.OrdinalIgnoreCase),
            PricingInputPerMillion = ParsePricing(model.Pricing?.Prompt),
            PricingOutputPerMillion = ParsePricing(model.Pricing?.Completion),
        };
    }

    private static decimal? ParsePricing(string? pricing)
    {
        if (string.IsNullOrEmpty(pricing))
        {
            return null;
        }

        if (decimal.TryParse(
                pricing,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var perToken))
        {
            return perToken * 1_000_000m;
        }

        return null;
    }
}
