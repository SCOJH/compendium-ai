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
using Compendium.Adapters.ZAi.Glm;

namespace Compendium.Adapters.Mistral.Services;

/// <summary>
/// Mistral implementation of <see cref="IAIProvider"/>. Provides chat completions (sync + streaming SSE),
/// embeddings, tool calling, structured outputs, and Pixtral vision against Mistral's "la Plateforme" API.
/// EU-hosted (France) → GDPR-friendly data residency.
/// </summary>
/// <remarks>
/// The GLM models Mistral serves (<c>zai-glm-5-3</c>, <c>zai-glm-latest</c>…) go through the GLM dialect
/// (<see cref="GlmDialect"/>): their effort on GLM's scale, <c>max_tokens</c> with room for a reasoning they always do, and
/// their content — always a list of <c>thinking</c> and <c>text</c> chunks, in a stream too — read apart. Every other model
/// is sent and read exactly as before.
/// <para>
/// Native tool calls, for every model: an assistant <see cref="Message"/> with <see cref="Message.ToolCalls"/> goes as
/// <c>tool_calls</c>, a tool message with <see cref="Message.ToolCallId"/> as <c>tool_call_id</c> (and the called tool's
/// <c>name</c>, as Mistral's guide shows it). The calls come back in <see cref="CompletionResponse.ToolCalls"/> and, put
/// together from the stream's deltas, on the final <see cref="CompletionChunk.ToolCalls"/> (a stream that reaches
/// <c>[DONE]</c> without a <c>finish_reason</c> gets a final chunk of its own for them; one cut before <c>[DONE]</c> ends
/// with <see cref="AIErrors.StreamInterrupted"/>, and a cancelled one with nothing more). A call Mistral sent without an id gets one
/// (<see cref="MistralToolCallAssembler.IdFor"/>). A conversation without them is sent as before.
/// </para>
/// </remarks>
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

    // Mistral's ReasoningEffort enum (https://docs.mistral.ai/openapi.yaml, read 2026-10-20) stops at xhigh; only its
    // reasoning guide (https://docs.mistral.ai/studio/conversations/reasoning.md, same date) lists max for zai-glm-5-3.
    // Until a measurement says Mistral takes max, GLM 5.3's xhigh and max go as high, and the room for the reasoning
    // follows. GLM 5.2's high / xhigh are in the enum and unchanged.
    private const bool GlmHostTakesMax = false;

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

        var glm = GlmDialect.Recognise(model);
        var apiRequest = MapToApiRequest(request, model, stream: false, glm);
        var result = await _httpClient.CreateChatCompletionAsync(apiRequest, cancellationToken);
        return result.Match(
            r => Result.Success(MapToCompletionResponse(r, request.Reasoning, glm is not null)),
            error => Result.Failure<CompletionResponse>(error));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<CompletionChunk>> StreamCompleteAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;
        _logger.LogDebug("Sending Mistral streaming chat completion to model {Model}", model);

        var glm = GlmDialect.Recognise(model);
        var apiRequest = MapToApiRequest(request, model, stream: true, glm);

        var index = 0;
        string? streamId = null;
        var done = false;
        var toolCalls = new MistralToolCallAssembler();
        await foreach (var chunk in _httpClient.StreamChatCompletionAsync(apiRequest, cancellationToken, () => done = true))
        {
            if (chunk.IsFailure)
            {
                yield return Result.Failure<CompletionChunk>(chunk.Error);
                yield break;
            }

            if (string.IsNullOrEmpty(streamId))
            {
                streamId = chunk.Value.Id;
            }

            // Every delta's tool_calls are kept, the skipped GLM ones included: the calls go out whole on the final chunk.
            toolCalls.Add(chunk.Value.Choices.FirstOrDefault()?.Delta?.ToolCalls);

            CompletionChunk completionChunk;
            if (glm is null)
            {
                completionChunk = MapToCompletionChunk(chunk.Value, index++, request.Reasoning is { IncludeReasoning: true });
            }
            else if (MapToGlmChunk(chunk.Value, index, request.Reasoning) is { } glmChunk)
            {
                completionChunk = glmChunk;
                index++;
            }
            else
            {
                // A GLM delta with nothing to pass on (an empty first delta, thinking not asked for): skipped.
                continue;
            }

            if (completionChunk.IsFinal)
            {
                completionChunk = completionChunk with { ToolCalls = toolCalls.Build(streamId) };
            }

            yield return Result.Success(completionChunk);

            if (completionChunk.IsFinal)
            {
                yield break;
            }
        }

        // No delta carried a finish_reason. Without a call, or once the caller cancelled (the SSE loop then stops
        // without an exception), the stream ends as before: nothing more.
        if (cancellationToken.IsCancellationRequested || toolCalls.Build(streamId) is not { } calls)
        {
            yield break;
        }

        // [DONE] was read: the turn is over, and the calls put together go out on a final chunk of their own. Without
        // [DONE], the connection was cut and the calls may be partial: never passed on as a finished turn.
        yield return done
            ? Result.Success(new CompletionChunk
            {
                Id = streamId ?? string.Empty,
                ContentDelta = string.Empty,
                Index = index,
                IsFinal = true,
                FinishReason = FinishReason.ToolCall,
                ToolCalls = calls,
            })
            : Result.Failure<CompletionChunk>(AIErrors.StreamInterrupted(
                "The Mistral stream ended before [DONE] with tool calls under way; they may be partial and are not returned."));
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

    private MistralChatCompletionRequest MapToApiRequest(CompletionRequest request, string model, bool stream, GlmModel? glm)
    {
        var messages = new List<MistralChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new MistralChatMessage { Role = "system", Content = request.SystemPrompt });
        }
        foreach (var msg in request.Messages)
        {
            messages.Add(MapToApiMessage(msg, request.Messages));
        }

        ApplyVision(messages, request);

        var apiRequest = new MistralChatCompletionRequest
        {
            Model = model,
            Messages = messages,
            Temperature = request.Temperature ?? CompletionRequest.FallbackTemperature,
            MaxTokens = glm is null
                ? request.MaxTokens ?? _options.DefaultMaxTokens
                : GlmDialect.MaxTokens(glm, request.MaxTokens, request.Reasoning, _options.DefaultMaxTokens, GlmHostTakesMax),
            TopP = request.TopP,
            Stop = request.StopSequences?.ToList(),
            Stream = stream,

            // Mistral's reasoning has one setting that returns the thinking: "high". The sampling parameters stay: a
            // reasoning model takes them (Magistral recommends 0.7 / 0.95). A GLM always returns its thinking; GLM 5.3
            // gets low / high (its guide also lists max, the API's enum does not: see GlmHostTakesMax), GLM 5.2
            // high / xhigh: its effort is the dialect's, none when the caller asks none. No `thinking` field: Mistral
            // has none.
            ReasoningEffort = glm is null
                ? request.Reasoning is null ? null : "high"
                : GlmDialect.EffortFor(glm, request.Reasoning, GlmHostTakesMax)
        };

        ApplyTools(apiRequest, request);
        ApplyResponseFormat(apiRequest, request);
        return apiRequest;
    }

    /// <summary>
    /// One conversation message on Mistral's wire. An assistant message's <see cref="Message.ToolCalls"/> go as
    /// <c>tool_calls</c> (their arguments as a string); a tool message's <see cref="Message.ToolCallId"/> as
    /// <c>tool_call_id</c>, with the called tool's <c>name</c> when the caller gave none and the call is in the
    /// conversation. On any other role, or empty, they are left out: the message is the one sent before they existed.
    /// </summary>
    private static MistralChatMessage MapToApiMessage(Message msg, IReadOnlyList<Message> conversation)
    {
        var apiMessage = new MistralChatMessage
        {
            Role = msg.Role.ToString().ToLowerInvariant(),
            Content = msg.Content,
            Name = msg.Name
        };

        if (msg.Role == MessageRole.Assistant && msg.ToolCalls is { Count: > 0 } calls)
        {
            apiMessage.ToolCalls = calls.Select(c => new MistralToolCall
            {
                Id = c.Id,
                Function = new MistralToolCallFunction { Name = c.Name, Arguments = c.ArgumentsJson }
            }).ToList();
        }
        else if (msg.Role == MessageRole.Tool && !string.IsNullOrEmpty(msg.ToolCallId))
        {
            apiMessage.ToolCallId = msg.ToolCallId;
            apiMessage.Name ??= NameOfCall(conversation, msg.ToolCallId);
        }

        return apiMessage;
    }

    /// <summary>The name of the tool an assistant message of the conversation called under <paramref name="id"/>, if any.</summary>
    private static string? NameOfCall(IReadOnlyList<Message> conversation, string id)
    {
        foreach (var message in conversation)
        {
            if (message.Role == MessageRole.Assistant
                && message.ToolCalls?.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal)) is { } call)
            {
                return call.Name;
            }
        }

        return null;
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

        if (request.AdditionalParameters.TryGetValue(MistralToolCallingExtensions.ParallelToolCallsKey, out var parallelRaw)
            && parallelRaw is bool parallel)
        {
            apiRequest.ParallelToolCalls = parallel;
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

    private static CompletionResponse MapToCompletionResponse(MistralChatCompletionResponse apiResponse, ReasoningOptions? reasoningOptions, bool glm)
    {
        var surfaceReasoning = reasoningOptions is { IncludeReasoning: true };
        var choice = apiResponse.Choices.FirstOrDefault();
        var message = choice?.Message;
        var content = MaterialiseContent(message?.Content);
        var reasoning = MaterialiseThinking(message?.Content);

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
            Reasoning = glm
                ? GlmDialect.Surface(reasoning, reasoningOptions)
                : surfaceReasoning && reasoning.Length > 0 ? reasoning : null,
            FinishReason = glm ? GlmDialect.MapFinishReason(choice?.FinishReason) : MapFinishReason(choice?.FinishReason),
            Usage = new UsageStats
            {
                PromptTokens = apiResponse.Usage?.PromptTokens ?? 0,
                CompletionTokens = apiResponse.Usage?.CompletionTokens ?? 0
            },
            ToolCalls = message?.ToolCalls is { Count: > 0 } calls
                ? calls.Select((call, position) => MapToToolCall(call, apiResponse.Id, position)).ToList()
                : null,
            CreatedAt = apiResponse.Created > 0
                ? DateTimeOffset.FromUnixTimeSeconds(apiResponse.Created).UtcDateTime
                : DateTime.UtcNow,
            Metadata = metadata
        };
    }

    private static ToolCall MapToToolCall(MistralToolCall toolCall, string completionId, int position)
    {
        var name = toolCall.Function?.Name ?? string.Empty;
        var arguments = string.IsNullOrEmpty(toolCall.Function?.Arguments) ? "{}" : toolCall.Function.Arguments;
        return new ToolCall
        {
            Id = MistralToolCallAssembler.IdOf(toolCall.Id)
                ?? MistralToolCallAssembler.IdFor(completionId, position, name, arguments),
            Name = name,
            ArgumentsJson = arguments
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

    /// <summary>
    /// The thinking of a reasoning model's content parts: <c>{type: "thinking", thinking: [{type: "text", text}]}</c>,
    /// kept apart from the answer. Empty for plain content.
    /// </summary>
    private static string MaterialiseThinking(object? raw)
    {
        if (raw is not JsonElement el || el.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var part in el.EnumerateArray())
        {
            AppendThinking(sb, part);
        }

        return sb.ToString();
    }

    private static void AppendThinking(System.Text.StringBuilder sb, JsonElement part)
    {
        if (part.ValueKind == JsonValueKind.Object
            && part.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "thinking"
            && part.TryGetProperty("thinking", out var inner) && inner.ValueKind == JsonValueKind.Array)
        {
            foreach (var piece in inner.EnumerateArray())
            {
                if (piece.ValueKind == JsonValueKind.Object && piece.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    sb.Append(text.GetString());
                }
            }
        }
    }

    /// <summary>
    /// A streamed delta's answer text and thinking text: a string is answer, a list is split by chunk type. With
    /// <paramref name="untypedTextIsAnswer"/> (the GLM path), a text part with no <c>type</c> is answer too, as
    /// <see cref="MaterialiseContent"/> reads it in a response: Mistral's schema gives <c>TextChunk.type</c> the default
    /// <c>text</c>. Left off for the other models, whose stream is read as before.
    /// </summary>
    private static (string Content, string? Thinking) SplitDelta(JsonElement? content, bool untypedTextIsAnswer = false)
    {
        if (content is not { } el)
        {
            return (string.Empty, null);
        }

        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return (el.GetString() ?? string.Empty, null);
            case JsonValueKind.Array:
                var answer = new System.Text.StringBuilder();
                var thinking = new System.Text.StringBuilder();
                foreach (var part in el.EnumerateArray())
                {
                    AppendThinking(thinking, part);
                    if (part.ValueKind == JsonValueKind.Object
                        && IsTextPart(part, untypedTextIsAnswer)
                        && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        answer.Append(text.GetString());
                    }
                }

                return (answer.ToString(), thinking.Length == 0 ? null : thinking.ToString());
            default:
                return (string.Empty, null);
        }
    }

    private static bool IsTextPart(JsonElement part, bool untypedTextIsAnswer) =>
        part.TryGetProperty("type", out var type)
            ? type.ValueKind == JsonValueKind.String && type.GetString() == "text"
            : untypedTextIsAnswer;

    private static AgentToolInvocation MapToAgentToolInvocation(MistralToolCall toolCall)
    {
        return new AgentToolInvocation(
            ToolName: toolCall.Function?.Name ?? string.Empty,
            ArgumentsJson: toolCall.Function?.Arguments ?? "{}",
            ResultText: string.Empty,
            IsError: false,
            Latency: TimeSpan.Zero);
    }

    private static CompletionChunk MapToCompletionChunk(MistralStreamChunk chunk, int index, bool surfaceReasoning)
    {
        var choice = chunk.Choices.FirstOrDefault();
        var isFinal = choice?.FinishReason != null;
        var (content, thinking) = SplitDelta(choice?.Delta?.Content);

        return new CompletionChunk
        {
            Id = chunk.Id,
            ContentDelta = content,
            // The thinking of a reasoning model is never answer text; it is returned when asked for (ReasoningOptions).
            ReasoningDelta = surfaceReasoning ? thinking : null,
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

    /// <summary>
    /// A GLM delta: Mistral's chunk lists read apart (thinking, answer — a text part with no <c>type</c> kept), then
    /// normalised by the dialect, which skips a delta with nothing to pass on.
    /// </summary>
    private static CompletionChunk? MapToGlmChunk(MistralStreamChunk chunk, int index, ReasoningOptions? reasoning)
    {
        var choice = chunk.Choices.FirstOrDefault();
        var (content, thinking) = SplitDelta(choice?.Delta?.Content, untypedTextIsAnswer: true);
        var usage = chunk.Usage != null
            ? new UsageStats
            {
                PromptTokens = chunk.Usage.PromptTokens,
                CompletionTokens = chunk.Usage.CompletionTokens
            }
            : null;
        return GlmDialect.Chunk(chunk.Id, index, content, thinking, choice?.FinishReason, usage, reasoning);
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

        var listed = new AIModel
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

        // A GLM gets what its family knows (context window, output limit, that it always reasons); the others, nothing.
        return GlmDialect.Recognise(model.Id) is { } glm ? GlmDialect.Describe(listed, glm) : listed;
    }
}
