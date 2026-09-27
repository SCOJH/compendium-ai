// -----------------------------------------------------------------------
// <copyright file="ClaudeReasoning.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Text.RegularExpressions;
using Compendium.Adapters.Anthropic.Http.Models;

namespace Compendium.Adapters.Anthropic.Services;

/// <summary>
/// How a Claude model takes extended thinking (<see cref="ReasoningOptions"/>), read from its id whatever the date suffix.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Adaptive</b> — Claude Opus 4.6 and later, Sonnet 4.6 and later, Fable, Mythos: <c>thinking: {type: "adaptive"}</c>
/// and <c>output_config.effort</c>. A <c>budget_tokens</c> is refused there (400 from Opus 4.7, Sonnet 5, Fable).
/// <c>xhigh</c> exists from Opus 4.7 and on Sonnet 5, Fable and Mythos; the 4.6 models get <c>high</c> for it. Their thinking
/// text is omitted by default from Opus 4.7 on: <c>display: "summarized"</c> asks for it.</item>
/// <item><b>Budget</b> — Opus 4.5 and earlier 4.x, Sonnet 4.5 and 4, Haiku 4.5, Sonnet 3.7: <c>thinking: {type: "enabled",
/// budget_tokens}</c>, at least 1 024 and below <c>max_tokens</c>.</item>
/// <item><b>None</b> — the older models, which have no thinking: the request is sent without it.</item>
/// </list>
/// Either way the thinking counts against <c>max_tokens</c>, and a thinking request takes no temperature and no top_p.
/// </remarks>
internal static partial class ClaudeReasoning
{
    /// <summary>The smallest <c>budget_tokens</c> Anthropic accepts.</summary>
    public const int MinBudgetTokens = 1_024;

    /// <summary>How a model takes thinking.</summary>
    internal enum Kind
    {
        /// <summary>No thinking: the request is sent without it.</summary>
        None,

        /// <summary><c>thinking: {type: "enabled", budget_tokens}</c>.</summary>
        Budget,

        /// <summary><c>thinking: {type: "adaptive"}</c> and <c>output_config.effort</c>.</summary>
        Adaptive,
    }

    /// <summary>What <paramref name="model"/> takes, and whether it has the <c>xhigh</c> effort.</summary>
    public static (Kind Kind, bool HasXHigh) Of(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return (Kind.None, false);
        }

        var current = Family().Match(model);
        if (current.Success)
        {
            var family = current.Groups["family"].Value.ToLowerInvariant();
            var major = current.Groups["major"].Success ? int.Parse(current.Groups["major"].Value, CultureInfo.InvariantCulture) : -1;
            var minor = current.Groups["minor"].Success ? int.Parse(current.Groups["minor"].Value, CultureInfo.InvariantCulture) : 0;
            return family switch
            {
                "fable" or "mythos" => (Kind.Adaptive, true),
                _ when major >= 5 => (Kind.Adaptive, true),
                "opus" when major == 4 && minor >= 7 => (Kind.Adaptive, true),
                "opus" or "sonnet" when major == 4 && minor == 6 => (Kind.Adaptive, false),
                "opus" or "sonnet" or "haiku" when major == 4 => (Kind.Budget, false),
                _ => (Kind.None, false),
            };
        }

        // The 3.x naming puts the version first: claude-3-7-sonnet has thinking, the older ones none.
        var legacy = Legacy().Match(model);
        return legacy.Success && legacy.Groups["major"].Value == "3" && legacy.Groups["minor"].Value == "7"
            && string.Equals(legacy.Groups["family"].Value, "sonnet", StringComparison.OrdinalIgnoreCase)
            ? (Kind.Budget, false)
            : (Kind.None, false);
    }

    /// <summary>The thinking budget of a budgeted model: the caller's, else one per effort; never below the minimum.</summary>
    public static int BudgetFor(ReasoningOptions reasoning) =>
        Math.Max(MinBudgetTokens, reasoning.BudgetTokens ?? reasoning.Effort switch
        {
            ReasoningEffort.Low => 2_048,
            ReasoningEffort.High => 16_000,
            ReasoningEffort.XHigh => 24_000,
            ReasoningEffort.Max => 32_000,
            _ => 8_192,
        });

    /// <summary>The <c>output_config.effort</c> of an adaptive model, or null for the model's own default.</summary>
    public static string? EffortFor(ReasoningEffort? effort, bool hasXHigh) => effort switch
    {
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.XHigh => hasXHigh ? "xhigh" : "high",
        ReasoningEffort.Max => "max",
        _ => null,
    };

    /// <summary>
    /// Sets thinking on <paramref name="apiRequest"/> for <paramref name="reasoning"/>: the thinking block, the effort, no
    /// sampling parameter, and <c>max_tokens</c> raised by what the thinking may take, so the answer keeps its room.
    /// Returns false, and leaves the request untouched, for a model with no thinking.
    /// </summary>
    public static bool Apply(AnthropicMessagesRequest apiRequest, ReasoningOptions reasoning)
    {
        var (kind, hasXHigh) = Of(apiRequest.Model);
        switch (kind)
        {
            case Kind.Adaptive:
                apiRequest.Thinking = new AnthropicThinking { Type = "adaptive", Display = reasoning.IncludeReasoning ? "summarized" : "omitted" };
                apiRequest.OutputConfig = EffortFor(reasoning.Effort, hasXHigh) is { } effort ? new AnthropicOutputConfig { Effort = effort } : null;
                apiRequest.MaxTokens += Math.Max(0, reasoning.BudgetTokens ?? 0);
                break;
            case Kind.Budget:
                var budget = BudgetFor(reasoning);
                apiRequest.Thinking = new AnthropicThinking { Type = "enabled", BudgetTokens = budget };
                apiRequest.MaxTokens += budget;
                break;
            default:
                return false;
        }

        apiRequest.Temperature = null;
        apiRequest.TopP = null;
        return true;
    }

    // claude-opus-4-7, claude-sonnet-5, claude-opus-5-5, claude-haiku-4-5-20251001, claude-sonnet-4-20250514 (4, no minor),
    // anthropic.claude-opus-4-7 (a gateway's prefix): a 1-2 digit minor, never the start of a date.
    [GeneratedRegex(@"claude-(?<family>opus|sonnet|haiku|fable|mythos)(?:-(?<major>\d{1,2})(?:[-.](?<minor>\d{1,2})(?!\d))?(?!\d))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Family();

    [GeneratedRegex(@"claude-(?<major>\d)-(?<minor>\d)-(?<family>opus|sonnet|haiku)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Legacy();
}
