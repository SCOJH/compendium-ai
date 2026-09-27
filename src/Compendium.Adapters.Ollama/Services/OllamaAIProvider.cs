// -----------------------------------------------------------------------
// <copyright file="OllamaAIProvider.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Abstractions.AI.Agents.Models;
using Compendium.Adapters.Ollama.Configuration;
using Compendium.Adapters.Ollama.Http;
using Compendium.Adapters.Ollama.Http.Models;
using Compendium.Adapters.Ollama.Tools;

namespace Compendium.Adapters.Ollama.Services;

/// <summary>
/// Ollama implementation of <see cref="IAIProvider"/>. Routes chat, streaming, embeddings,
/// model discovery, and health checks through a local Ollama server (default
/// <c>http://localhost:11434</c>).
/// </summary>
internal sealed class OllamaAIProvider : IAIProvider
{
    private readonly OllamaHttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaAIProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaAIProvider"/> class.
    /// </summary>
    public OllamaAIProvider(
        OllamaHttpClient httpClient,
        IOptions<OllamaOptions> options,
        ILogger<OllamaAIProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "ollama";

    /// <inheritdoc />
    public async Task<Result<CompletionResponse>> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;

        _logger.LogDebug("Sending Ollama chat completion to model {Model}", model);

        var apiRequest = MapToApiRequest(request, model, stream: false);

        var result = await _httpClient.ChatAsync(apiRequest, cancellationToken);

        // Auto-pull on "model not found", if enabled.
        if (result.IsFailure
            && _options.AutoPullModel
            && result.Error.Code == "AI.ModelNotFound")
        {
            _logger.LogInformation("Model {Model} not found on Ollama — attempting auto-pull", model);
            var pull = await _httpClient.PullModelAsync(model, cancellationToken);
            if (pull.IsFailure)
            {
                return Result.Failure<CompletionResponse>(pull.Error);
            }
            result = await _httpClient.ChatAsync(apiRequest, cancellationToken);
        }

        return result.Match(
            r => Result.Success(MapToCompletionResponse(r, model)),
            error => Result.Failure<CompletionResponse>(error));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<CompletionChunk>> StreamCompleteAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;

        _logger.LogDebug("Sending Ollama streaming chat completion to model {Model}", model);

        var apiRequest = MapToApiRequest(request, model, stream: true);

        var index = 0;
        await foreach (var chunkResult in _httpClient.ChatStreamAsync(apiRequest, cancellationToken))
        {
            if (chunkResult.IsFailure)
            {
                yield return Result.Failure<CompletionChunk>(chunkResult.Error);
                yield break;
            }

            var completionChunk = MapToCompletionChunk(chunkResult.Value, model, index++);
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

        _logger.LogDebug(
            "Sending Ollama embeddings request for {Count} inputs (model {Model})",
            request.Inputs.Count,
            model);

        // Try the newer batched endpoint first.
        var embedRequest = new OllamaEmbedRequest
        {
            Model = model,
            Input = request.Inputs.ToList(),
            KeepAlive = _options.KeepAlive,
        };

        var batchResult = await _httpClient.EmbedAsync(embedRequest, cancellationToken);

        // Auto-pull if missing.
        if (batchResult.IsFailure
            && _options.AutoPullModel
            && batchResult.Error.Code == "AI.ModelNotFound")
        {
            _logger.LogInformation(
                "Embedding model {Model} not found on Ollama — attempting auto-pull",
                model);
            var pull = await _httpClient.PullModelAsync(model, cancellationToken);
            if (pull.IsFailure)
            {
                return Result.Failure<EmbeddingResponse>(pull.Error);
            }
            batchResult = await _httpClient.EmbedAsync(embedRequest, cancellationToken);
        }

        if (batchResult.IsSuccess)
        {
            var embeddings = batchResult.Value.Embeddings
                .Select((vec, i) => new Embedding { Index = i, Vector = vec })
                .ToList();
            return Result.Success(new EmbeddingResponse
            {
                Model = batchResult.Value.Model,
                Embeddings = embeddings,
                Usage = new EmbeddingUsage
                {
                    PromptTokens = batchResult.Value.PromptEvalCount ?? 0,
                },
            });
        }

        // Fallback to legacy single-prompt endpoint when /api/embed isn't supported
        // (older Ollama builds return 404 on the route itself, distinct from "model not found").
        if (batchResult.Error.Code is "AI.ProviderError" or "AI.ProviderUnavailable")
        {
            _logger.LogDebug("Falling back to legacy /api/embeddings endpoint");
            return await EmbedLegacyAsync(model, request.Inputs, cancellationToken);
        }

        return Result.Failure<EmbeddingResponse>(batchResult.Error);
    }

    private async Task<Result<EmbeddingResponse>> EmbedLegacyAsync(
        string model,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        var aggregated = new List<Embedding>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var legacyRequest = new OllamaLegacyEmbeddingsRequest
            {
                Model = model,
                Prompt = inputs[i],
                KeepAlive = _options.KeepAlive,
            };

            var legacyResult = await _httpClient.EmbedLegacyAsync(legacyRequest, cancellationToken);
            if (legacyResult.IsFailure)
            {
                return Result.Failure<EmbeddingResponse>(legacyResult.Error);
            }

            aggregated.Add(new Embedding
            {
                Index = i,
                Vector = legacyResult.Value.Embedding,
            });
        }

        return Result.Success(new EmbeddingResponse
        {
            Model = model,
            Embeddings = aggregated,
            Usage = new EmbeddingUsage { PromptTokens = 0 },
        });
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AIModel>>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching local models from Ollama");
        var result = await _httpClient.ListModelsAsync(cancellationToken);
        return result.Match(
            tags => Result.Success<IReadOnlyList<AIModel>>(tags.Select(MapToAIModel).ToList()),
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
            _logger.LogWarning(ex, "Health check failed for Ollama provider");
            return Result.Failure(AIErrors.ProviderUnavailable("ollama"));
        }
    }

