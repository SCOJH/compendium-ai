// -----------------------------------------------------------------------
// <copyright file="GlmDialect.Response.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.ZAi.Glm;

// What comes back from a GLM model, normalised the same way whatever host served it.
public static partial class GlmDialect
{
    /// <summary>The <see cref="AIModel.Metadata"/> key <see cref="Describe"/> sets: <c>always</c> or <c>by-default</c>.</summary>
    public const string ReasoningMetadataKey = "reasoning";

    /// <summary>
    /// A GLM finish reason. Over the OpenAI-compatible set: Z.ai's <c>sensitive</c> (its content filter),
    /// <c>model_context_window_exceeded</c> (a length), <c>network_error</c> (how Z.ai ends a stream that failed mid-way);
    /// Mistral's <c>model_length</c> and <c>error</c>.
    /// </summary>
    public static FinishReason MapFinishReason(string? reason) => reason?.ToLowerInvariant() switch
    {
        "stop" => FinishReason.Stop,
        "length" or "model_length" or "model_context_window_exceeded" => FinishReason.Length,
        "content_filter" or "sensitive" => FinishReason.ContentFilter,
        "tool_calls" or "function_call" => FinishReason.ToolCall,
        null => FinishReason.InProgress,
        _ => FinishReason.Other,
    };

    /// <summary>
    /// The reasoning to return: <paramref name="reasoning"/> when the caller asked for it
    /// (<see cref="ReasoningOptions.IncludeReasoning"/>) and the host gave some, <see langword="null"/> otherwise. A GLM
    /// reasons unasked; what it thought is then left out, and never put in the answer.
    /// </summary>
    public static string? Surface(string? reasoning, ReasoningOptions? options) =>
        options is { IncludeReasoning: true } && !string.IsNullOrEmpty(reasoning) ? reasoning : null;

    /// <summary>
    /// One <see cref="CompletionChunk"/> of a GLM stream, from what the host's wire reader found in a delta: the answer
    /// text, the reasoning text, the finish reason, the usage. The answer alone goes to
    /// <see cref="CompletionChunk.ContentDelta"/>; the reasoning goes to <see cref="CompletionChunk.ReasoningDelta"/> only
    /// when asked for. Returns <see langword="null"/> for a delta with nothing to pass on (no answer, no reasoning
    /// returned, not the last one, no usage): the host skips it and does not advance its index, so the same stream reads
    /// as the same chunks on every host.
    /// </summary>
    /// <param name="id">The completion id.</param>
    /// <param name="index">The index this chunk takes if it is emitted.</param>
    /// <param name="answer">The answer text in the delta.</param>
    /// <param name="reasoning">The reasoning text in the delta.</param>
    /// <param name="finishReason">The host's finish reason; set only on the last chunk.</param>
    /// <param name="usage">The usage, when the host sends it with this delta.</param>
    /// <param name="options">The request's <see cref="CompletionRequest.Reasoning"/>.</param>
    public static CompletionChunk? Chunk(
        string id,
        int index,
        string? answer,
        string? reasoning,
        string? finishReason,
        UsageStats? usage,
        ReasoningOptions? options)
    {
        var surfaced = Surface(reasoning, options);
        var isFinal = finishReason is not null;
        if (string.IsNullOrEmpty(answer) && surfaced is null && !isFinal && usage is null)
        {
            return null;
        }

        return new CompletionChunk
        {
            Id = id,
            ContentDelta = answer ?? string.Empty,
            ReasoningDelta = surfaced,
            Index = index,
            IsFinal = isFinal,
            FinishReason = isFinal ? MapFinishReason(finishReason) : null,
            Usage = usage,
        };
    }

    /// <summary>
    /// <paramref name="listed"/>, a host's description of a GLM model, completed with what the family knows: the context
    /// window and the output limit where the host gave none, vision where the model has it, and
    /// <see cref="ReasoningMetadataKey"/>. What the host said is kept.
    /// </summary>
    public static AIModel Describe(AIModel listed, GlmModel model)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(model);

        var metadata = listed.Metadata is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(listed.Metadata);
        if (model.ReasonsByDefault)
        {
            metadata[ReasoningMetadataKey] = model.AlwaysReasons ? "always" : "by-default";
        }

        return listed with
        {
            ContextWindow = listed.ContextWindow ?? model.ContextWindow,
            MaxOutputTokens = listed.MaxOutputTokens ?? model.MaxOutputTokens,
            SupportsVision = listed.SupportsVision || model.Vision,
            Metadata = metadata.Count == 0 ? listed.Metadata : metadata,
        };
    }
}
