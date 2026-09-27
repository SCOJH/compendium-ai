// -----------------------------------------------------------------------
// <copyright file="HuggingFaceAIProvider.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Abstractions.AI.Agents.Models;
using Compendium.Adapters.HuggingFace.Configuration;
using Compendium.Adapters.HuggingFace.Http;
using Compendium.Adapters.HuggingFace.Http.Models;
using Compendium.Adapters.HuggingFace.Tools;

namespace Compendium.Adapters.HuggingFace.Services;

/// <summary>
/// Hugging Face implementation of <see cref="IAIProvider"/>. Chat uses the OpenAI-compatible
/// Messages API (<c>/v1/chat/completions</c>) for both dedicated and serverless modes; embeddings
/// use the <em>feature-extraction</em> task by POSTing inputs to the bare model URL.
/// </summary>
internal sealed class HuggingFaceAIProvider : IAIProvider
{
    private readonly HuggingFaceHttpClient _httpClient;
    private readonly HuggingFaceOptions _options;
    private readonly ILogger<HuggingFaceAIProvider> _logger;

    public HuggingFaceAIProvider(
        HuggingFaceHttpClient httpClient,
        IOptions<HuggingFaceOptions> options,
        ILogger<HuggingFaceAIProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "huggingface";

    /// <inheritdoc />
    public async Task<Result<CompletionResponse>> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = ResolveChatModel(request);
        _logger.LogDebug("Sending HuggingFace chat completion (mode {Mode}, model {Model})",
            _options.Mode, model);

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
        ArgumentNullException.ThrowIfNull(request);

        var model = ResolveChatModel(request);
        _logger.LogDebug("Sending HuggingFace streaming chat completion (mode {Mode}, model {Model})",
            _options.Mode, model);

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

        var model = string.IsNullOrEmpty(request.Model)
            ? _options.DefaultEmbeddingModel
            : request.Model;

        _logger.LogDebug("Sending HuggingFace embeddings request for {Count} inputs (mode {Mode}, model {Model})",
            request.Inputs.Count, _options.Mode, model);

        var payload = new HuggingFaceFeatureExtractionRequest { Inputs = request.Inputs.ToList() };
        var raw = await _httpClient.CreateFeatureExtractionAsync(model, payload, cancellationToken);
        if (raw.IsFailure)
        {
            return Result.Failure<EmbeddingResponse>(raw.Error);
        }

        var parseResult = ParseEmbeddings(raw.Value);
        if (parseResult.IsFailure)
        {
            return Result.Failure<EmbeddingResponse>(parseResult.Error);
        }

        return Result.Success(new EmbeddingResponse
        {
            Model = model,
            Embeddings = parseResult.Value,
            Usage = new EmbeddingUsage { PromptTokens = 0 }
        });
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AIModel>>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        // HF Inference Endpoints don't expose a generic /models listing (each endpoint is bound
        // to a single model, and the serverless catalog is the entire Hugging Face Hub).
        // Surface a single descriptor for the currently configured default chat model so callers
        // can introspect the active provider deterministically.
        await Task.CompletedTask;

        var model = _options.DefaultChatModel;
        var descriptor = new AIModel
        {
            Id = model,
            Name = model,
            Provider = "huggingface",
            SupportsStreaming = true,
            SupportsEmbeddings = false,
            SupportsVision = false,
            SupportsTools = true
        };
        return Result.Success<IReadOnlyList<AIModel>>(new[] { descriptor });
    }

    /// <inheritdoc />
    public async Task<Result> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        // Health check sends a no-op embedding probe (cheap, never bills a chat completion).
        try
        {
            var model = _options.DefaultEmbeddingModel;
            var payload = new HuggingFaceFeatureExtractionRequest { Inputs = new List<string> { "ping" } };
            var raw = await _httpClient.CreateFeatureExtractionAsync(model, payload, cancellationToken);
            return raw.IsSuccess ? Result.Success() : Result.Failure(raw.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check failed for HuggingFace provider");
            return Result.Failure(AIErrors.ProviderUnavailable("huggingface"));
        }
    }

    private string ResolveChatModel(CompletionRequest request)
    {
        if (!string.IsNullOrEmpty(request.Model))
        {
            return request.Model;
        }
        return _options.DefaultChatModel;
    }

