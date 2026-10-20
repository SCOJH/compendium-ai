// -----------------------------------------------------------------------
// <copyright file="GlmDialect.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Compendium.Adapters.ZAi.Glm;

/// <summary>
/// What a GLM model is and how a request to it is shaped, whatever host serves it. Pure functions: the host adapter keeps
/// its own wire format (Z.ai's <c>reasoning_content</c>, Mistral's <c>thinking</c> chunks, OpenRouter's
/// <c>reasoning</c>) and asks the dialect what the GLM family needs.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Recognition</b> — <see cref="Recognise"/> reads <c>glm-5.3</c> (Z.ai), <c>zai-glm-5-3</c> (Mistral),
/// <c>z-ai/glm-5.3</c> and <c>~z-ai/glm-latest</c> (OpenRouter), <c>zai-org/GLM-5.3</c> (Hugging Face), with a tier
/// (<c>-flash</c>, <c>-flashx</c>, <c>-prime</c>, <c>-turbo</c>, <c>-air</c>, <c>v</c>) and a variant suffix
/// (<c>:batch</c>). Anything else is not GLM, and the host sends it exactly as before.</item>
/// <item><b>Effort</b> — GLM 5.2 and later take <c>low</c>, <c>high</c> or <c>max</c>; GLM 5.3 refuses any other value on
/// Z.ai's API. The five levels of <see cref="Abstractions.AI.Models.ReasoningEffort"/> are brought onto that scale the
/// way Z.ai's own compatibility mapping does: low → low, medium and high → high, xhigh and max → max.</item>
/// <item><b>Room for the reasoning</b> — the reasoning counts against <c>max_tokens</c>, and GLM reasons by default (5.3
/// always, at <c>max</c> unless told otherwise): <see cref="MaxTokens"/> adds room for it to the answer's, so a short
/// <c>max_tokens</c> is not spent before the answer starts.</item>
/// </list>
/// </remarks>
public static partial class GlmDialect
{
    /// <summary>Room for the reasoning at <c>low</c> effort.</summary>
    public const int LowEffortHeadroom = 4_096;

    /// <summary>Room for the reasoning at <c>high</c> effort, and for a GLM that takes no effort (it decides alone).</summary>
    public const int HighEffortHeadroom = 16_384;

    /// <summary>Room for the reasoning at <c>max</c> effort, the default of GLM 5.2 and later.</summary>
    public const int MaxEffortHeadroom = 32_768;

    // The newest GLM documented on 2026-10-20: what the "latest" aliases and Mistral's "zai-glm-5" point to.
    private const int NewestMajor = 5;
    private const int NewestMinor = 3;

    /// <summary>The GLM model <paramref name="modelId"/> names, or <see langword="null"/> when it is not a GLM id.</summary>
    public static GlmModel? Recognise(string? modelId) => TryRecognise(modelId, out var model) ? model : null;

    /// <summary>Reads <paramref name="modelId"/> as a GLM id, whatever the host's prefix.</summary>
    /// <param name="modelId">The model id as a host takes it.</param>
    /// <param name="model">The model, when the id is a GLM one.</param>
    /// <returns>Whether <paramref name="modelId"/> is a GLM id.</returns>
    public static bool TryRecognise(string? modelId, [NotNullWhen(true)] out GlmModel? model)
    {
        model = null;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        // A gateway's path (z-ai/, zai-org/, openrouter/z-ai/) and OpenRouter's alias mark (~) say nothing of the model.
        var id = modelId.Trim();
        var name = id[(id.LastIndexOf('/') + 1)..].TrimStart('~');
        var match = Name().Match(name);
        if (!match.Success)
        {
            return false;
        }

        int major, minor;
        string? variant;
        if (match.Groups["latest"].Success)
        {
            (major, minor) = (NewestMajor, NewestMinor);
            variant = match.Groups["flash"].Success ? "flash" : null;
        }
        else
        {
            major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
            minor = match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture) : 0;
            variant = match.Groups["variant"].Success ? match.Groups["variant"].Value.ToLowerInvariant() : null;

            // Mistral's "zai-glm-5" is an alias of its newest GLM (5.3), where Z.ai's and OpenRouter's "glm-5" is GLM-5.
            if (match.Groups["zai"].Success && major == 5 && !match.Groups["minor"].Success)
            {
                (major, minor) = (NewestMajor, NewestMinor);
            }
        }

