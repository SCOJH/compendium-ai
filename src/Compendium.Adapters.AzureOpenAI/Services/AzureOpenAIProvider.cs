// -----------------------------------------------------------------------
// <copyright file="AzureOpenAIProvider.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Abstractions.AI.Agents.Models;
using Compendium.Adapters.AzureOpenAI.Configuration;
using Compendium.Adapters.AzureOpenAI.Http;
using Compendium.Adapters.AzureOpenAI.Http.Models;
using Compendium.Adapters.AzureOpenAI.StructuredOutputs;
using Compendium.Adapters.AzureOpenAI.Tools;
using Compendium.Adapters.AzureOpenAI.Vision;

namespace Compendium.Adapters.AzureOpenAI.Services;

/// <summary>
/// Azure OpenAI Service implementation of <see cref="IAIProvider"/>. Routes Compendium calls
/// to per-resource deployments and surfaces Azure content-filter blocks as
/// <see cref="AIErrors.ContentFiltered"/> failures.
/// </summary>
internal sealed class AzureOpenAIProvider : IAIProvider
{
    private static readonly HashSet<string> KnownEmbeddingModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "text-embedding-3-small",
        "text-embedding-3-large",
        "text-embedding-ada-002"
    };

    private readonly AzureOpenAIHttpClient _httpClient;
    private readonly AzureOpenAIOptions _options;
    private readonly ILogger<AzureOpenAIProvider> _logger;

    public AzureOpenAIProvider(
        AzureOpenAIHttpClient httpClient,
        IOptions<AzureOpenAIOptions> options,
        ILogger<AzureOpenAIProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "azure-openai";

    /// <inheritdoc />
    public async Task<Result<CompletionResponse>> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;
        var deployment = _options.ResolveDeployment(model);
        _logger.LogDebug("Sending Azure OpenAI chat completion to deployment {Deployment} (model {Model})", deployment, model);

        var apiRequest = MapToApiRequest(request, stream: false);
        var result = await _httpClient.CreateChatCompletionAsync(deployment, apiRequest, cancellationToken);
        return result.Match(
            r => MapToCompletionResultWithFilter(r, model),
            error => Result.Failure<CompletionResponse>(error));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<CompletionChunk>> StreamCompleteAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;
        var deployment = _options.ResolveDeployment(model);
        _logger.LogDebug("Sending Azure OpenAI streaming completion to deployment {Deployment} (model {Model})", deployment, model);

        var apiRequest = MapToApiRequest(request, stream: true);

        var index = 0;
        await foreach (var chunk in _httpClient.StreamChatCompletionAsync(deployment, apiRequest, cancellationToken))
        {
            if (chunk.IsFailure)
            {
                yield return Result.Failure<CompletionChunk>(chunk.Error);
                yield break;
            }

            // Surface a content-filter block emitted mid-stream as a failure.
            var filterError = TryMapStreamContentFilter(chunk.Value);
            if (filterError is not null)
            {
                yield return Result.Failure<CompletionChunk>(filterError);
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
        var deployment = _options.ResolveDeployment(model);
        var batchSize = Math.Max(1, _options.MaxEmbeddingsBatchSize);
        _logger.LogDebug(
            "Sending Azure OpenAI embeddings request for {Count} inputs (batch size {Batch}, deployment {Deployment})",
            request.Inputs.Count,
            batchSize,
            deployment);

        var aggregated = new List<Embedding>(request.Inputs.Count);
        var totalPromptTokens = 0;

        for (var offset = 0; offset < request.Inputs.Count; offset += batchSize)
        {
            var slice = request.Inputs.Skip(offset).Take(batchSize).ToList();
            var batchRequest = new AzureOpenAIEmbeddingsRequest
            {
                Input = slice,
                Dimensions = request.Dimensions,
                User = request.UserId
            };

            var result = await _httpClient.CreateEmbeddingsAsync(deployment, batchRequest, cancellationToken);
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
    public Task<Result<IReadOnlyList<AIModel>>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        // Azure OpenAI has no per-resource models discovery endpoint that mirrors OpenAI's /models.
        // We synthesise the catalogue from the configured deployment mapping. Callers using the
        // default model only will still see at least one row.
        cancellationToken.ThrowIfCancellationRequested();

        var rows = new List<AIModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in _options.DeploymentMapping)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || !seen.Add(kvp.Key))
            {
                continue;
            }
            rows.Add(BuildModelRow(kvp.Key));
        }

        if (!string.IsNullOrWhiteSpace(_options.DefaultModel) && seen.Add(_options.DefaultModel))
        {
            rows.Add(BuildModelRow(_options.DefaultModel));
        }

        if (!string.IsNullOrWhiteSpace(_options.DefaultEmbeddingModel) && seen.Add(_options.DefaultEmbeddingModel))
        {
            rows.Add(BuildModelRow(_options.DefaultEmbeddingModel));
        }

        return Task.FromResult(Result.Success<IReadOnlyList<AIModel>>(rows));
    }

    /// <inheritdoc />
    public async Task<Result> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var deployment = _options.ResolveDeployment(_options.DefaultModel);
            if (string.IsNullOrWhiteSpace(deployment))
            {
                return Result.Failure(AIErrors.ProviderUnavailable("azure-openai"));
            }

            var result = await _httpClient.PingDeploymentAsync(deployment, cancellationToken);
            return result;
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check failed for Azure OpenAI provider");
            return Result.Failure(AIErrors.ProviderUnavailable("azure-openai"));
        }
    }

    private AzureOpenAIChatCompletionRequest MapToApiRequest(CompletionRequest request, bool stream)
    {
        var messages = new List<AzureOpenAIChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new AzureOpenAIChatMessage { Role = "system", Content = request.SystemPrompt });
        }
        foreach (var msg in request.Messages)
        {
            messages.Add(new AzureOpenAIChatMessage
            {
                Role = msg.Role.ToString().ToLowerInvariant(),
                Content = msg.Content,
                Name = msg.Name
            });
        }

        ApplyImagesToLastUserMessage(messages, request);

        var apiRequest = new AzureOpenAIChatCompletionRequest
        {
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
            apiRequest.StreamOptions = new AzureOpenAIStreamOptions { IncludeUsage = true };
        }

        ApplyTools(apiRequest, request);
        ApplyResponseFormat(apiRequest, request);
        return apiRequest;
    }

    private static void ApplyImagesToLastUserMessage(
        List<AzureOpenAIChatMessage> messages,
        CompletionRequest request)
    {
        if (request.AdditionalParameters == null
            || !request.AdditionalParameters.TryGetValue(AzureOpenAIVisionExtensions.ImagesKey, out var raw)
            || raw is not IReadOnlyList<AzureOpenAIImageInput> images
            || images.Count == 0)
        {
            return;
        }

        // Find the last user message and rewrite its content into the multimodal shape.
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existingText = messages[i].Content as string;
            var parts = new List<AzureOpenAIContentPart>();
            if (!string.IsNullOrEmpty(existingText))
            {
                parts.Add(new AzureOpenAIContentPart { Type = "text", Text = existingText });
            }
            foreach (var img in images)
            {
                parts.Add(new AzureOpenAIContentPart
                {
                    Type = "image_url",
                    ImageUrl = new AzureOpenAIImageUrl { Url = img.Url, Detail = img.Detail }
                });
            }
            messages[i].Content = parts;
            return;
        }
    }

    private static void ApplyTools(AzureOpenAIChatCompletionRequest apiRequest, CompletionRequest request)
    {
        if (request.AdditionalParameters == null)
        {
            return;
        }

        if (request.AdditionalParameters.TryGetValue(AzureOpenAIToolCallingExtensions.ToolsKey, out var toolsRaw)
            && toolsRaw is IReadOnlyList<AgentTool> tools
            && tools.Count > 0)
        {
            apiRequest.Tools = tools.Select(t => new AzureOpenAIToolDefinition
            {
                Function = new AzureOpenAIFunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = ParseSchemaOrDefault(t.InputSchemaJson)
                }
            }).ToList();
        }

        if (request.AdditionalParameters.TryGetValue(AzureOpenAIToolCallingExtensions.ToolChoiceKey, out var choiceRaw)
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

    private void ApplyResponseFormat(AzureOpenAIChatCompletionRequest apiRequest, CompletionRequest request)
    {
        var parameters = request.AdditionalParameters;
        if (parameters != null
            && parameters.TryGetValue(AzureOpenAIStructuredOutputExtensions.SchemaKey, out var schemaRaw)
            && schemaRaw is string schemaJson
            && !string.IsNullOrWhiteSpace(schemaJson))
        {
            var schemaName = parameters.TryGetValue(AzureOpenAIStructuredOutputExtensions.SchemaNameKey, out var nameRaw)
                && nameRaw is string s
                ? s
                : "response";
            var strict = !parameters.TryGetValue(AzureOpenAIStructuredOutputExtensions.StrictKey, out var strictRaw)
                || strictRaw is not bool b
                || b;

            apiRequest.ResponseFormat = new AzureOpenAIResponseFormat
            {
                Type = "json_schema",
                JsonSchema = new AzureOpenAIJsonSchemaFormat
                {
                    Name = schemaName,
                    Schema = JsonDocument.Parse(schemaJson).RootElement,
                    Strict = strict
                }
            };
            return;
        }

        var explicitJsonMode = parameters != null
            && parameters.TryGetValue(AzureOpenAIStructuredOutputExtensions.JsonModeKey, out var jsonModeRaw)
            && jsonModeRaw is bool jsonModeFlag
            && jsonModeFlag;

        if (explicitJsonMode || _options.UseStructuredOutputsByDefault)
        {
            apiRequest.ResponseFormat = new AzureOpenAIResponseFormat { Type = "json_object" };
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

    private static Result<CompletionResponse> MapToCompletionResultWithFilter(
        AzureOpenAIChatCompletionResponse apiResponse,
        string requestedModel)
    {
        // Azure may return 200 OK with a finish_reason="content_filter" choice when the completion
        // is partially blocked. Surface that as a failure with the canonical ContentFiltered error.
        var choice = apiResponse.Choices.FirstOrDefault();
        var filteredCategories = choice?.ContentFilterResults?.FilteredCategories() ?? new List<string>();

        if (string.Equals(choice?.FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase)
            || filteredCategories.Count > 0)
        {
            var reason = filteredCategories.Count > 0
                ? $"Azure content filter blocked categories: {string.Join(", ", filteredCategories)}"
                : "Azure content filter blocked the completion.";
            return Result.Failure<CompletionResponse>(AIErrors.ContentFiltered(reason));
        }

        return Result.Success(MapToCompletionResponse(apiResponse, requestedModel));
    }

    private static Error? TryMapStreamContentFilter(AzureOpenAIStreamChunk chunk)
    {
        var choice = chunk.Choices.FirstOrDefault();
        if (choice == null)
        {
            return null;
        }
        var filteredCategories = choice.ContentFilterResults?.FilteredCategories() ?? new List<string>();
        if (string.Equals(choice.FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase)
            || filteredCategories.Count > 0)
        {
            var reason = filteredCategories.Count > 0
                ? $"Azure content filter blocked categories: {string.Join(", ", filteredCategories)}"
                : "Azure content filter blocked the streamed completion.";
            return AIErrors.ContentFiltered(reason);
        }
        return null;
    }

    private static CompletionResponse MapToCompletionResponse(
        AzureOpenAIChatCompletionResponse apiResponse,
        string requestedModel)
    {
        var choice = apiResponse.Choices.FirstOrDefault();
        var message = choice?.Message;
        var content = ExtractContentText(message?.Content);

        IReadOnlyDictionary<string, object>? metadata = null;
        if (message?.ToolCalls != null && message.ToolCalls.Count > 0)
        {
            var invocations = message.ToolCalls.Select(MapToAgentToolInvocation).ToList();
            metadata = new Dictionary<string, object>
            {
                [AzureOpenAIToolCallingExtensions.ToolCallsMetadataKey] = invocations
            };
        }

        return new CompletionResponse
        {
            Id = apiResponse.Id,
            // Azure echoes back the deployment id in "model"; keep the caller's logical name when present.
            Model = string.IsNullOrEmpty(apiResponse.Model) ? requestedModel : apiResponse.Model,
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

    private static string ExtractContentText(object? content)
    {
        return content switch
        {
            null => string.Empty,
            string s => s,
            JsonElement el => el.ValueKind == JsonValueKind.String
                ? (el.GetString() ?? string.Empty)
                : el.ToString(),
            _ => content.ToString() ?? string.Empty
        };
    }

    private static AgentToolInvocation MapToAgentToolInvocation(AzureOpenAIToolCall toolCall)
    {
        return new AgentToolInvocation(
            ToolName: toolCall.Function?.Name ?? string.Empty,
            ArgumentsJson: toolCall.Function?.Arguments ?? "{}",
            ResultText: string.Empty,
            IsError: false,
            Latency: TimeSpan.Zero);
    }

    private static CompletionChunk MapToCompletionChunk(AzureOpenAIStreamChunk chunk, int index)
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

    private static AIModel BuildModelRow(string modelId)
    {
        var supportsEmbeddings = KnownEmbeddingModels.Contains(modelId);
        var supportsChat = !supportsEmbeddings;
        return new AIModel
        {
            Id = modelId,
            Name = modelId,
            Provider = "azure-openai",
            SupportsStreaming = supportsChat,
            SupportsEmbeddings = supportsEmbeddings,
            SupportsVision = modelId.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase)
                || modelId.Contains("vision", StringComparison.OrdinalIgnoreCase),
            SupportsTools = supportsChat
        };
    }
}
