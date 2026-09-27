// -----------------------------------------------------------------------
// <copyright file="LiteLLMAIProvider.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Abstractions.AI.Agents.Models;
using Compendium.Adapters.LiteLLM.Configuration;
using Compendium.Adapters.LiteLLM.Http;
using Compendium.Adapters.LiteLLM.Http.Models;
using Compendium.Adapters.LiteLLM.StructuredOutputs;
using Compendium.Adapters.LiteLLM.Tools;

namespace Compendium.Adapters.LiteLLM.Services;

/// <summary>
/// LiteLLM proxy implementation of <see cref="IAIProvider"/>. Wraps the proxy's OpenAI-compatible
/// chat, streaming, embeddings, tool-calling and structured-output surface area.
/// </summary>
internal sealed class LiteLLMAIProvider : IAIProvider
{
    private readonly LiteLLMHttpClient _httpClient;
    private readonly LiteLLMOptions _options;
    private readonly ILogger<LiteLLMAIProvider> _logger;

    public LiteLLMAIProvider(
        LiteLLMHttpClient httpClient,
        IOptions<LiteLLMOptions> options,
        ILogger<LiteLLMAIProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "litellm";

    /// <inheritdoc />
    public async Task<Result<CompletionResponse>> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;
        _logger.LogDebug("Sending LiteLLM chat completion to model {Model}", model);

        var apiRequest = MapToApiRequest(request, model, stream: false);
        var result = await _httpClient.CreateChatCompletionAsync(apiRequest, cancellationToken);
        return result.Match(
            r => Result.Success(MapToCompletionResponse(r)),
            error => Result.Failure<CompletionResponse>(error));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<CompletionChunk>> StreamCompleteAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;
        _logger.LogDebug("Sending LiteLLM streaming chat completion to model {Model}", model);

        var apiRequest = MapToApiRequest(request, model, stream: true);

        var index = 0;
        await foreach (var chunk in _httpClient.StreamChatCompletionAsync(apiRequest, cancellationToken))
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
        ArgumentNullException.ThrowIfNull(request);
        if (request.Inputs == null || request.Inputs.Count == 0)
        {
            return Result.Failure<EmbeddingResponse>(
                AIErrors.InvalidRequest("At least one input is required to compute embeddings."));
        }

        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultEmbeddingModel : request.Model;
        var batchSize = Math.Max(1, _options.MaxEmbeddingsBatchSize);
        _logger.LogDebug(
            "Sending LiteLLM embeddings request for {Count} inputs (batch size {Batch}, model {Model})",
            request.Inputs.Count,
            batchSize,
            model);

        var aggregated = new List<Embedding>(request.Inputs.Count);
        var totalPromptTokens = 0;

        for (var offset = 0; offset < request.Inputs.Count; offset += batchSize)
        {
            var slice = request.Inputs.Skip(offset).Take(batchSize).ToList();
            var batchRequest = new LiteLLMEmbeddingsRequest
            {
                Model = model,
                Input = slice,
                Dimensions = request.Dimensions,
                User = request.UserId
            };

            var result = await _httpClient.CreateEmbeddingsAsync(batchRequest, cancellationToken);
            if (result.IsFailure)
            {
                return Result.Failure<EmbeddingResponse>(result.Error);
            }

            var batchOffset = offset;
            foreach (var data in result.Value.Data)
            {
                aggregated.Add(new Embedding
                {
                    Index = batchOffset + data.Index,
                    Vector = data.Embedding
                });
            }

            if (result.Value.Usage != null)
            {
                totalPromptTokens += result.Value.Usage.PromptTokens;
            }
        }

        return Result.Success(new EmbeddingResponse
        {
            Model = model,
            Embeddings = aggregated,
            Usage = new EmbeddingUsage { PromptTokens = totalPromptTokens }
        });
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AIModel>>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching available models from LiteLLM");
        var result = await _httpClient.ListModelsAsync(cancellationToken);
        return result.Match(
            apiModels => Result.Success<IReadOnlyList<AIModel>>(apiModels.Select(MapToAIModel).ToList()),
            error => Result.Failure<IReadOnlyList<AIModel>>(error));
    }

