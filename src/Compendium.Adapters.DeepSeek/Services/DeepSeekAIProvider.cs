// -----------------------------------------------------------------------
// <copyright file="DeepSeekAIProvider.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Abstractions.AI.Agents.Models;
using Compendium.Adapters.DeepSeek.Configuration;
using Compendium.Adapters.DeepSeek.Http;
using Compendium.Adapters.DeepSeek.Http.Models;
using Compendium.Adapters.DeepSeek.Reasoning;
using Compendium.Adapters.DeepSeek.Tools;

namespace Compendium.Adapters.DeepSeek.Services;

/// <summary>
/// DeepSeek implementation of <see cref="IAIProvider"/>. Provides chat completions, streaming,
/// and tool calling against the DeepSeek REST API (OpenAI-compatible).
/// </summary>
/// <remarks>
/// DeepSeek does not currently expose a hosted embedding endpoint, so <see cref="EmbedAsync"/>
/// returns a <c>Result.Failure</c> with the <c>AI.Unsupported</c> error code rather than throwing.
/// For embeddings use <c>compendium-adapter-openai</c> or <c>compendium-adapter-mistral</c>.
/// </remarks>
internal sealed class DeepSeekAIProvider : IAIProvider
{
    private const string UnsupportedErrorCode = "AI.Unsupported";

    private readonly DeepSeekHttpClient _httpClient;
    private readonly DeepSeekOptions _options;
    private readonly ILogger<DeepSeekAIProvider> _logger;

    public DeepSeekAIProvider(
        DeepSeekHttpClient httpClient,
        IOptions<DeepSeekOptions> options,
        ILogger<DeepSeekAIProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "deepseek";

    /// <inheritdoc />
    public async Task<Result<CompletionResponse>> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;
        _logger.LogDebug("Sending DeepSeek chat completion to model {Model}", model);

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
        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;
        _logger.LogDebug("Sending DeepSeek streaming chat completion to model {Model}", model);

        var apiRequest = MapToApiRequest(request, model, stream: true);

        var index = 0;
        await foreach (var chunk in _httpClient.StreamChatCompletionAsync(apiRequest, cancellationToken))
        {
            if (chunk.IsFailure)
            {
                yield return Result.Failure<CompletionChunk>(chunk.Error);
                yield break;
            }

            var mapped = MapToCompletionChunk(chunk.Value, index);
            if (mapped is null)
            {
                // Pure reasoning delta with no visible content, and the consumer did not opt in
                // to inline reasoning — skip silently. Final completion still arrives via the
                // chunk carrying finish_reason.
                continue;
            }

            yield return Result.Success(mapped);
            index++;

            if (mapped.IsFinal)
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
        _logger.LogDebug("Refusing DeepSeek embedding request — provider has no embedding endpoint");
        var error = Error.Unavailable(
            UnsupportedErrorCode,
            "DeepSeek does not currently offer a hosted embedding endpoint. "
            + "Use compendium-adapter-openai or compendium-adapter-mistral for embeddings.");
        return Task.FromResult(Result.Failure<EmbeddingResponse>(error));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AIModel>>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching available models from DeepSeek");
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
            _logger.LogWarning(ex, "Health check failed for DeepSeek provider");
            return Result.Failure(AIErrors.ProviderUnavailable("deepseek"));
        }
    }