    private OllamaChatRequest MapToApiRequest(CompletionRequest request, string model, bool stream)
    {
        var messages = new List<OllamaChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new OllamaChatMessage { Role = "system", Content = request.SystemPrompt });
        }

        foreach (var msg in request.Messages)
        {
            messages.Add(new OllamaChatMessage
            {
                Role = MapRole(msg.Role),
                Content = msg.Content,
            });
        }

        var apiRequest = new OllamaChatRequest
        {
            Model = model,
            Messages = messages,
            Stream = stream,
            KeepAlive = _options.KeepAlive,
            Options = BuildOptions(request),
        };

        ApplyTools(apiRequest, request);
        return apiRequest;
    }

    private OllamaRequestOptions BuildOptions(CompletionRequest request)
    {
        var maxTokens = request.MaxTokens ?? _options.DefaultMaxTokens;
        return new OllamaRequestOptions
        {
            Temperature = request.Temperature ?? CompletionRequest.FallbackTemperature,
            TopP = request.TopP,
            FrequencyPenalty = request.FrequencyPenalty,
            PresencePenalty = request.PresencePenalty,
            NumPredict = maxTokens != 0 ? maxTokens : null,
            Stop = request.StopSequences?.Count > 0 ? request.StopSequences.ToList() : null,
        };
    }

    private static void ApplyTools(OllamaChatRequest apiRequest, CompletionRequest request)
    {
        if (request.AdditionalParameters == null)
        {
            return;
        }

        if (request.AdditionalParameters.TryGetValue(OllamaToolCallingExtensions.ToolsKey, out var toolsRaw)
            && toolsRaw is IReadOnlyList<AgentTool> tools
            && tools.Count > 0)
        {
            apiRequest.Tools = tools.Select(t => new OllamaToolDefinition
            {
                Function = new OllamaFunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = ParseSchemaOrDefault(t.InputSchemaJson),
                },
            }).ToList();
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
            return JsonDocument.Parse(schemaJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string MapRole(MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.Tool => "tool",
        _ => role.ToString().ToLowerInvariant(),
    };

    private static CompletionResponse MapToCompletionResponse(OllamaChatResponse api, string fallbackModel)
    {
        var message = api.Message;
        var content = message?.Content ?? string.Empty;

        IReadOnlyDictionary<string, object>? metadata = null;
        if (message?.ToolCalls != null && message.ToolCalls.Count > 0)
        {
            var invocations = message.ToolCalls
                .Select(MapToAgentToolInvocation)
                .ToList();
            metadata = new Dictionary<string, object>
            {
                [OllamaToolCallingExtensions.ToolCallsMetadataKey] = invocations,
            };
        }

        var hasToolCalls = message?.ToolCalls != null && message.ToolCalls.Count > 0;

        return new CompletionResponse
        {
            Id = api.CreatedAt ?? string.Empty, // Ollama has no request id; use the timestamp.
            Model = string.IsNullOrEmpty(api.Model) ? fallbackModel : api.Model,
            Content = content,
            FinishReason = MapFinishReason(api.DoneReason, api.Done, hasToolCalls),
            Usage = new UsageStats
            {
                PromptTokens = api.PromptEvalCount ?? 0,
                CompletionTokens = api.EvalCount ?? 0,
            },
            CreatedAt = TryParseCreatedAt(api.CreatedAt) ?? DateTime.UtcNow,
            Metadata = metadata,
        };
    }

    private static AgentToolInvocation MapToAgentToolInvocation(OllamaToolCall toolCall)
    {
        var args = toolCall.Function?.Arguments?.GetRawText() ?? "{}";
        return new AgentToolInvocation(
            ToolName: toolCall.Function?.Name ?? string.Empty,
            ArgumentsJson: args,
            ResultText: string.Empty,
            IsError: false,
            Latency: TimeSpan.Zero);
    }

    private static CompletionChunk MapToCompletionChunk(OllamaChatResponse api, string fallbackModel, int index)
    {
        var hasToolCalls = api.Message?.ToolCalls != null && api.Message.ToolCalls.Count > 0;
        UsageStats? usage = null;
        if (api.Done)
        {
            usage = new UsageStats
            {
                PromptTokens = api.PromptEvalCount ?? 0,
                CompletionTokens = api.EvalCount ?? 0,
            };
        }

        return new CompletionChunk
        {
            Id = api.CreatedAt ?? string.Empty,
            ContentDelta = api.Message?.Content ?? string.Empty,
            Index = index,
            IsFinal = api.Done,
            FinishReason = api.Done ? MapFinishReason(api.DoneReason, api.Done, hasToolCalls) : null,
            Usage = usage,
        };
    }

    private static FinishReason MapFinishReason(string? reason, bool done, bool hasToolCalls)
    {
        if (hasToolCalls)
        {
            return FinishReason.ToolCall;
        }

        return reason?.ToLowerInvariant() switch
        {
            "stop" => FinishReason.Stop,
            "length" => FinishReason.Length,
            "load" => FinishReason.Other,
            "unload" => FinishReason.Other,
            null when !done => FinishReason.InProgress,
            null => FinishReason.Stop,
            _ => FinishReason.Other,
        };
    }

    private static DateTime? TryParseCreatedAt(string? createdAt)
    {
        if (string.IsNullOrEmpty(createdAt))
        {
            return null;
        }
        return DateTime.TryParse(createdAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime()
            : null;
    }

    private static AIModel MapToAIModel(OllamaTagModel tag)
    {
        var name = tag.Name;
        var family = tag.Details?.Family ?? ExtractFamily(name);
        var isEmbeddingModel = LooksLikeEmbeddingModel(name, family);
        return new AIModel
        {
            Id = name,
            Name = name,
            Provider = "ollama",
            SupportsStreaming = !isEmbeddingModel,
            SupportsEmbeddings = isEmbeddingModel,
            SupportsVision = LooksLikeVisionModel(name, family),
            SupportsTools = !isEmbeddingModel && LooksLikeToolCapable(name),
            Metadata = BuildModelMetadata(tag),
        };
    }

    private static Dictionary<string, object>? BuildModelMetadata(OllamaTagModel tag)
    {
        var metadata = new Dictionary<string, object>();
        if (tag.Size.HasValue)
        {
            metadata["size_bytes"] = tag.Size.Value;
        }
        if (!string.IsNullOrEmpty(tag.Digest))
        {
            metadata["digest"] = tag.Digest;
        }
        if (tag.Details?.ParameterSize is { Length: > 0 } parameterSize)
        {
            metadata["parameter_size"] = parameterSize;
        }
        if (tag.Details?.QuantizationLevel is { Length: > 0 } quant)
        {
            metadata["quantization_level"] = quant;
        }
        return metadata.Count == 0 ? null : metadata;
    }

    private static string ExtractFamily(string id)
    {
        var colon = id.IndexOf(':');
        return colon > 0 ? id[..colon] : id;
    }

    private static bool LooksLikeEmbeddingModel(string id, string family)
    {
        var combined = $"{id} {family}".ToLowerInvariant();
        return combined.Contains("embed")
            || combined.Contains("nomic-embed")
            || combined.Contains("mxbai-embed")
            || combined.Contains("snowflake-arctic-embed")
            || combined.Contains("bge-m3");
    }

    private static bool LooksLikeVisionModel(string id, string family)
    {
        var combined = $"{id} {family}".ToLowerInvariant();
        return combined.Contains("llava")
            || combined.Contains("bakllava")
            || combined.Contains("vision")
            || combined.Contains("moondream");
    }

    private static bool LooksLikeToolCapable(string id)
    {
        var lowered = id.ToLowerInvariant();
        return lowered.Contains("llama3.2")
            || lowered.Contains("llama3.3")
            || lowered.Contains("llama4")
            || lowered.Contains("qwen2.5")
            || lowered.Contains("qwen3")
            || lowered.Contains("mistral-nemo")
            || lowered.Contains("mistral-large")
            || lowered.Contains("mixtral")
            || lowered.Contains("command-r");
    }
}
