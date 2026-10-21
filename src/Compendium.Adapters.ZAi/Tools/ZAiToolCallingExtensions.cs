// -----------------------------------------------------------------------
// <copyright file="ZAiToolCallingExtensions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Abstractions.AI.Agents.Models;

namespace Compendium.Adapters.ZAi.Tools;

/// <summary>
/// Helpers for attaching tool definitions to a request and reading back the tool calls of a response, through
/// <see cref="CompletionRequest.AdditionalParameters"/> and <see cref="CompletionResponse.Metadata"/> as the other
/// adapters do. Z.ai takes up to 128 functions and only <c>tool_choice: "auto"</c>
/// (https://docs.z.ai/api-reference/llm/chat-completion.md, read 2026-10-20): any other choice is refused before the
/// request is sent.
/// </summary>
public static class ZAiToolCallingExtensions
{
    /// <summary>Key inside <see cref="CompletionRequest.AdditionalParameters"/> carrying the tool list.</summary>
    public const string ToolsKey = "zai.tools";

    /// <summary>Key inside <see cref="CompletionRequest.AdditionalParameters"/> carrying the tool_choice value.</summary>
    public const string ToolChoiceKey = "zai.tool_choice";

    /// <summary>Key inside <see cref="CompletionResponse.Metadata"/> carrying the assistant's tool_calls.</summary>
    public const string ToolCallsMetadataKey = "zai.tool_calls";

    /// <summary>The one tool choice Z.ai takes.</summary>
    public const string AutoToolChoice = "auto";

    /// <summary>
    /// Attaches a tool catalog to a completion request. The model may answer with tool calls, read back with
    /// <see cref="GetToolCalls"/>.
    /// </summary>
    /// <param name="request">The request to clone.</param>
    /// <param name="tools">The tools to expose; ignored when empty.</param>
    /// <param name="toolChoice">Optional; only <c>"auto"</c> (Z.ai's default) is accepted.</param>
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
    /// Reads back the tool calls the model requested, if any; an empty list when it called none.
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