    private HuggingFaceChatCompletionRequest MapToApiRequest(
        CompletionRequest request,
        string model,
        bool stream)
    {
        var messages = new List<HuggingFaceChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new HuggingFaceChatMessage { Role = "system", Content = request.SystemPrompt });
        }

        foreach (var msg in request.Messages)
        {
            messages.Add(new HuggingFaceChatMessage
            {
                Role = msg.Role.ToString().ToLowerInvariant(),
                Content = msg.Content,
                Name = msg.Name
            });
        }

        var apiRequest = new HuggingFaceChatCompletionRequest
        {
            Model = model,
            Messages = messages,
            Temperature = request.Temperature ?? CompletionRequest.FallbackTemperature,
            MaxTokens = request.MaxTokens ?? _options.DefaultMaxTokens,
            TopP = request.TopP,
            FrequencyPenalty = request.FrequencyPenalty,
            PresencePenalty = request.PresencePenalty,
            Stop = request.StopSequences?.ToList(),
            Stream = stream
        };

        ApplyTools(apiRequest, request);
        return apiRequest;
    }

    /// <summary>
    /// Best-effort tool pass-through. Most TGI-backed endpoints accept these fields; non-TGI
    /// endpoints will silently ignore them, which is by design (HF returns 200 with a normal
    /// assistant message).
    /// </summary>
    private static void ApplyTools(HuggingFaceChatCompletionRequest apiRequest, CompletionRequest request)
    {
        if (request.AdditionalParameters == null)
        {
            return;
        }

        if (request.AdditionalParameters.TryGetValue(HuggingFaceToolCallingExtensions.ToolsKey, out var toolsRaw)
            && toolsRaw is IReadOnlyList<AgentTool> tools
            && tools.Count > 0)
        {
            apiRequest.Tools = tools.Select(t => new HuggingFaceToolDefinition
            {
                Function = new HuggingFaceFunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = ParseSchemaOrDefault(t.InputSchemaJson)
                }
            }).ToList();
        }

        if (request.AdditionalParameters.TryGetValue(HuggingFaceToolCallingExtensions.ToolChoiceKey, out var choiceRaw)
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

    private static CompletionResponse MapToCompletionResponse(HuggingFaceChatCompletionResponse apiResponse)
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
                [HuggingFaceToolCallingExtensions.ToolCallsMetadataKey] = invocations
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

    private static AgentToolInvocation MapToAgentToolInvocation(HuggingFaceToolCall toolCall)
    {
        return new AgentToolInvocation(
            ToolName: toolCall.Function?.Name ?? string.Empty,
            ArgumentsJson: toolCall.Function?.Arguments ?? "{}",
            ResultText: string.Empty,
            IsError: false,
            Latency: TimeSpan.Zero);
    }

    private static CompletionChunk MapToCompletionChunk(HuggingFaceStreamChunk chunk, int index)
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
        "stop" or "eos_token" => FinishReason.Stop,
        "length" => FinishReason.Length,
        "content_filter" => FinishReason.ContentFilter,
        "tool_calls" or "function_call" => FinishReason.ToolCall,
        null => FinishReason.InProgress,
        _ => FinishReason.Other
    };

    /// <summary>
    /// Parses the response body from the feature-extraction task. HF returns either a single
    /// embedding (<c>[ 0.1, 0.2, … ]</c>) for a single-input request, or a batch
    /// (<c>[ [ 0.1, … ], [ 0.3, … ] ]</c>) for a multi-input request. Some sentence-transformers
    /// endpoints can also return token-level matrices (<c>number[][][]</c>) which we mean-pool
    /// to a single vector per input so the abstraction's <see cref="Embedding"/> shape always
    /// holds.
    /// </summary>
    private static Result<List<Embedding>> ParseEmbeddings(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            return Result.Failure<List<Embedding>>(
                AIErrors.ProviderError($"Failed to parse embeddings JSON: {ex.Message}"));
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                return Result.Failure<List<Embedding>>(
                    AIErrors.ProviderError("Embeddings response was not a JSON array."));
            }

            var first = root.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Number)
            {
                // Single vector — read the whole root as one embedding.
                var vector = ReadFloatArray(root);
                return Result.Success(new List<Embedding>
                {
                    new() { Index = 0, Vector = vector }
                });
            }

            if (first.ValueKind != JsonValueKind.Array)
            {
                return Result.Failure<List<Embedding>>(
                    AIErrors.ProviderError("Unexpected embedding response shape."));
            }

            // Either number[][] (batch of vectors) or number[][][] (batch of token-matrices).
            var embeddings = new List<Embedding>();
            var i = 0;
            foreach (var item in root.EnumerateArray())
            {
                var sample = item.EnumerateArray().FirstOrDefault();
                float[] vector;
                if (sample.ValueKind == JsonValueKind.Number)
                {
                    vector = ReadFloatArray(item);
                }
                else if (sample.ValueKind == JsonValueKind.Array)
                {
                    vector = MeanPool(item);
                }
                else
                {
                    return Result.Failure<List<Embedding>>(
                        AIErrors.ProviderError("Unexpected embedding row shape."));
                }
                embeddings.Add(new Embedding { Index = i++, Vector = vector });
            }
            return Result.Success(embeddings);
        }
    }

    private static float[] ReadFloatArray(JsonElement array)
    {
        var len = array.GetArrayLength();
        var result = new float[len];
        var i = 0;
        foreach (var el in array.EnumerateArray())
        {
            result[i++] = el.GetSingle();
        }
        return result;
    }

    private static float[] MeanPool(JsonElement tokenMatrix)
    {
        // tokenMatrix is number[][] — average across the leading axis.
        var tokens = tokenMatrix.EnumerateArray().ToList();
        if (tokens.Count == 0)
        {
            return Array.Empty<float>();
        }

        var dim = tokens[0].GetArrayLength();
        var sums = new double[dim];
        foreach (var token in tokens)
        {
            var j = 0;
            foreach (var el in token.EnumerateArray())
            {
                sums[j++] += el.GetDouble();
            }
        }

        var result = new float[dim];
        for (var k = 0; k < dim; k++)
        {
            result[k] = (float)(sums[k] / tokens.Count);
        }
        return result;
    }
}