        model = Profile(id, major, minor, variant, match.Groups["v"].Success);
        return true;
    }

    /// <summary>
    /// The <c>reasoning_effort</c> to send for <paramref name="reasoning"/>: <c>low</c>, <c>high</c> or <c>max</c>, or
    /// <see langword="null"/> to send none (no <see cref="ReasoningOptions.Effort"/> asked, or a GLM that takes no effort)
    /// and leave the host's default.
    /// </summary>
    public static string? EffortFor(GlmModel model, ReasoningOptions? reasoning)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.TakesReasoningEffort || reasoning?.Effort is not { } effort)
        {
            return null;
        }

        return effort switch
        {
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium or ReasoningEffort.High => "high",
            ReasoningEffort.XHigh or ReasoningEffort.Max => "max",
            _ => null,
        };
    }

    /// <summary>
    /// The tokens to add to <c>max_tokens</c> for the reasoning: <see cref="ReasoningOptions.BudgetTokens"/> when the
    /// caller gives one, else one per effort (the effort sent, or the model's default <c>max</c>; a GLM that takes no
    /// effort gets <see cref="HighEffortHeadroom"/>). None for a GLM that does not reason.
    /// </summary>
    public static int ReasoningHeadroom(GlmModel model, ReasoningOptions? reasoning)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.ReasonsByDefault)
        {
            return 0;
        }

        if (reasoning?.BudgetTokens is int budget && budget > 0)
        {
            return budget;
        }

        if (!model.TakesReasoningEffort)
        {
            return HighEffortHeadroom;
        }

        return EffortFor(model, reasoning) switch
        {
            "low" => LowEffortHeadroom,
            "high" => HighEffortHeadroom,
            _ => MaxEffortHeadroom,
        };
    }

    /// <summary>
    /// The <c>max_tokens</c> to send: the answer's room (<paramref name="requested"/>, else
    /// <paramref name="adapterDefault"/>) plus <see cref="ReasoningHeadroom"/>, never above the model's
    /// <see cref="GlmModel.MaxOutputTokens"/>. It applies with or without <see cref="ReasoningOptions"/>: a GLM reasons
    /// unasked, and its reasoning would otherwise take the answer's room.
    /// </summary>
    public static int MaxTokens(GlmModel model, int? requested, ReasoningOptions? reasoning, int adapterDefault)
    {
        ArgumentNullException.ThrowIfNull(model);
        var total = (long)(requested ?? adapterDefault) + ReasoningHeadroom(model, reasoning);
        if (model.MaxOutputTokens is { } cap && total > cap)
        {
            total = cap;
        }

        return (int)Math.Min(total, int.MaxValue);
    }

    private static GlmModel Profile(string id, int major, int minor, string? variant, bool v)
    {
        bool AtLeast(int a, int b) => major > a || (major == a && minor >= b);

        var (context, output) = major switch
        {
            _ when AtLeast(5, 2) => (1_048_576, 131_072),
            5 => (v ? 202_752 : 204_800, 131_072),
            4 when minor == 7 => (204_800, 131_072),
            4 when minor == 6 => v ? (131_072, 32_768) : (204_800, 131_072),
            4 when minor == 5 => v ? (65_536, 16_384) : (131_072, 98_304),
            _ => ((int?)null, (int?)null),
        };

        return new GlmModel
        {
            Id = id,
            Major = major,
            Minor = minor,
            Variant = variant,
            Vision = v || (AtLeast(5, 3) && variant is "flash" or "flashx"),
            AlwaysReasons = AtLeast(5, 3),
            ReasonsByDefault = AtLeast(4, 5),
            TakesReasoningEffort = AtLeast(5, 2),
            ContextWindow = context,
            MaxOutputTokens = output,
        };
    }

    // glm-5.3, glm-5.3-flashx, glm-5.3:batch, glm-4.6v, glm-5v-turbo, glm-4.5-air, zai-glm-5-3 (Mistral), zai-glm-latest,
    // glm-latest, glm-flash-latest (OpenRouter's ~z-ai/ aliases, the ~ and the path taken off before). Not chatglm3-6b,
    // glm-4-9b-chat, glmx-1 or my-glm-proxy: the whole name must be a GLM release name.
    [GeneratedRegex(
        @"^(?<zai>zai-)?glm-(?:(?<latest>(?<flash>flash-)?latest)|(?<major>\d{1,2})(?:[.\-](?<minor>\d{1,2}))?(?<v>v)?(?:-(?<variant>flashx|flash|prime|turbo|air))?)(?::[a-z0-9._\-]+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Name();
}
