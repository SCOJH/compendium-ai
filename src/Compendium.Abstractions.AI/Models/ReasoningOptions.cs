// -----------------------------------------------------------------------
// <copyright file="ReasoningOptions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Abstractions.AI.Models;

/// <summary>
/// Asks the model for its own reasoning channel on one request: Claude's adaptive (or budgeted) extended thinking, the
/// OpenAI o-series' and GPT-5's reasoning effort, Mistral's reasoning (Magistral and the models that take
/// <c>reasoning_effort</c>). Each adapter maps it to what its provider accepts and leaves out what the provider refuses;
/// a provider without a reasoning channel ignores it. Unset (<see cref="CompletionRequest.Reasoning"/> <see langword="null"/>),
/// the request is the one it was before this option existed.
/// </summary>
/// <remarks>
/// <para>
/// With reasoning on, the adapters whose provider refuses sampling parameters leave the temperature and top_p out: Claude
/// refuses them with thinking on, OpenAI's reasoning models refuse them altogether. Mistral's reasoning models take them.
/// </para>
/// <para>
/// The reasoning a provider returns comes back apart from the answer, when <see cref="IncludeReasoning"/> asks for it:
/// <see cref="CompletionChunk.ReasoningDelta"/> in a stream, <see cref="CompletionResponse.Reasoning"/> otherwise, never in
/// <c>ContentDelta</c> / <c>Content</c> (a model that thinks unasked has its thinking left out of the answer too). A
/// caller that keeps a conversation must not send it back as a user or assistant turn.
/// </para>
/// </remarks>
public sealed record ReasoningOptions
{
    /// <summary>
    /// How hard the model thinks, or <see langword="null"/> for the provider's default. Mapped to the provider's own
    /// scale: Claude's <c>output_config.effort</c> (a level the model does not have is brought to the nearest it has),
    /// OpenAI's <c>reasoning_effort</c>, a thinking budget for the Claude models that take one.
    /// </summary>
    public ReasoningEffort? Effort { get; init; }

    /// <summary>
    /// The most tokens the reasoning may take, or <see langword="null"/>. Sent as <c>budget_tokens</c> to the Claude
    /// models that take a budget (at least 1 024). On a model whose reasoning is adaptive (Claude Opus 4.6 and later,
    /// Sonnet 4.6 and later, Fable, Mythos: a budget is refused there) it is added to <c>max_tokens</c> as headroom: the
    /// thinking counts against <c>max_tokens</c>, and the answer keeps the room <see cref="CompletionRequest.MaxTokens"/>
    /// gave it.
    /// </summary>
    public int? BudgetTokens { get; init; }

    /// <summary>
    /// Whether the provider is asked to return the reasoning (a summary of it, on Claude) as well as to do it. Default
    /// <see langword="true"/>. <see langword="false"/> asks for the thinking without its text (Claude
    /// <c>display: "omitted"</c>), which saves the transfer, not the tokens.
    /// </summary>
    public bool IncludeReasoning { get; init; } = true;
}

/// <summary>How hard a model reasons (<see cref="ReasoningOptions.Effort"/>), from the least to the most.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReasoningEffort
{
    /// <summary>The least reasoning the provider allows.</summary>
    Low,

    /// <summary>A middle setting.</summary>
    Medium,

    /// <summary>Thorough reasoning; the default of most providers.</summary>
    High,

    /// <summary>Between high and max, where the provider has it (Claude Opus 4.7 and later); high elsewhere.</summary>
    XHigh,

    /// <summary>The most reasoning the provider allows.</summary>
    Max,
}
