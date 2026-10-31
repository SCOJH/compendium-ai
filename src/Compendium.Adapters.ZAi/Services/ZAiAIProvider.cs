// -----------------------------------------------------------------------
// <copyright file="ZAiAIProvider.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Abstractions.AI.Agents.Models;
using Compendium.Adapters.ZAi.Configuration;
using Compendium.Adapters.ZAi.Glm;
using Compendium.Adapters.ZAi.Http;
using Compendium.Adapters.ZAi.Http.Models;
using Compendium.Adapters.ZAi.Tools;

namespace Compendium.Adapters.ZAi.Services;

/// <summary>
/// Z.ai implementation of <see cref="IAIProvider"/>: the GLM models through Z.ai's own OpenAI-compatible API. Chat
/// completions, streaming, reasoning and native tool calling.
/// </summary>
/// <remarks>
/// <para>
/// What a GLM model needs comes from the GLM dialect (<see cref="GlmDialect"/>), shared with the Mistral and OpenRouter
/// adapters: the effort on GLM's <c>low</c> · <c>high</c> · <c>max</c> scale, <c>max_tokens</c> with room for the
/// reasoning. What this adapter adds is Z.ai's wire format: <c>thinking: {type: "enabled"}</c> when reasoning is asked for
/// (never <c>disabled</c>: GLM 5.3 refuses it), the reasoning in <c>reasoning_content</c> — returned as
/// <see cref="CompletionChunk.ReasoningDelta"/> / <see cref="CompletionResponse.Reasoning"/> when asked for, never in the
/// answer — the <c>tool_calls</c> arguments as a string or an object, Z.ai's finish reasons and business error codes.
/// </para>
/// <para>
/// Z.ai documents no embeddings endpoint on this API, nor a model list: <see cref="EmbedAsync"/> refuses, and
/// <see cref="ListModelsAsync"/> returns the GLM models documented on 2026-10-20.
/// </para>
/// </remarks>
internal sealed class ZAiAIProvider : IAIProvider
{
    private const string UnsupportedErrorCode = "AI.Unsupported";

    /// <summary>The chat models of Z.ai's API on 2026-10-20 (https://docs.z.ai/api-reference/llm/chat-completion.md).</summary>
    internal static readonly IReadOnlyList<string> KnownModelIds =
        ["glm-5.3", "glm-5.3-flash", "glm-5.3-flashx", "glm-5.2", "glm-5.1", "glm-5", "glm-4.7", "glm-4.6"];

    private static readonly IReadOnlyList<AIModel> KnownModels = KnownModelIds
        .Select(id => GlmDialect.Describe(
            new AIModel { Id = id, Name = id, Provider = "zai", SupportsStreaming = true, SupportsTools = true },
            GlmDialect.Recognise(id)!))
        .ToList();

    private readonly ZAiHttpClient _httpClient;
    private readonly ZAiOptions _options;
    private readonly ILogger<ZAiAIProvider> _logger;

