// -----------------------------------------------------------------------
// <copyright file="MistralAIProvider.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Abstractions.AI.Agents.Models;
using Compendium.Adapters.Mistral.Configuration;
using Compendium.Adapters.Mistral.Http;
using Compendium.Adapters.Mistral.Http.Models;
using Compendium.Adapters.Mistral.StructuredOutputs;
using Compendium.Adapters.Mistral.Tools;
using Compendium.Adapters.Mistral.Vision;

namespace Compendium.Adapters.Mistral.Services;

/// <summary>
/// Mistral implementation of <see cref="IAIProvider"/>. Provides chat completions (sync + streaming SSE),
/// embeddings, tool calling, structured outputs, and Pixtral vision against Mistral's "la Plateforme" API.
/// EU-hosted (France) → GDPR-friendly data residency.
/// </summary>
internal sealed class MistralAIProvider : IAIProvider
{
    private static readonly HashSet<string> KnownEmbeddingModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "mistral-embed"
    };

    private static readonly HashSet<string> KnownVisionModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "pixtral-large-latest",
        "pixtral-12b-2409",
        "pixtral-12b-latest"
    };

    private static readonly HashSet<string> KnownCodeModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "codestral-latest",
        "codestral-2405"
    };

    private readonly MistralHttpClient _httpClient;
    private readonly MistralOptions _options;
    private readonly ILogger<MistralAIProvider> _logger;

    public MistralAIProvider(
        MistralHttpClient httpClient,
        IOptions<MistralOptions> options,
        ILogger<MistralAIProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "mistral";

    /// <inheritdoc />
    public async Task<Result<CompletionResponse>> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;
        _logger.LogDebug("Sending Mistral chat completion to model {Model}", model);

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
        _logger.LogDebug("Sending Mistral streaming chat completion to model {Model}", model);

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
            "Sending Mistral embeddings request for {Count} inputs (batch size {Batch}, model {Model})",
            request.Inputs.Count,
            batchSize,
            model);

        var aggregated = new List<Embedding>(request.Inputs.Count);
        var totalPromptTokens = 0;

        for (var offset = 0; offset < request.Inputs.Count; offset += batchSize)
        {
            var slice = request.Inputs.Skip(offset).Take(batchSize).ToList();
            var batchRequest = new MistralEmbeddingsRequest
            {
                Model = model,
                Input = slice
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
        _logger.LogDebug("Fetching available models from Mistral");
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
            _logger.LogWarning(ex, "Health check failed for Mistral provider");
            return Result.Failure(AIErrors.ProviderUnavailable("mistral"));
        }
    }

    private MistralChatCompletionRequest MapToApiRequest(CompletionRequest request, string model, bool stream)
    {
        var messages = new List<MistralChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new MistralChatMessage { Role = "system", Content = request.SystemPrompt });
        }
        foreach (var msg in request.Messages)
        {
            messages.Add(new MistralChatMessage
            {
                Role = msg.Role.ToString().ToLowerInvariant(),
                Content = msg.Content,
                Name = msg.Name
            });
        }

        ApplyVision(messages, request);

        var apiRequest = new MistralChatCompletionRequest
        {
            Model = model,
            Messages = messages,
            Temperature = request.Temperature ?? CompletionRequest.FallbackTemperature,
            MaxTokens = request.MaxTokens ?? _options.DefaultMaxTokens,
            TopP = request.TopP,
            Stop = request.StopSequences?.ToList(),
            Stream = stream
        };

        ApplyTools(apiRequest, request);
        ApplyResponseFormat(apiRequest, request);
        return apiRequest;
    }

    /// <summary>
    /// Pixtral-style vision: if the caller attached image URLs via <see cref="MistralVisionExtensions.WithImages"/>,
    /// promote the LAST user message to multimodal content (a list of text + image_url parts).
    /// </summary>
    private static void ApplyVision(List<MistralChatMessage> messages, CompletionRequest request)
    {
        if (request.AdditionalParameters == null)
        {
            return;
        }
        if (!request.AdditionalParameters.TryGetValue(MistralVisionExtensions.ImagesKey, out var imagesRaw))
        {
            return;
        }
        if (imagesRaw is not IEnumerable<string> images)
        {
            return;
        }

        // Find the last user message to attach the images to. If none, append a fresh user message.
        MistralChatMessage? target = null;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                target = messages[i];
                break;
            }
        }
        if (target == null)
        {
            target = new MistralChatMessage { Role = "user", Content = string.Empty };
            messages.Add(target);
        }

        var parts = new List<MistralContentPart>();
        if (target.Content is string text && !string.IsNullOrEmpty(text))
        {
            parts.Add(new MistralContentPart { Type = "text", Text = text });
        }
        foreach (var url in images)
        {
            parts.Add(new MistralContentPart
            {
                Type = "image_url",
                ImageUrl = new MistralImageUrl { Url = url }
            });
        }
        target.Content = parts;
    }

    private static void ApplyTools(MistralChatCompletionRequest apiRequest, CompletionRequest request)
    {
        if (request.AdditionalParameters == null)
        {
            return;
        }

        if (request.AdditionalParameters.TryGetValue(MistralToolCallingExtensions.ToolsKey, out var toolsRaw)
            && toolsRaw is IReadOnlyList<AgentTool> tools
            && tools.Count > 0)
        {
            apiRequest.Tools = tools.Select(t => new MistralToolDefinition
            {
                Function = new MistralFunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = ParseSchemaOrDefault(t.InputSchemaJson)
                }
            }).ToList();
        }

        if (request.AdditionalParameters.TryGetValue(MistralToolCallingExtensions.ToolChoiceKey, out var choiceRaw)
            && choiceRaw is string toolChoice
            && !string.IsNullOrEmpty(toolChoice))
        {
            apiRequest.ToolChoice = toolChoice switch
            {
                // Mistral accepts "auto", "any" (force a tool call), and "none" verbatim.
                "auto" or "any" or "none" => toolChoice,
                _ => new { type = "function", function = new { name = toolChoice } }
            };
        }
    }

    private void ApplyResponseFormat(MistralChatCompletionRequest apiRequest, CompletionRequest request)
    {
        var parameters = request.AdditionalParameters;
        if (parameters != null
            && parameters.TryGetValue(MistralStructuredOutputExtensions.SchemaKey, out var schemaRaw)
            && schemaRaw is string schemaJson
            && !string.IsNullOrWhiteSpace(schemaJson))
        {
            var schemaName = parameters.TryGetValue(MistralStructuredOutputExtensions.SchemaNameKey, out var nameRaw)
                && nameRaw is string s
                ? s
                : "response";
            var strict = !parameters.TryGetValue(MistralStructuredOutputExtensions.StrictKey, out var strictRaw)
                || strictRaw is not bool b
                || b;

            apiRequest.ResponseFormat = new MistralResponseFormat
            {
                Type = "json_schema",
                JsonSchema = new MistralJsonSchemaFormat
                {
                    Name = schemaName,
                    Schema = JsonDocument.Parse(schemaJson).RootElement,
                    Strict = strict
                }
            };
            return;
        }

        var explicitJsonMode = parameters != null
            && parameters.TryGetValue(MistralStructuredOutputExtensions.JsonModeKey, out var jsonModeRaw)
            && jsonModeRaw is bool jsonModeFlag
            && jsonModeFlag;

        if (explicitJsonMode || _options.UseStructuredOutputsByDefault)
        {
            apiRequest.ResponseFormat = new MistralResponseFormat { Type = "json_object" };
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

    private static CompletionResponse MapToCompletionResponse(MistralChatCompletionResponse apiResponse)
    {
        var choice = apiResponse.Choices.FirstOrDefault();
        var message = choice?.Message;
        var content = MaterialiseContent(message?.Content);

        IReadOnlyDictionary<string, object>? metadata = null;
        if (message?.ToolCalls != null && message.ToolCalls.Count > 0)
        {
            var invocations = message.ToolCalls.Select(MapToAgentToolInvocation).ToList();
            metadata = new Dictionary<string, object>
            {
                [MistralToolCallingExtensions.ToolCallsMetadataKey] = invocations
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

    /// <summary>
    /// Mistral's <c>message.content</c> is normally a string, but for assistant replies to vision
    /// inputs it can occasionally be a content-parts array (text-only parts). Concatenate them.
    /// </summary>
    private static string MaterialiseContent(object? raw)
    {
        switch (raw)
        {
            case null:
                return string.Empty;
            case string s:
                return s;
            case JsonElement el when el.ValueKind == JsonValueKind.String:
                return el.GetString() ?? string.Empty;
            case JsonElement el when el.ValueKind == JsonValueKind.Array:
                var sb = new System.Text.StringBuilder();
                foreach (var part in el.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textProp)
                        && textProp.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(textProp.GetString());
                    }
                }
                return sb.ToString();
            default:
                return raw.ToString() ?? string.Empty;
        }
    }

    private static AgentToolInvocation MapToAgentToolInvocation(MistralToolCall toolCall)
    {
        return new AgentToolInvocation(
            ToolName: toolCall.Function?.Name ?? string.Empty,
            ArgumentsJson: toolCall.Function?.Arguments ?? "{}",
            ResultText: string.Empty,
            IsError: false,
            Latency: TimeSpan.Zero);
    }

    private static CompletionChunk MapToCompletionChunk(MistralStreamChunk chunk, int index)
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
        "length" or "model_length" => FinishReason.Length,
        "content_filter" => FinishReason.ContentFilter,
        "tool_calls" or "function_call" => FinishReason.ToolCall,
        null => FinishReason.InProgress,
        _ => FinishReason.Other
    };

    private static AIModel MapToAIModel(MistralModelInfo model)
    {
        var supportsEmbeddings = KnownEmbeddingModels.Contains(model.Id)
            || model.Id.Contains("embed", StringComparison.OrdinalIgnoreCase);
        var supportsChat = !supportsEmbeddings;
        var supportsVision = KnownVisionModels.Contains(model.Id)
            || model.Id.Contains("pixtral", StringComparison.OrdinalIgnoreCase);
        var supportsCode = KnownCodeModels.Contains(model.Id)
            || model.Id.Contains("codestral", StringComparison.OrdinalIgnoreCase);

        return new AIModel
        {
            Id = model.Id,
            Name = model.Id,
            Provider = model.OwnedBy ?? "mistral",
            SupportsStreaming = supportsChat,
            SupportsEmbeddings = supportsEmbeddings,
            SupportsVision = supportsVision,
            SupportsTools = supportsChat,
            Metadata = supportsCode
                ? new Dictionary<string, object> { ["specialty"] = "code" }
                : null
        };
    }
}
