// -----------------------------------------------------------------------
// <copyright file="GeminiAIProvider.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Abstractions.AI.Agents.Models;
using Compendium.Adapters.Gemini.Configuration;
using Compendium.Adapters.Gemini.Http;
using Compendium.Adapters.Gemini.Http.Models;
using Compendium.Adapters.Gemini.StructuredOutputs;
using Compendium.Adapters.Gemini.Tools;

namespace Compendium.Adapters.Gemini.Services;

/// <summary>
/// Google Gemini implementation of <see cref="IAIProvider"/>. Provides chat completions, streaming,
/// embeddings, tool calling, structured outputs, and vision via Gemini's public REST API
/// (<c>generativelanguage.googleapis.com</c>).
/// </summary>
internal sealed class GeminiAIProvider : IAIProvider
{
    private readonly GeminiHttpClient _httpClient;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiAIProvider> _logger;

    public GeminiAIProvider(
        GeminiHttpClient httpClient,
        IOptions<GeminiOptions> options,
        ILogger<GeminiAIProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "gemini";

    /// <inheritdoc />
    public async Task<Result<CompletionResponse>> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;
        _logger.LogDebug("Sending Gemini generateContent to model {Model}", model);

        var apiRequest = MapToApiRequest(request);
        var result = await _httpClient.GenerateContentAsync(model, apiRequest, cancellationToken);
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
        _logger.LogDebug("Sending Gemini streamGenerateContent to model {Model}", model);

        var apiRequest = MapToApiRequest(request);

        var index = 0;
        await foreach (var chunk in _httpClient.StreamGenerateContentAsync(model, apiRequest, cancellationToken))
        {
            if (chunk.IsFailure)
            {
                yield return Result.Failure<CompletionChunk>(chunk.Error);
                yield break;
            }

            var completionChunk = MapToCompletionChunk(chunk.Value, index++, model);
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
            "Sending Gemini embeddings request for {Count} inputs (batch size {Batch}, model {Model})",
            request.Inputs.Count,
            batchSize,
            model);

        // Promote the bare model id to the fully-qualified form Gemini expects in the
        // `requests[].model` field of batchEmbedContents.
        var qualifiedModel = model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? model
            : $"models/{model}";

        var aggregated = new List<Embedding>(request.Inputs.Count);

        if (request.Inputs.Count == 1)
        {
            var single = new GeminiEmbedContentRequest
            {
                Model = qualifiedModel,
                Content = new GeminiContent
                {
                    Parts = new List<GeminiPart> { new() { Text = request.Inputs[0] } }
                },
                OutputDimensionality = request.Dimensions
            };
            var result = await _httpClient.EmbedContentAsync(model, single, cancellationToken);
            if (result.IsFailure)
            {
                return Result.Failure<EmbeddingResponse>(result.Error);
            }
            aggregated.Add(new Embedding
            {
                Index = 0,
                Vector = result.Value.Embedding?.Values ?? Array.Empty<float>()
            });
        }
        else
        {
            for (var offset = 0; offset < request.Inputs.Count; offset += batchSize)
            {
                var slice = request.Inputs.Skip(offset).Take(batchSize).ToList();
                var batchRequest = new GeminiBatchEmbedContentsRequest
                {
                    Requests = slice.Select(text => new GeminiEmbedContentRequest
                    {
                        Model = qualifiedModel,
                        Content = new GeminiContent
                        {
                            Parts = new List<GeminiPart> { new() { Text = text } }
                        },
                        OutputDimensionality = request.Dimensions
                    }).ToList()
                };

                var result = await _httpClient.BatchEmbedContentsAsync(model, batchRequest, cancellationToken);
                if (result.IsFailure)
                {
                    return Result.Failure<EmbeddingResponse>(result.Error);
                }

                var batchOffset = offset;
                for (var i = 0; i < result.Value.Embeddings.Count; i++)
                {
                    aggregated.Add(new Embedding
                    {
                        Index = batchOffset + i,
                        Vector = result.Value.Embeddings[i].Values
                    });
                }
            }
        }

        return Result.Success(new EmbeddingResponse
        {
            Model = model,
            Embeddings = aggregated,
            Usage = new EmbeddingUsage { PromptTokens = 0 }
        });
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AIModel>>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching available models from Gemini");
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
            _logger.LogWarning(ex, "Health check failed for Gemini provider");
            return Result.Failure(AIErrors.ProviderUnavailable("gemini"));
        }
    }

    // ---------- Request mapping ----------

    private GeminiGenerateContentRequest MapToApiRequest(CompletionRequest request)
    {
        var contents = new List<GeminiContent>(request.Messages.Count);
        foreach (var message in request.Messages)
        {
            // Compendium "Tool" role maps to a Gemini functionResponse content authored by the user.
            if (message.Role == MessageRole.Tool)
            {
                contents.Add(new GeminiContent
                {
                    Role = "user",
                    Parts = new List<GeminiPart>
                    {
                        new()
                        {
                            FunctionResponse = new GeminiFunctionResponse
                            {
                                Name = message.Name ?? string.Empty,
                                Response = ParseJsonOrWrap(message.Content)
                            }
                        }
                    }
                });
                continue;
            }

            contents.Add(new GeminiContent
            {
                Role = MapRole(message.Role),
                Parts = new List<GeminiPart> { new() { Text = message.Content } }
            });
        }

        var apiRequest = new GeminiGenerateContentRequest
        {
            Contents = contents,
            GenerationConfig = BuildGenerationConfig(request)
        };

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            apiRequest.SystemInstruction = new GeminiContent
            {
                Parts = new List<GeminiPart> { new() { Text = request.SystemPrompt } }
            };
        }

        ApplyTools(apiRequest, request);
        ApplyResponseFormat(apiRequest, request);
        ApplyVisionParts(apiRequest, request);

        return apiRequest;
    }

    private GeminiGenerationConfig BuildGenerationConfig(CompletionRequest request) => new()
    {
        Temperature = request.Temperature ?? CompletionRequest.FallbackTemperature,
        TopP = request.TopP,
        MaxOutputTokens = request.MaxTokens ?? _options.DefaultMaxTokens,
        FrequencyPenalty = request.FrequencyPenalty,
        PresencePenalty = request.PresencePenalty,
        StopSequences = request.StopSequences?.ToList()
    };

    private static void ApplyTools(GeminiGenerateContentRequest apiRequest, CompletionRequest request)
    {
        if (request.AdditionalParameters == null)
        {
            return;
        }

        if (request.AdditionalParameters.TryGetValue(GeminiToolCallingExtensions.ToolsKey, out var toolsRaw)
            && toolsRaw is IReadOnlyList<AgentTool> tools
            && tools.Count > 0)
        {
            apiRequest.Tools = new List<GeminiTool>
            {
                new()
                {
                    FunctionDeclarations = tools.Select(t => new GeminiFunctionDeclaration
                    {
                        Name = t.Name,
                        Description = t.Description,
                        Parameters = ParseSchemaOrDefault(t.InputSchemaJson)
                    }).ToList()
                }
            };
        }

        if (request.AdditionalParameters.TryGetValue(GeminiToolCallingExtensions.ToolChoiceKey, out var choiceRaw)
            && choiceRaw is string toolChoice
            && !string.IsNullOrEmpty(toolChoice))
        {
            apiRequest.ToolConfig = toolChoice.ToLowerInvariant() switch
            {
                "auto" => new GeminiToolConfig { FunctionCallingConfig = new GeminiFunctionCallingConfig { Mode = "AUTO" } },
                "any" or "required" => new GeminiToolConfig { FunctionCallingConfig = new GeminiFunctionCallingConfig { Mode = "ANY" } },
                "none" => new GeminiToolConfig { FunctionCallingConfig = new GeminiFunctionCallingConfig { Mode = "NONE" } },
                _ => new GeminiToolConfig
                {
                    FunctionCallingConfig = new GeminiFunctionCallingConfig
                    {
                        Mode = "ANY",
                        AllowedFunctionNames = new List<string> { toolChoice }
                    }
                }
            };
        }
    }

    private void ApplyResponseFormat(GeminiGenerateContentRequest apiRequest, CompletionRequest request)
    {
        var parameters = request.AdditionalParameters;

        if (parameters != null
            && parameters.TryGetValue(GeminiStructuredOutputExtensions.SchemaKey, out var schemaRaw)
            && schemaRaw is string schemaJson
            && !string.IsNullOrWhiteSpace(schemaJson))
        {
            apiRequest.GenerationConfig ??= new GeminiGenerationConfig();
            apiRequest.GenerationConfig.ResponseMimeType = "application/json";
            apiRequest.GenerationConfig.ResponseSchema = ParseSchemaOrDefault(schemaJson);
            return;
        }

        var explicitJsonMode = parameters != null
            && parameters.TryGetValue(GeminiStructuredOutputExtensions.JsonModeKey, out var jsonModeRaw)
            && jsonModeRaw is bool jsonModeFlag
            && jsonModeFlag;

        if (explicitJsonMode || _options.UseStructuredOutputsByDefault)
        {
            apiRequest.GenerationConfig ??= new GeminiGenerationConfig();
            apiRequest.GenerationConfig.ResponseMimeType = "application/json";
        }
    }

    /// <summary>
    /// Vision support. <see cref="CompletionRequest"/> has no first-class image field, so callers may
    /// stash a list of <see cref="GeminiPart"/> entries under <see cref="VisionPartsKey"/> in
    /// <see cref="CompletionRequest.AdditionalParameters"/>. We append them to the last user content.
    /// </summary>
    private static void ApplyVisionParts(GeminiGenerateContentRequest apiRequest, CompletionRequest request)
    {
        if (request.AdditionalParameters == null
            || !request.AdditionalParameters.TryGetValue(VisionPartsKey, out var raw)
            || raw is not IReadOnlyList<GeminiPart> extraParts
            || extraParts.Count == 0)
        {
            return;
        }

        var target = apiRequest.Contents.LastOrDefault(c => c.Role == "user")
            ?? apiRequest.Contents.LastOrDefault();
        if (target == null)
        {
            apiRequest.Contents.Add(new GeminiContent
            {
                Role = "user",
                Parts = extraParts.ToList()
            });
            return;
        }

        foreach (var part in extraParts)
        {
            target.Parts.Add(part);
        }
    }

    /// <summary>
    /// Key under which callers may attach a list of pre-built <see cref="GeminiPart"/> objects
    /// (typically <c>inlineData</c> images or <c>fileData</c> URIs) to a <see cref="CompletionRequest"/>.
    /// </summary>
    internal const string VisionPartsKey = "gemini.vision_parts";

    // ---------- Response mapping ----------

    private static CompletionResponse MapToCompletionResponse(GeminiGenerateContentResponse apiResponse, string model)
    {
        var candidate = apiResponse.Candidates?.FirstOrDefault();
        var content = ExtractText(candidate?.Content);
        var toolCalls = ExtractToolCalls(candidate?.Content);

        IReadOnlyDictionary<string, object>? metadata = null;
        if (toolCalls.Count > 0)
        {
            metadata = new Dictionary<string, object>
            {
                [GeminiToolCallingExtensions.ToolCallsMetadataKey] = toolCalls
            };
        }

        return new CompletionResponse
        {
            Id = string.Empty,
            Model = apiResponse.ModelVersion ?? model,
            Content = content,
            FinishReason = MapFinishReason(candidate?.FinishReason, toolCalls.Count > 0),
            Usage = new UsageStats
            {
                PromptTokens = apiResponse.UsageMetadata?.PromptTokenCount ?? 0,
                CompletionTokens = apiResponse.UsageMetadata?.CandidatesTokenCount ?? 0
            },
            CreatedAt = DateTime.UtcNow,
            Metadata = metadata
        };
    }

    private static CompletionChunk MapToCompletionChunk(
        GeminiGenerateContentResponse chunk,
        int index,
        string model)
    {
        var candidate = chunk.Candidates?.FirstOrDefault();
        var contentDelta = ExtractText(candidate?.Content);
        var isFinal = candidate?.FinishReason != null;
        var toolCalls = ExtractToolCalls(candidate?.Content);

        return new CompletionChunk
        {
            Id = chunk.ModelVersion ?? model,
            ContentDelta = contentDelta,
            Index = index,
            IsFinal = isFinal,
            FinishReason = isFinal ? MapFinishReason(candidate?.FinishReason, toolCalls.Count > 0) : null,
            Usage = chunk.UsageMetadata != null
                ? new UsageStats
                {
                    PromptTokens = chunk.UsageMetadata.PromptTokenCount,
                    CompletionTokens = chunk.UsageMetadata.CandidatesTokenCount
                }
                : null
        };
    }

    private static List<AgentToolInvocation> ExtractToolCalls(GeminiContent? content)
    {
        if (content?.Parts == null)
        {
            return new List<AgentToolInvocation>();
        }

        var calls = new List<AgentToolInvocation>();
        foreach (var part in content.Parts)
        {
            if (part.FunctionCall == null)
            {
                continue;
            }

            var argsJson = part.FunctionCall.Args.HasValue
                ? part.FunctionCall.Args.Value.GetRawText()
                : "{}";

            calls.Add(new AgentToolInvocation(
                ToolName: part.FunctionCall.Name,
                ArgumentsJson: argsJson,
                ResultText: string.Empty,
                IsError: false,
                Latency: TimeSpan.Zero));
        }
        return calls;
    }

    private static string ExtractText(GeminiContent? content)
    {
        if (content?.Parts == null)
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder();
        foreach (var part in content.Parts)
        {
            if (!string.IsNullOrEmpty(part.Text))
            {
                buffer.Append(part.Text);
            }
        }
        return buffer.ToString();
    }

    private static FinishReason MapFinishReason(string? reason, bool hasToolCalls) =>
        reason?.ToUpperInvariant() switch
        {
            "STOP" => hasToolCalls ? FinishReason.ToolCall : FinishReason.Stop,
            "MAX_TOKENS" => FinishReason.Length,
            "SAFETY" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "RECITATION" => FinishReason.ContentFilter,
            "TOOL_CALL" or "FUNCTION_CALL" => FinishReason.ToolCall,
            null => hasToolCalls ? FinishReason.ToolCall : FinishReason.InProgress,
            _ => FinishReason.Other
        };

    private static string MapRole(MessageRole role) => role switch
    {
        MessageRole.User => "user",
        MessageRole.Assistant => "model",
        MessageRole.System => "user", // system messages should be hoisted to systemInstruction; treat stragglers as user
        MessageRole.Tool => "user",
        _ => "user"
    };

    private static AIModel MapToAIModel(GeminiModelInfo model)
    {
        var methods = model.SupportedGenerationMethods ?? new List<string>();
        var supportsEmbeddings = methods.Any(m =>
            m.Equals("embedContent", StringComparison.OrdinalIgnoreCase)
            || m.Equals("batchEmbedContents", StringComparison.OrdinalIgnoreCase));
        var supportsChat = methods.Any(m =>
            m.Equals("generateContent", StringComparison.OrdinalIgnoreCase)
            || m.Equals("streamGenerateContent", StringComparison.OrdinalIgnoreCase));

        // Heuristics: chat-capable models in the gemini-1.5/2.x family natively accept vision.
        var supportsVision = supportsChat && model.Name.Contains("gemini", StringComparison.OrdinalIgnoreCase);

        return new AIModel
        {
            Id = model.Name.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? model.Name["models/".Length..]
                : model.Name,
            Name = model.DisplayName ?? model.Name,
            Provider = "gemini",
            ContextWindow = model.InputTokenLimit,
            MaxOutputTokens = model.OutputTokenLimit,
            SupportsStreaming = supportsChat,
            SupportsEmbeddings = supportsEmbeddings,
            SupportsVision = supportsVision,
            SupportsTools = supportsChat
        };
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

    private static JsonElement ParseJsonOrWrap(string text)
    {
        try
        {
            return JsonDocument.Parse(text).RootElement;
        }
        catch (JsonException)
        {
            // Wrap non-JSON text into { "result": "<text>" } so the API still accepts it.
            var wrapped = JsonSerializer.SerializeToElement(new { result = text });
            return wrapped;
        }
    }
}