    public ZAiAIProvider(
        ZAiHttpClient httpClient,
        IOptions<ZAiOptions> options,
        ILogger<ZAiAIProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "zai";

    /// <inheritdoc />
    public async Task<Result<CompletionResponse>> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;
        _logger.LogDebug("Sending Z.ai chat completion to model {Model}", model);

        var apiRequest = MapToApiRequest(request, model, stream: false);
        if (apiRequest.IsFailure)
        {
            return Result.Failure<CompletionResponse>(apiRequest.Error);
        }

        var result = await _httpClient.CreateChatCompletionAsync(apiRequest.Value, cancellationToken);
        return result.Match(
            r => Result.Success(MapToCompletionResponse(r, request.Reasoning)),
            error => Result.Failure<CompletionResponse>(error));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<CompletionChunk>> StreamCompleteAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = string.IsNullOrEmpty(request.Model) ? _options.DefaultModel : request.Model;
        _logger.LogDebug("Sending Z.ai streaming chat completion to model {Model}", model);

        var apiRequest = MapToApiRequest(request, model, stream: true);
        if (apiRequest.IsFailure)
        {
            yield return Result.Failure<CompletionChunk>(apiRequest.Error);
            yield break;
        }

        var index = 0;
        await foreach (var chunk in _httpClient.StreamChatCompletionAsync(apiRequest.Value, cancellationToken))
        {
            if (chunk.IsFailure)
            {
                yield return Result.Failure<CompletionChunk>(chunk.Error);
                yield break;
            }

            var choice = chunk.Value.Choices.FirstOrDefault();
            var mapped = GlmDialect.Chunk(
                chunk.Value.Id,
                index,
                choice?.Delta?.Content,
                choice?.Delta?.ReasoningContent,
                choice?.FinishReason,
                MapUsage(chunk.Value.Usage),
                request.Reasoning);
            if (mapped is null)
            {
                // Reasoning not asked for, or an empty delta: nothing to pass on.
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
        var error = Error.Unavailable(
            UnsupportedErrorCode,
            "The Z.ai adapter offers no embeddings. Use another adapter for embeddings.");
        return Task.FromResult(Result.Failure<EmbeddingResponse>(error));
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<AIModel>>> ListModelsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(KnownModels));

    /// <summary>
    /// A one-token probe of <see cref="ZAiOptions.DefaultModel"/> (Z.ai has no model list to call): billed as such.
    /// </summary>
    public async Task<Result> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var probe = new ZAiChatCompletionRequest
            {
                Model = _options.DefaultModel,
                Messages = [new ZAiRequestMessage { Role = "user", Content = "ping" }],
                MaxTokens = 1,
            };
            var result = await _httpClient.CreateChatCompletionAsync(probe, cancellationToken);
            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Health check failed for Z.ai provider");
            return Result.Failure(AIErrors.ProviderUnavailable("zai"));
        }
    }

    /// <summary>
    /// A <see cref="CompletionRequest.ResponseSchema"/> Z.ai is not asked to follow: the request goes without it, with
    /// a warning (<see cref="IAIProvider.SupportsJsonSchema"/> stays <see langword="false"/>).
    /// </summary>
    private void WarnResponseSchemaNotSent(CompletionRequest request, string model)
    {
        if (request.ResponseSchema is { } responseSchema)
        {
            _logger.LogWarning(
                "Z.ai does not constrain model {Model} to a JSON schema: ResponseSchema {Schema} is not sent",
                model,
                responseSchema.Name);
        }
    }

