// -----------------------------------------------------------------------
// <copyright file="GlmModel.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.ZAi.Glm;

/// <summary>
/// A GLM model (Z.ai's open-weight family) as <see cref="GlmDialect.Recognise"/> reads it from an id, whatever the host's
/// prefix: its version, and what the family does by default at that version. The same model behind Z.ai
/// (<c>glm-5.3</c>), Mistral (<c>zai-glm-5-3</c>) or OpenRouter (<c>z-ai/glm-5.3</c>) gives the same <see cref="GlmModel"/>
/// but its <see cref="Id"/>.
/// </summary>
/// <remarks>
/// The values are the family's, read from the hosts' public documentation on 2026-10-20 (see the dialect's README and
/// <c>docs/adr/0001-glm-dialect.md</c>): a host's own catalog wins where it says otherwise, which is why
/// <see cref="GlmDialect.Describe"/> only fills what a host leaves unset.
/// </remarks>
public sealed record GlmModel
{
    /// <summary>The id as the caller gave it (host prefix and suffix included).</summary>
    public required string Id { get; init; }

    /// <summary>The major version: <c>5</c> for GLM 5.3.</summary>
    public required int Major { get; init; }

    /// <summary>The minor version: <c>3</c> for GLM 5.3, <c>0</c> when the id has none (<c>glm-5</c>).</summary>
    public required int Minor { get; init; }

    /// <summary>The tier, lower case, when the id names one: <c>flash</c>, <c>flashx</c>, <c>prime</c>, <c>turbo</c>, <c>air</c>.</summary>
    public string? Variant { get; init; }

    /// <summary>Whether the model reads images (the <c>v</c> models, GLM 5.3 Flash and FlashX).</summary>
    public bool Vision { get; init; }

    /// <summary>
    /// Whether the model always reasons, with no way to turn it off: GLM 5.3 and later. Z.ai answers
    /// <c>thinking: {type: "disabled"}</c> with an error there, Mistral has no plain-string mode for it, and OpenRouter marks
    /// its reasoning mandatory.
    /// </summary>
    public bool AlwaysReasons { get; init; }

    /// <summary>
    /// Whether the model reasons unless told not to: GLM 4.5 and later. Its reasoning counts against <c>max_tokens</c>, so
    /// the dialect gives <c>max_tokens</c> room for it (<see cref="GlmDialect.MaxTokens"/>).
    /// </summary>
    public bool ReasonsByDefault { get; init; }

    /// <summary>
    /// Whether the model takes a reasoning effort: GLM 5.2 and later, on the <c>low</c> · <c>high</c> · <c>max</c> scale
    /// from 5.3 on and <c>high</c> · <c>xhigh</c> for 5.2 (<see cref="GlmDialect.EffortFor"/>). The earlier versions
    /// decide alone how much they reason.
    /// </summary>
    public bool TakesReasoningEffort { get; init; }

    /// <summary>The context window in tokens, or <see langword="null"/> where the family's documentation gives none.</summary>
    public int? ContextWindow { get; init; }

    /// <summary>The most tokens one answer may take, reasoning included, or <see langword="null"/> where unknown.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Whether this is GLM <paramref name="major"/>.<paramref name="minor"/> or later.</summary>
    public bool IsAtLeast(int major, int minor) => Major > major || (Major == major && Minor >= minor);
}
