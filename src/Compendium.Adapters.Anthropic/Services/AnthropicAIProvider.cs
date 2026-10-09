// -----------------------------------------------------------------------
// <copyright file="AnthropicAIProvider.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using Compendium.Adapters.Anthropic.Configuration;
using Compendium.Adapters.Anthropic.Http;
using Compendium.Adapters.Anthropic.Http.Models;

namespace Compendium.Adapters.Anthropic.Services;

/// <summary>
/// Anthropic implementation of <see cref="IAIProvider"/>, backed by the
/// Messages API (<c>POST /v1/messages</c>) on <c>https://api.anthropic.com</c>.
/// </summary>
/// <remarks>
/// <para>
/// This adapter targets the published <see cref="IAIProvider"/> surface, which is
/// text-only — image / tool blocks are intentionally not exposed here because
/// agent-level tool calling and vision are handled by the agent layer in
/// <c>Compendium.Application</c>. Anthropic-native models (vision-capable Claude,
/// tool-use blocks) remain reachable through the typed <c>AnthropicHttpClient</c>
/// for advanced consumers that wire their own surface.
/// </para>
/// <para>
/// <see cref="EmbedAsync"/> always fails with <c>AI.InvalidRequest</c> — Anthropic
/// does not expose a public embeddings endpoint.
/// </para>
/// <para>
/// <see cref="ListModelsAsync"/> reads <c>GET /v1/models</c>, every page, and returns the provider's error as it is.
/// </para>
/// </remarks>
internal sealed class AnthropicAIProvider : IAIProvider
{
    private readonly AnthropicHttpClient _httpClient;
    private readonly AnthropicOptions _options;
    private readonly ILogger<AnthropicAIProvider> _logger;

    /// <summary>
    /// Creates a new <see cref="AnthropicAIProvider"/>.
    /// </summary>
    public AnthropicAIProvider(
        AnthropicHttpClient httpClient,
        IOptions<AnthropicOptions> options,
        ILogger<AnthropicAIProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "anthropic";

    /// <inheritdoc />
    public async Task<Result<CompletionResponse>> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? _options.DefaultModel : request.Model;

        _logger.LogDebug("Sending Anthropic message request to model {Model}", model);

        var apiRequest = MapToApiRequest(request, model, stream: false);
        var result = await _httpClient.CreateMessageAsync(apiRequest, cancellationToken);

        return result.Match(
            apiResponse => Result.Success(MapToCompletionResponse(apiResponse)),
            error => Result.Failure<CompletionResponse>(error));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<CompletionChunk>> StreamCompleteAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? _options.DefaultModel : request.Model;

        _logger.LogDebug("Sending Anthropic streaming message request to model {Model}", model);

        var apiRequest = MapToApiRequest(request, model, stream: true);

        var messageId = string.Empty;
        var index = 0;
        UsageStats? aggregatedUsage = null;
        var sawFinalEvent = false;

        await foreach (var evt in _httpClient.CreateMessageStreamAsync(apiRequest, cancellationToken))
        {
            if (evt.IsFailure)
            {
                yield return Result.Failure<CompletionChunk>(evt.Error);
                yield break;
            }

            var streamEvent = evt.Value;
            switch (streamEvent.Type)
            {
                case "message_start":
                    if (streamEvent.Message is { } start)
                    {
                        messageId = start.Id;
                        aggregatedUsage = start.Usage is null ? null : new UsageStats
                        {
                            PromptTokens = start.Usage.InputTokens,
                            CompletionTokens = start.Usage.OutputTokens,
                        };
                    }

                    break;

                case "content_block_delta":
                    if (streamEvent.Delta?.Text is { } text)
                    {
                        yield return Result.Success(new CompletionChunk
                        {
                            Id = messageId,
                            ContentDelta = text,
                            Index = index++,
                            IsFinal = false,
                        });
                    }

                    break;

                case "message_delta":
                    if (streamEvent.Usage is { } messageDeltaUsage)
                    {
                        aggregatedUsage = new UsageStats
                        {
                            PromptTokens = aggregatedUsage?.PromptTokens ?? 0,
                            CompletionTokens = messageDeltaUsage.OutputTokens,
                        };
                    }

                    if (streamEvent.Delta?.StopReason is { } stopReason)
                    {
                        yield return Result.Success(new CompletionChunk
                        {
                            Id = messageId,
                            ContentDelta = string.Empty,
                            Index = index++,
                            IsFinal = true,
                            FinishReason = MapStopReason(stopReason),
                            Usage = aggregatedUsage,
                        });
                        sawFinalEvent = true;
                    }

                    break;

                case "message_stop":
                    if (!sawFinalEvent)
                    {
                        yield return Result.Success(new CompletionChunk
                        {
                            Id = messageId,
                            ContentDelta = string.Empty,
                            Index = index++,
                            IsFinal = true,
                            FinishReason = FinishReason.Stop,
                            Usage = aggregatedUsage,
                        });
                    }

                    yield break;
            }
        }
    }

