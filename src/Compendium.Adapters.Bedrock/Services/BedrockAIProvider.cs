// -----------------------------------------------------------------------
// <copyright file="BedrockAIProvider.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.EventStreams;
using Compendium.Adapters.Bedrock.Options;
using BedrockMessage = Amazon.BedrockRuntime.Model.Message;
using CompletionMessage = Compendium.Abstractions.AI.Models.Message;

namespace Compendium.Adapters.Bedrock.Services;

/// <summary>
/// AWS Bedrock implementation of <see cref="IAIProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// Chat completions (sync + streaming) flow through the unified <c>Converse</c> /
/// <c>ConverseStream</c> API, which normalizes the request shape across all hosted
/// model families (Anthropic Claude, Meta Llama, Mistral, Amazon Nova, Cohere). This
/// keeps the adapter free from per-model JSON marshaling.
/// </para>
/// <para>
/// Embeddings flow through <c>InvokeModel</c> because <c>Converse</c> does not cover
/// them. The request body shape branches on <see cref="BedrockOptions.EmbeddingModelId"/>'s
/// prefix — see <see cref="BedrockEmbeddingPayloads"/>.
/// </para>
/// <para>
/// This preview targets the published <see cref="IAIProvider"/> surface, which is
/// text-only. Vision (image content blocks) and tool / function-calling are supported
/// natively by Bedrock's Converse API but are intentionally not exposed on
/// <see cref="IAIProvider"/> — the agent layer in <c>Compendium.Application</c> owns
/// that surface. They will be re-introduced through a typed Bedrock-native client in a
/// follow-up preview, matching the Anthropic adapter's split.
/// </para>
/// </remarks>
internal sealed class BedrockAIProvider : IAIProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAmazonBedrockRuntime _client;
    private readonly BedrockOptions _options;
    private readonly ILogger<BedrockAIProvider> _logger;

    /// <summary>
    /// Creates a new <see cref="BedrockAIProvider"/>.
    /// </summary>
    /// <param name="client">Bedrock runtime client (registered by DI).</param>
    /// <param name="options">Adapter options.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public BedrockAIProvider(
        IAmazonBedrockRuntime client,
        IOptions<BedrockOptions> options,
        ILogger<BedrockAIProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "bedrock";

    /// <inheritdoc />
    public async Task<Result<CompletionResponse>> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var modelId = ResolveModelId(request.Model);
        _logger.LogDebug("Bedrock Converse request to model {ModelId}", modelId);

        var converseRequest = BuildConverseRequest(request, modelId);

        try
        {
            var response = await _client.ConverseAsync(converseRequest, cancellationToken)
                .ConfigureAwait(false);

            return Result.Success(MapResponse(response, modelId));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AmazonBedrockRuntimeException ex)
        {
            _logger.LogWarning(ex, "Bedrock Converse failed (status {Status}, code {Code})", ex.StatusCode, ex.ErrorCode);
            return Result.Failure<CompletionResponse>(BedrockErrorMapping.Map(ex, modelId));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<CompletionChunk>> StreamCompleteAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var modelId = ResolveModelId(request.Model);
        _logger.LogDebug("Bedrock ConverseStream request to model {ModelId}", modelId);

        var converseRequest = BuildConverseStreamRequest(request, modelId);

        var openResult = await OpenStreamAsync(converseRequest, modelId, cancellationToken).ConfigureAwait(false);
        if (openResult.IsFailure)
        {
            yield return Result.Failure<CompletionChunk>(openResult.Error);
            yield break;
        }

        await foreach (var chunk in EnumerateStreamAsync(openResult.Value, modelId, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    private async Task<Result<ConverseStreamResponse>> OpenStreamAsync(
        ConverseStreamRequest converseRequest,
        string modelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.ConverseStreamAsync(converseRequest, cancellationToken).ConfigureAwait(false);
            return Result.Success(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AmazonBedrockRuntimeException ex)
        {
            _logger.LogWarning(ex, "Bedrock ConverseStream failed at handshake (status {Status})", ex.StatusCode);
            return Result.Failure<ConverseStreamResponse>(BedrockErrorMapping.Map(ex, modelId));
        }
    }

    /// <inheritdoc />
    public async Task<Result<EmbeddingResponse>> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var modelId = string.IsNullOrWhiteSpace(request.Model) ? _options.EmbeddingModelId : request.Model;

        var family = BedrockEmbeddingPayloads.Detect(modelId);
        if (family is null)
        {
            return Result.Failure<EmbeddingResponse>(
                AIErrors.InvalidRequest(
                    $"Unsupported embedding model id '{modelId}'. Expected an 'amazon.titan-embed-*' or 'cohere.embed-*' id."));
        }

        if (request.Inputs is null || request.Inputs.Count == 0)
        {
            return Result.Failure<EmbeddingResponse>(
                AIErrors.InvalidRequest("EmbeddingRequest.Inputs must contain at least one entry."));
        }

        try
        {
            return family switch
            {
                BedrockEmbeddingPayloads.Family.Titan => await EmbedTitanAsync(request, modelId, cancellationToken)
                    .ConfigureAwait(false),
                BedrockEmbeddingPayloads.Family.Cohere => await EmbedCohereAsync(request, modelId, cancellationToken)
                    .ConfigureAwait(false),
                _ => Result.Failure<EmbeddingResponse>(AIErrors.InvalidRequest("Unsupported embedding family.")),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AmazonBedrockRuntimeException ex)
        {
            _logger.LogWarning(ex, "Bedrock InvokeModel (embeddings) failed (status {Status})", ex.StatusCode);
            return Result.Failure<EmbeddingResponse>(BedrockErrorMapping.Map(ex, modelId));
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<AIModel>>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result.Success(BedrockModelCatalog.KnownModels));
    }

    /// <inheritdoc />
    public async Task<Result> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        var modelId = _options.DefaultModelId;

        try
        {
            var probe = new ConverseRequest
            {
                ModelId = modelId,
                Messages =
                [
                    new BedrockMessage
                    {
                        Role = ConversationRole.User,
                        Content = [new ContentBlock { Text = "ping" }],
                    },
                ],
                InferenceConfig = new InferenceConfiguration { MaxTokens = 1 },
            };

            await _client.ConverseAsync(probe, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AmazonBedrockRuntimeException ex)
        {
            _logger.LogWarning(ex, "Bedrock health check failed (status {Status})", ex.StatusCode);
            return Result.Failure(BedrockErrorMapping.Map(ex, modelId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bedrock health check raised an unexpected exception");
            return Result.Failure(AIErrors.ProviderUnavailable("bedrock"));
        }
    }

    /// <summary>Resolve effective model id, with <see cref="BedrockOptions.DefaultModelId"/> fallback.</summary>
    internal string ResolveModelId(string? requestedModel) =>
        string.IsNullOrWhiteSpace(requestedModel) ? _options.DefaultModelId : requestedModel;

    /// <summary>
    /// Build a <see cref="ConverseRequest"/> from the Compendium completion request.
    /// </summary>
    internal ConverseRequest BuildConverseRequest(CompletionRequest request, string modelId)
    {
        var (messages, systemBlocks) = MapMessages(request);

        return new ConverseRequest
        {
            ModelId = modelId,
            Messages = messages,
            System = systemBlocks,
            InferenceConfig = BuildInferenceConfig(request),
        };
    }

    /// <summary>
    /// Build a <see cref="ConverseStreamRequest"/> from the Compendium completion request.
    /// </summary>
    internal ConverseStreamRequest BuildConverseStreamRequest(CompletionRequest request, string modelId)
    {
        var (messages, systemBlocks) = MapMessages(request);

        return new ConverseStreamRequest
        {
            ModelId = modelId,
            Messages = messages,
            System = systemBlocks,
            InferenceConfig = BuildInferenceConfig(request),
        };
    }

    /// <summary>
    /// Map the IAIProvider messages to Bedrock <see cref="BedrockMessage"/> / <see cref="SystemContentBlock"/>.
    /// </summary>
    /// <remarks>
    /// Bedrock's Converse API rejects two consecutive messages with the same role, so we
    /// coalesce adjacent user / assistant turns and route system messages into the
    /// top-level <c>system</c> field (alongside <see cref="CompletionRequest.SystemPrompt"/>).
    /// Bedrock also requires the first message to have <c>role = user</c> — assistant-leading
    /// conversations are normalized by inserting an empty system marker if needed.
    /// </remarks>
    internal (List<BedrockMessage> Messages, List<SystemContentBlock> SystemBlocks) MapMessages(CompletionRequest request)
    {
        var messages = new List<BedrockMessage>();
        var systemBlocks = new List<SystemContentBlock>();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            systemBlocks.Add(new SystemContentBlock { Text = request.SystemPrompt });
        }

        var src = request.Messages ?? [];
        foreach (var msg in src)
        {
            var content = msg.Content ?? string.Empty;

            switch (msg.Role)
            {
                case MessageRole.System:
                    if (!string.IsNullOrEmpty(content))
                    {
                        systemBlocks.Add(new SystemContentBlock { Text = content });
                    }

                    break;

                case MessageRole.User:
                case MessageRole.Tool:
                    AppendOrCoalesce(messages, ConversationRole.User, content);
                    break;

                case MessageRole.Assistant:
                    AppendOrCoalesce(messages, ConversationRole.Assistant, content);
                    break;
            }
        }

        // Bedrock requires at least one message; if everything was system, synthesize a
        // single user prompt from the trailing system block to satisfy the API contract.
        if (messages.Count == 0)
        {
            messages.Add(new BedrockMessage
            {
                Role = ConversationRole.User,
                Content = [new ContentBlock { Text = systemBlocks.Count > 0 ? systemBlocks[^1].Text : string.Empty }],
            });
        }

        return (messages, systemBlocks);
    }

    private static void AppendOrCoalesce(List<BedrockMessage> messages, ConversationRole role, string content)
    {
        if (messages.Count > 0 && messages[^1].Role == role)
        {
            messages[^1].Content.Add(new ContentBlock { Text = content });
            return;
        }

        messages.Add(new BedrockMessage
        {
            Role = role,
            Content = [new ContentBlock { Text = content }],
        });
    }

    private InferenceConfiguration BuildInferenceConfig(CompletionRequest request)
    {
        var temperature = request.Temperature ?? CompletionRequest.FallbackTemperature;
        return new()
        {
            MaxTokens = request.MaxTokens ?? _options.DefaultMaxTokens,
            Temperature = temperature > 0 ? temperature : null,
            TopP = request.TopP,
            StopSequences = request.StopSequences is { Count: > 0 } ? [.. request.StopSequences] : null,
        };
    }

    private static CompletionResponse MapResponse(ConverseResponse response, string modelId)
    {
        var text = ExtractText(response.Output?.Message);
        var usage = response.Usage;

        return new CompletionResponse
        {
            Id = response.ResponseMetadata?.RequestId ?? string.Empty,
            Model = modelId,
            Content = text,
            FinishReason = MapStopReason(response.StopReason),
            Usage = new UsageStats
            {
                PromptTokens = usage?.InputTokens ?? 0,
                CompletionTokens = usage?.OutputTokens ?? 0,
            },
            CreatedAt = DateTime.UtcNow,
        };
    }

    private static string ExtractText(BedrockMessage? message)
    {
        if (message?.Content is not { Count: > 0 } blocks)
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
            if (!string.IsNullOrEmpty(block.Text))
            {
                sb.Append(block.Text);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Map Bedrock's <see cref="StopReason"/> (a string-backed <c>ConstantClass</c>) to
    /// the abstraction-level <see cref="FinishReason"/>.
    /// </summary>
    internal static FinishReason MapStopReason(StopReason? reason)
    {
        if (reason is null)
        {
            return FinishReason.InProgress;
        }

        if (reason == StopReason.End_turn || reason == StopReason.Stop_sequence)
        {
            return FinishReason.Stop;
        }

        if (reason == StopReason.Max_tokens || reason == StopReason.Model_context_window_exceeded)
        {
            return FinishReason.Length;
        }

        if (reason == StopReason.Tool_use)
        {
            return FinishReason.ToolCall;
        }

        if (reason == StopReason.Content_filtered || reason == StopReason.Guardrail_intervened)
        {
            return FinishReason.ContentFilter;
        }

        return FinishReason.Other;
    }

    /// <summary>
    /// Translate Bedrock's event-stream union into <see cref="CompletionChunk"/> values.
    /// Internal so tests can call it with a hand-rolled enumerable.
    /// </summary>
    internal static async IAsyncEnumerable<Result<CompletionChunk>> TranslateEventsAsync(
        string requestId,
        string modelId,
        IAsyncEnumerable<IEventStreamEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var index = 0;
        UsageStats? aggregatedUsage = null;
        FinishReason? finalReason = null;

        await using var enumerator = events.GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            Result<bool> step;
            try
            {
                step = Result.Success(await enumerator.MoveNextAsync().ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AmazonBedrockRuntimeException ex)
            {
                step = Result.Failure<bool>(BedrockErrorMapping.Map(ex, modelId));
            }
            catch (Exception ex)
            {
                step = Result.Failure<bool>(AIErrors.StreamInterrupted(ex.Message));
            }

            if (step.IsFailure)
            {
                yield return Result.Failure<CompletionChunk>(step.Error);
                yield break;
            }

            if (!step.Value)
            {
                break;
            }

            switch (enumerator.Current)
            {
                case ContentBlockDeltaEvent delta when !string.IsNullOrEmpty(delta.Delta?.Text):
                    yield return Result.Success(new CompletionChunk
                    {
                        Id = requestId,
                        ContentDelta = delta.Delta!.Text,
                        Index = index++,
                        IsFinal = false,
                    });

                    break;

                case MessageStopEvent stopEvt:
                    finalReason = MapStopReason(stopEvt.StopReason);
                    break;

                case ConverseStreamMetadataEvent meta when meta.Usage is { } usage:
                    aggregatedUsage = new UsageStats
                    {
                        PromptTokens = usage.InputTokens ?? 0,
                        CompletionTokens = usage.OutputTokens ?? 0,
                    };
                    break;
            }
        }

        yield return Result.Success(new CompletionChunk
        {
            Id = requestId,
            ContentDelta = string.Empty,
            Index = index,
            IsFinal = true,
            FinishReason = finalReason ?? FinishReason.Stop,
            Usage = aggregatedUsage,
        });
    }

    private static async IAsyncEnumerable<Result<CompletionChunk>> EnumerateStreamAsync(
        ConverseStreamResponse response,
        string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var requestId = response.ResponseMetadata?.RequestId ?? string.Empty;
        var stream = response.Stream;
        if (stream is null)
        {
            yield break;
        }

        await foreach (var chunk in TranslateEventsAsync(requestId, modelId, stream, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    private async Task<Result<EmbeddingResponse>> EmbedTitanAsync(
        EmbeddingRequest request,
        string modelId,
        CancellationToken cancellationToken)
    {
        // Titan embeds a single string per call; batch by calling per input.
        var vectors = new List<Embedding>(request.Inputs.Count);
        var totalTokens = 0;

        for (var i = 0; i < request.Inputs.Count; i++)
        {
            var payload = new BedrockEmbeddingPayloads.TitanRequest
            {
                InputText = request.Inputs[i] ?? string.Empty,
                Dimensions = request.Dimensions,
            };

            using var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
            var apiRequest = new InvokeModelRequest
            {
                ModelId = modelId,
                ContentType = "application/json",
                Accept = "application/json",
                Body = body,
            };

            var apiResponse = await _client.InvokeModelAsync(apiRequest, cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<BedrockEmbeddingPayloads.TitanResponse>(apiResponse.Body, JsonOpts)
                ?? throw new InvalidOperationException("Titan embedding response was empty.");

            vectors.Add(new Embedding
            {
                Index = i,
                Vector = parsed.Embedding,
            });

            totalTokens += parsed.InputTextTokenCount;
        }

        return Result.Success(new EmbeddingResponse
        {
            Model = modelId,
            Embeddings = vectors,
            Usage = new EmbeddingUsage
            {
                PromptTokens = totalTokens,
            },
        });
    }

    private async Task<Result<EmbeddingResponse>> EmbedCohereAsync(
        EmbeddingRequest request,
        string modelId,
        CancellationToken cancellationToken)
    {
        var payload = new BedrockEmbeddingPayloads.CohereRequest
        {
            Texts = [.. request.Inputs.Select(s => s ?? string.Empty)],
        };

        using var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        var apiRequest = new InvokeModelRequest
        {
            ModelId = modelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = body,
        };

        var apiResponse = await _client.InvokeModelAsync(apiRequest, cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<BedrockEmbeddingPayloads.CohereResponse>(apiResponse.Body, JsonOpts)
            ?? throw new InvalidOperationException("Cohere embedding response was empty.");

        var vectors = new List<Embedding>(parsed.Embeddings.Length);
        for (var i = 0; i < parsed.Embeddings.Length; i++)
        {
            vectors.Add(new Embedding
            {
                Index = i,
                Vector = parsed.Embeddings[i],
            });
        }

        return Result.Success(new EmbeddingResponse
        {
            Model = modelId,
            Embeddings = vectors,
            Usage = new EmbeddingUsage
            {
                // Cohere on Bedrock does not return token usage; report zero rather than guess.
                PromptTokens = 0,
            },
        });
    }
}
