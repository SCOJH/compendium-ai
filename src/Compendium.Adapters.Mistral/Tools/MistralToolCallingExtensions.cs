// -----------------------------------------------------------------------
// <copyright file="MistralToolCallingExtensions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Abstractions.AI.Agents.Models;

namespace Compendium.Adapters.Mistral.Tools;

/// <summary>
/// Ergonomic helpers for attaching tool definitions and reading back tool invocations
/// when round-tripping through the abstractions' provider-agnostic <see cref="CompletionRequest"/>
/// and <see cref="CompletionResponse"/>.
/// </summary>
/// <remarks>
/// Mistral's tool calling format mirrors OpenAI's <c>tools[]</c> array exactly — function name,
/// description, and a JSON-schema parameters block. Tool calls come back on the assistant message
/// the same way too.
/// </remarks>
public static class MistralToolCallingExtensions
{
    /// <summary>Key inside <see cref="CompletionRequest.AdditionalParameters"/> carrying the tool list.</summary>
    public const string ToolsKey = "mistral.tools";

    /// <summary>Key inside <see cref="CompletionRequest.AdditionalParameters"/> carrying the tool_choice value.</summary>
    public const string ToolChoiceKey = "mistral.tool_choice";

    /// <summary>Key inside <see cref="CompletionResponse.Metadata"/> carrying the assistant's tool_calls.</summary>
    public const string ToolCallsMetadataKey = "mistral.tool_calls";

    /// <summary>Key inside <see cref="CompletionRequest.AdditionalParameters"/> carrying the parallel_tool_calls value.</summary>
    public const string ParallelToolCallsKey = "mistral.parallel_tool_calls";

    /// <summary>
    /// Attaches a tool catalog to a completion request. The model receives the tool list and may
    /// emit one or more <see cref="AgentToolInvocation"/> entries in
    /// <see cref="CompletionResponse.Metadata"/> under <see cref="ToolCallsMetadataKey"/>.
    /// </summary>
    /// <param name="request">The request to clone.</param>
    /// <param name="tools">The tools to expose; ignored when empty.</param>
    /// <param name="toolChoice">Optional choice strategy ("auto", "any", "none", or a tool name).</param>
    public static CompletionRequest WithTools(
        this CompletionRequest request,
        IReadOnlyList<AgentTool> tools,
        string? toolChoice = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tools);

        var dict = new Dictionary<string, object>(request.AdditionalParameters ?? new Dictionary<string, object>())
        {
            [ToolsKey] = tools
        };
        if (!string.IsNullOrEmpty(toolChoice))
        {
            dict[ToolChoiceKey] = toolChoice;
        }

        return request with { AdditionalParameters = dict };
    }

    /// <summary>
    /// Sets Mistral's <c>parallel_tool_calls</c>: with <see langword="false"/>, the model calls one tool at most per turn
    /// ("forces the model to use single tool calling", https://docs.mistral.ai/studio/conversations/function-calling.md);
    /// with <see langword="true"/>, it may call several in one turn, each to be answered by its own tool message. Without
    /// this call, the field is not sent and Mistral's default applies (several allowed).
    /// </summary>
    /// <param name="request">The request to clone.</param>
    /// <param name="allowed">Whether the model may call several tools in one turn.</param>
    public static CompletionRequest WithParallelToolCalls(this CompletionRequest request, bool allowed)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dict = new Dictionary<string, object>(request.AdditionalParameters ?? new Dictionary<string, object>())
        {
            [ParallelToolCallsKey] = allowed
        };
        return request with { AdditionalParameters = dict };
    }

    /// <summary>
    /// Reads back tool calls the model requested, if any. Returns an empty list when the model
    /// did not call a tool.
    /// </summary>
    public static IReadOnlyList<AgentToolInvocation> GetToolCalls(this CompletionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Metadata != null
            && response.Metadata.TryGetValue(ToolCallsMetadataKey, out var raw)
            && raw is IReadOnlyList<AgentToolInvocation> invocations)
        {
            return invocations;
        }
        return Array.Empty<AgentToolInvocation>();
    }
}