    /// <inheritdoc />
    public Task<Result<EmbeddingResponse>> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result.Failure<EmbeddingResponse>(
            AIErrors.InvalidRequest("Embeddings are not supported by Anthropic; use a dedicated embedding provider.")));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AIModel>>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Listing Anthropic models");
        var result = await _httpClient.ListModelsAsync(cancellationToken);

        // The provider's answer as it is: no fallback onto a static list, which would show models a key cannot call.
        return result.Match(
            models => Result.Success<IReadOnlyList<AIModel>>(models.Select(MapToAIModel).ToList()),
            error => Result.Failure<IReadOnlyList<AIModel>>(error));
    }

    /// <inheritdoc />
    public async Task<Result> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var probe = new AnthropicMessagesRequest
            {
                Model = _options.DefaultModel,
                MaxTokens = 1,
                Messages = new List<AnthropicMessage>
                {
                    new() { Role = "user", Content = "ping" },
                },
            };

            var result = await _httpClient.CreateMessageAsync(probe, cancellationToken);

            if (result.IsSuccess)
            {
                return Result.Success();
            }

            // Treat auth + insufficient-credits as definitive provider-level failures.
            // Transient model/rate errors still get reported but don't crash the host.
            return Result.Failure(result.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Health check failed for Anthropic provider");
            return Result.Failure(AIErrors.ProviderUnavailable("anthropic"));
        }
    }

    private AnthropicMessagesRequest MapToApiRequest(CompletionRequest request, string model, bool stream)
    {
        var messages = new List<AnthropicMessage>();
        var extraSystem = new StringBuilder();

        foreach (var msg in request.Messages ?? Enumerable.Empty<Message>())
        {
            switch (msg.Role)
            {
                case MessageRole.System:
                    if (extraSystem.Length > 0)
                    {
                        extraSystem.Append('\n');
                    }

                    extraSystem.Append(msg.Content);
                    break;
                case MessageRole.User:
                case MessageRole.Tool:
                    messages.Add(new AnthropicMessage { Role = "user", Content = msg.Content ?? string.Empty });
                    break;
                case MessageRole.Assistant:
                    messages.Add(new AnthropicMessage { Role = "assistant", Content = msg.Content ?? string.Empty });
                    break;
            }
        }

        List<AnthropicSystemBlock>? systemBlocks = null;
        var systemPrompt = CombineSystemPrompt(request.SystemPrompt, extraSystem);
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            var block = new AnthropicSystemBlock { Type = "text", Text = systemPrompt };
            if (_options.EnablePromptCaching)
            {
                block.CacheControl = new AnthropicCacheControl();
            }

            systemBlocks = new List<AnthropicSystemBlock> { block };
        }

        AnthropicRequestMetadata? metadata = null;
        if (!string.IsNullOrWhiteSpace(request.UserId))
        {
            metadata = new AnthropicRequestMetadata { UserId = request.UserId };
        }

        return new AnthropicMessagesRequest
        {
            Model = model,
            Messages = messages,
            MaxTokens = request.MaxTokens ?? _options.DefaultMaxTokens,
            System = systemBlocks,

            // Unset stays unset, with no fallback: Claude Opus 4.7 and later, Sonnet 5, Fable and Mythos answer any
            // temperature or top_p with a 400, and the null fields are left out of the body.
            Temperature = request.Temperature,
            TopP = request.TopP,
            StopSequences = request.StopSequences?.ToList(),
            Stream = stream ? true : null,
            Metadata = metadata,
        };
    }

    private static string? CombineSystemPrompt(string? primary, StringBuilder addendum)
    {
        if (string.IsNullOrEmpty(primary) && addendum.Length == 0)
        {
            return null;
        }

        if (string.IsNullOrEmpty(primary))
        {
            return addendum.ToString();
        }

        if (addendum.Length == 0)
        {
            return primary;
        }

        return primary + "\n" + addendum;
    }

    private static CompletionResponse MapToCompletionResponse(AnthropicMessagesResponse apiResponse)
    {
        var text = ExtractText(apiResponse.Content);

        return new CompletionResponse
        {
            Id = apiResponse.Id,
            Model = apiResponse.Model,
            Content = text,
            FinishReason = MapStopReason(apiResponse.StopReason),
            Usage = new UsageStats
            {
                PromptTokens = apiResponse.Usage?.InputTokens ?? 0,
                CompletionTokens = apiResponse.Usage?.OutputTokens ?? 0,
            },
            CreatedAt = DateTime.UtcNow,
        };
    }

    private static string ExtractText(IReadOnlyList<AnthropicContentBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        if (blocks.Count == 1)
        {
            return blocks[0].Text ?? string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (string.Equals(block.Type, "text", StringComparison.Ordinal) && !string.IsNullOrEmpty(block.Text))
            {
                sb.Append(block.Text);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Maps a <c>ModelInfo</c> of <c>/v1/models</c>. The API gives no price; an unknown limit stays null; the release
    /// date goes to <c>Metadata["created_at"]</c> (a UTC <see cref="DateTimeOffset"/>), left out when the API sends the
    /// epoch for an unknown date.
    /// </summary>
    private static AIModel MapToAIModel(AnthropicModelInfo info)
    {
        var metadata = new Dictionary<string, object>(StringComparer.Ordinal);
        if (info.CreatedAt is { } createdAt && createdAt > DateTimeOffset.UnixEpoch)
        {
            metadata["created_at"] = createdAt.ToUniversalTime();
        }

        if (!string.IsNullOrEmpty(info.Line))
        {
            metadata["line"] = info.Line;
        }

        return new AIModel
        {
            Id = info.Id,
            Name = string.IsNullOrWhiteSpace(info.DisplayName) ? info.Id : info.DisplayName,
            Provider = "anthropic",
            ContextWindow = info.MaxInputTokens is > 0 ? info.MaxInputTokens : null,
            MaxOutputTokens = info.MaxTokens is > 0 ? info.MaxTokens : null,
            SupportsStreaming = true,
            SupportsTools = true,
            SupportsEmbeddings = false,
            SupportsVision = info.Capabilities?.ImageInput?.Supported ?? false,
            PricingInputPerMillion = null,
            PricingOutputPerMillion = null,
            Metadata = metadata.Count == 0 ? null : metadata,
        };
    }

    private static FinishReason MapStopReason(string? reason) => reason switch
    {
        "end_turn" or "stop_sequence" => FinishReason.Stop,
        "max_tokens" => FinishReason.Length,
        "tool_use" => FinishReason.ToolCall,
        null => FinishReason.InProgress,
        _ => FinishReason.Other,
    };
}