    private DeepSeekChatCompletionRequest MapToApiRequest(CompletionRequest request, string model, bool stream)
    {
        var messages = new List<DeepSeekChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new DeepSeekChatMessage { Role = "system", Content = request.SystemPrompt });
        }
        foreach (var msg in request.Messages)
        {
            messages.Add(new DeepSeekChatMessage
            {
                Role = msg.Role.ToString().ToLowerInvariant(),
                Content = msg.Content,
                Name = msg.Name
            });
        }

        var apiRequest = new DeepSeekChatCompletionRequest
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
            apiRequest.StreamOptions = new DeepSeekStreamOptions { IncludeUsage = true };
        }

        ApplyTools(apiRequest, request);
        return apiRequest;
    }

    private static void ApplyTools(DeepSeekChatCompletionRequest apiRequest, CompletionRequest request)
    {
        if (request.AdditionalParameters == null)
        {
            return;
        }

        if (request.AdditionalParameters.TryGetValue(DeepSeekToolCallingExtensions.ToolsKey, out var toolsRaw)
            && toolsRaw is IReadOnlyList<AgentTool> tools
            && tools.Count > 0)
        {
            apiRequest.Tools = tools.Select(t => new DeepSeekToolDefinition
            {
                Function = new DeepSeekFunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = ParseSchemaOrDefault(t.InputSchemaJson)
                }
            }).ToList();
        }

        if (request.AdditionalParameters.TryGetValue(DeepSeekToolCallingExtensions.ToolChoiceKey, out var choiceRaw)
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

    private CompletionResponse MapToCompletionResponse(DeepSeekChatCompletionResponse apiResponse)
    {
        var choice = apiResponse.Choices.FirstOrDefault();
        var message = choice?.Message;
        var content = message?.Content ?? string.Empty;
        var reasoning = message?.ReasoningContent;

        Dictionary<string, object>? metadata = null;

        if (!string.IsNullOrEmpty(reasoning))
        {
            metadata = new Dictionary<string, object>
            {
                [DeepSeekReasoningExtensions.ReasoningContentKey] = reasoning
            };
            if (_options.InlineReasoningInContent)
            {
                content = $"{DeepSeekReasoningExtensions.ThinkOpenTag}{reasoning}{DeepSeekReasoningExtensions.ThinkCloseTag}{content}";
            }
        }

        if (message?.ToolCalls != null && message.ToolCalls.Count > 0)
        {
            metadata ??= new Dictionary<string, object>();
            metadata[DeepSeekToolCallingExtensions.ToolCallsMetadataKey] =
                (IReadOnlyList<AgentToolInvocation>)message.ToolCalls.Select(MapToAgentToolInvocation).ToList();
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

    private static AgentToolInvocation MapToAgentToolInvocation(DeepSeekToolCall toolCall)
    {
        return new AgentToolInvocation(
            ToolName: toolCall.Function?.Name ?? string.Empty,
            ArgumentsJson: toolCall.Function?.Arguments ?? "{}",
            ResultText: string.Empty,
            IsError: false,
            Latency: TimeSpan.Zero);
    }

    private CompletionChunk? MapToCompletionChunk(DeepSeekStreamChunk chunk, int index)
    {
        var choice = chunk.Choices.FirstOrDefault();
        var isFinal = choice?.FinishReason != null;
        var content = choice?.Delta?.Content ?? string.Empty;
        var reasoning = choice?.Delta?.ReasoningContent;

        var hasContent = !string.IsNullOrEmpty(content);
        var hasReasoning = !string.IsNullOrEmpty(reasoning);

        // CompletionChunk has no Metadata side channel in 1.0.1, so reasoning either inlines
        // into ContentDelta (opt-in) or is dropped from the stream. Always emit the final chunk
        // even if its delta is empty so consumers see IsFinal=true.
        if (!hasContent && !isFinal)
        {
            if (!hasReasoning || !_options.InlineReasoningInContent)
            {
                return null;
            }
        }

        var delta = hasContent ? content : string.Empty;
        if (hasReasoning && _options.InlineReasoningInContent)
        {
            // Tag the reasoning so downstream consumers can still partition the stream.
            delta = $"{DeepSeekReasoningExtensions.ThinkOpenTag}{reasoning}{DeepSeekReasoningExtensions.ThinkCloseTag}{delta}";
        }

        return new CompletionChunk
        {
            Id = chunk.Id,
            ContentDelta = delta,
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

    private static AIModel MapToAIModel(DeepSeekModelInfo model)
    {
        var isReasoner = model.Id.Equals(DeepSeekOptions.ReasonerModel, StringComparison.OrdinalIgnoreCase);
        return new AIModel
        {
            Id = model.Id,
            Name = model.Id,
            Provider = model.OwnedBy ?? "deepseek",
            SupportsStreaming = true,
            SupportsEmbeddings = false,
            SupportsVision = false,
            // The reasoner model does not currently support function calling — only the chat model does.
            SupportsTools = !isReasoner
        };
    }
}