    private Result<ZAiChatCompletionRequest> MapToApiRequest(CompletionRequest request, string model, bool stream)
    {
        WarnResponseSchemaNotSent(request, model);

        var glm = GlmDialect.Recognise(model);

        var messages = new List<ZAiRequestMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new ZAiRequestMessage { Role = "system", Content = request.SystemPrompt });
        }

        foreach (var msg in request.Messages)
        {
            messages.Add(new ZAiRequestMessage { Role = msg.Role.ToString().ToLowerInvariant(), Content = msg.Content });
        }

        var apiRequest = new ZAiChatCompletionRequest
        {
            Model = model,
            Messages = messages,

            // Asked for, the reasoning is turned on in so many words (it is Z.ai's default). Never turned off: GLM 5.3
            // answers "disabled" with an error, and an unset Reasoning means the model's default, not off.
            Thinking = request.Reasoning is not null && glm is { ReasonsByDefault: true }
                ? new ZAiThinking { Type = "enabled" }
                : null,
            ReasoningEffort = glm is null ? null : GlmDialect.EffortFor(glm, request.Reasoning),
            Temperature = request.Temperature ?? CompletionRequest.FallbackTemperature,
            TopP = request.TopP,
            MaxTokens = glm is null
                ? request.MaxTokens ?? _options.DefaultMaxTokens
                : GlmDialect.MaxTokens(glm, request.MaxTokens, request.Reasoning, _options.DefaultMaxTokens),
            Stop = request.StopSequences?.ToList(),
            Stream = stream,
            RequestId = Bounded(request.RequestId, 6, 64),
            UserId = Bounded(request.UserId, 6, 128),
        };

        return ApplyTools(apiRequest, request);
    }

    private static Result<ZAiChatCompletionRequest> ApplyTools(ZAiChatCompletionRequest apiRequest, CompletionRequest request)
    {
        if (request.AdditionalParameters == null)
        {
            return Result.Success(apiRequest);
        }

        if (request.AdditionalParameters.TryGetValue(ZAiToolCallingExtensions.ToolsKey, out var toolsRaw)
            && toolsRaw is IReadOnlyList<AgentTool> tools
            && tools.Count > 0)
        {
            apiRequest.Tools = tools.Select(t => new ZAiToolDefinition
            {
                Function = new ZAiFunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = ParseSchemaOrDefault(t.InputSchemaJson)
                }
            }).ToList();
        }

        if (request.AdditionalParameters.TryGetValue(ZAiToolCallingExtensions.ToolChoiceKey, out var choiceRaw)
            && choiceRaw is string toolChoice
            && !string.IsNullOrEmpty(toolChoice))
        {
            if (!string.Equals(toolChoice, ZAiToolCallingExtensions.AutoToolChoice, StringComparison.Ordinal))
            {
                return Result.Failure<ZAiChatCompletionRequest>(AIErrors.InvalidRequest(
                    $"Z.ai takes only tool_choice \"auto\"; \"{toolChoice}\" is not sent."));
            }

            apiRequest.ToolChoice = toolChoice;
        }

        return Result.Success(apiRequest);
    }

    private static JsonElement? ParseSchemaOrDefault(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Bounded(string? value, int min, int max) =>
        value is { Length: var n } && n >= min && n <= max ? value : null;

    private static CompletionResponse MapToCompletionResponse(ZAiChatCompletionResponse apiResponse, ReasoningOptions? reasoning)
    {
        var choice = apiResponse.Choices.FirstOrDefault();
        var message = choice?.Message;

        Dictionary<string, object>? metadata = null;
        if (message?.ToolCalls is { Count: > 0 } calls)
        {
            metadata = new Dictionary<string, object>
            {
                [ZAiToolCallingExtensions.ToolCallsMetadataKey] = (IReadOnlyList<AgentToolInvocation>)calls.Select(MapToAgentToolInvocation).ToList()
            };
        }

        return new CompletionResponse
        {
            Id = apiResponse.Id,
            Model = apiResponse.Model,
            Content = message?.Content ?? string.Empty,
            Reasoning = GlmDialect.Surface(message?.ReasoningContent, reasoning),
            FinishReason = GlmDialect.MapFinishReason(choice?.FinishReason),
            Usage = MapUsage(apiResponse.Usage) ?? new UsageStats { PromptTokens = 0, CompletionTokens = 0 },
            CreatedAt = apiResponse.Created > 0
                ? DateTimeOffset.FromUnixTimeSeconds(apiResponse.Created).UtcDateTime
                : DateTime.UtcNow,
            Metadata = metadata
        };
    }

    private static AgentToolInvocation MapToAgentToolInvocation(ZAiToolCall toolCall)
    {
        var arguments = toolCall.Function?.Arguments switch
        {
            { ValueKind: JsonValueKind.String } text => text.GetString() ?? "{}",
            { ValueKind: JsonValueKind.Object or JsonValueKind.Array } json => json.GetRawText(),
            _ => "{}",
        };

        return new AgentToolInvocation(
            ToolName: toolCall.Function?.Name ?? string.Empty,
            ArgumentsJson: arguments,
            ResultText: string.Empty,
            IsError: false,
            Latency: TimeSpan.Zero);
    }

    private static UsageStats? MapUsage(ZAiUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        int? reasoningTokens = usage.CompletionTokensDetails is { ValueKind: JsonValueKind.Object } details
            && details.TryGetProperty("reasoning_tokens", out var tokens)
            && tokens.ValueKind == JsonValueKind.Number
            && tokens.TryGetInt32(out var count)
                ? count
                : null;

        return new UsageStats
        {
            PromptTokens = usage.PromptTokens,
            CompletionTokens = usage.CompletionTokens,
            ReasoningTokens = reasoningTokens
        };
    }
}