    /// <inheritdoc />
    public async Task<Result> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _httpClient.ListModelsAsync(cancellationToken);
            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check failed for LiteLLM provider");
            return Result.Failure(AIErrors.ProviderUnavailable("litellm"));
        }
    }

    private LiteLLMChatCompletionRequest MapToApiRequest(CompletionRequest request, string model, bool stream)
    {
        var messages = new List<LiteLLMChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new LiteLLMChatMessage { Role = "system", Content = request.SystemPrompt });
        }
        foreach (var msg in request.Messages)
        {
            messages.Add(new LiteLLMChatMessage
            {
                Role = msg.Role.ToString().ToLowerInvariant(),
                Content = msg.Content,
                Name = msg.Name
            });
        }

        var apiRequest = new LiteLLMChatCompletionRequest
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
            User = request.UserId
        };
        if (stream)
        {
            apiRequest.StreamOptions = new LiteLLMStreamOptions { IncludeUsage = true };
        }

        ApplyTools(apiRequest, request);
        ApplyResponseFormat(apiRequest, request);
        return apiRequest;
    }

    private static void ApplyTools(LiteLLMChatCompletionRequest apiRequest, CompletionRequest request)
    {
        if (request.AdditionalParameters == null)
        {
            return;
        }

        if (request.AdditionalParameters.TryGetValue(LiteLLMToolCallingExtensions.ToolsKey, out var toolsRaw)
            && toolsRaw is IReadOnlyList<AgentTool> tools
            && tools.Count > 0)
        {
            apiRequest.Tools = tools.Select(t => new LiteLLMToolDefinition
            {
                Function = new LiteLLMFunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = ParseSchemaOrDefault(t.InputSchemaJson)
                }
            }).ToList();
        }

        if (request.AdditionalParameters.TryGetValue(LiteLLMToolCallingExtensions.ToolChoiceKey, out var choiceRaw)
            && choiceRaw is string toolChoice
            && !string.IsNullOrEmpty(toolChoice))
        {
            apiRequest.ToolChoice = toolChoice switch
            {
                "auto" or "required" or "none" => toolChoice,
                _ => new { type = "function", function = new { name = toolChoice } }
            };
        }
    }

    private void ApplyResponseFormat(LiteLLMChatCompletionRequest apiRequest, CompletionRequest request)
    {
        var parameters = request.AdditionalParameters;
        if (parameters != null
            && parameters.TryGetValue(LiteLLMStructuredOutputExtensions.SchemaKey, out var schemaRaw)
            && schemaRaw is string schemaJson
            && !string.IsNullOrWhiteSpace(schemaJson))
        {
            var schemaName = parameters.TryGetValue(LiteLLMStructuredOutputExtensions.SchemaNameKey, out var nameRaw)
                && nameRaw is string s
                ? s
                : "response";
            var strict = !parameters.TryGetValue(LiteLLMStructuredOutputExtensions.StrictKey, out var strictRaw)
                || strictRaw is not bool b
                || b;

            apiRequest.ResponseFormat = new LiteLLMResponseFormat
            {
                Type = "json_schema",
                JsonSchema = new LiteLLMJsonSchemaFormat
                {
                    Name = schemaName,
                    Schema = JsonDocument.Parse(schemaJson).RootElement,
                    Strict = strict
                }
            };
            return;
        }

        var explicitJsonMode = parameters != null
            && parameters.TryGetValue(LiteLLMStructuredOutputExtensions.JsonModeKey, out var jsonModeRaw)
            && jsonModeRaw is bool jsonModeFlag
            && jsonModeFlag;

        if (explicitJsonMode || _options.UseStructuredOutputsByDefault)
        {
            apiRequest.ResponseFormat = new LiteLLMResponseFormat { Type = "json_object" };
        }
    }

    private static JsonElement? ParseSchemaOrDefault(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(schemaJson).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CompletionResponse MapToCompletionResponse(LiteLLMChatCompletionResponse apiResponse)
    {
        var choice = apiResponse.Choices.FirstOrDefault();
        var message = choice?.Message;
        var content = message?.Content ?? string.Empty;

        IReadOnlyDictionary<string, object>? metadata = null;
        if (message?.ToolCalls != null && message.ToolCalls.Count > 0)
        {
            var invocations = message.ToolCalls.Select(MapToAgentToolInvocation).ToList();
            metadata = new Dictionary<string, object>
            {
                [LiteLLMToolCallingExtensions.ToolCallsMetadataKey] = invocations
            };
        }

        return new CompletionResponse
        {
            Id = apiResponse.Id,
            Model = apiResponse.Model,
            Content = content,
            FinishReason = MapFinishReason(choice?.FinishReason),
            Usage = new UsageStats
            {
                PromptTokens = apiResponse.Usage?.PromptTokens ?? 0,
                CompletionTokens = apiResponse.Usage?.CompletionTokens ?? 0
            },
            CreatedAt = apiResponse.Created > 0
                ? DateTimeOffset.FromUnixTimeSeconds(apiResponse.Created).UtcDateTime
                : DateTime.UtcNow,
            Metadata = metadata
        };
    }

    private static AgentToolInvocation MapToAgentToolInvocation(LiteLLMToolCall toolCall)
    {
        return new AgentToolInvocation(
            ToolName: toolCall.Function?.Name ?? string.Empty,
            ArgumentsJson: toolCall.Function?.Arguments ?? "{}",
            ResultText: string.Empty,
            IsError: false,
            Latency: TimeSpan.Zero);
    }

    private static CompletionChunk MapToCompletionChunk(LiteLLMStreamChunk chunk, int index)
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

    private static AIModel MapToAIModel(LiteLLMModelInfo model)
    {
        // LiteLLM model ids are provider-prefixed (e.g. "openai/gpt-4o", "anthropic/claude-sonnet-4",
        // "bedrock/anthropic.claude-3-haiku"). Treat the prefix as the provider; fall back to the
        // owned_by field reported by the proxy, then to "litellm".
        var provider = ExtractProvider(model.Id) ?? model.OwnedBy ?? "litellm";

        // Heuristics: provider-prefixed embedding ids contain "embedding"; vision-capable models
        // generally contain "gpt-4o", "vision", "claude-3", "sonnet", "opus", or "gemini" in the id.
        var isEmbedding = model.Id.Contains("embedding", StringComparison.OrdinalIgnoreCase);
        var supportsChat = !isEmbedding;
        var supportsVision = !isEmbedding && ContainsAny(model.Id,
            "gpt-4o", "vision", "claude-3", "claude-sonnet", "claude-opus", "gemini");

        return new AIModel
        {
            Id = model.Id,
            Name = model.Id,
            Provider = provider,
            SupportsStreaming = supportsChat,
            SupportsEmbeddings = isEmbedding,
            SupportsVision = supportsVision,
            SupportsTools = supportsChat
        };
    }

    private static string? ExtractProvider(string modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }
        var slash = modelId.IndexOf('/');
        return slash > 0 ? modelId[..slash] : null;
    }

    private static bool ContainsAny(string source, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (source.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
