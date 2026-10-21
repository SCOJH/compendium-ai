// -----------------------------------------------------------------------
// <copyright file="GlmRequestTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.ZAi.Glm.Tests;

/// <summary>
/// How a request to a GLM is shaped. The effort scale is Z.ai's (https://docs.z.ai/guides/capabilities/thinking.md, read
/// 2026-10-20: GLM 5.3 takes low / high / max only, "any other input will result in an error"; the compatibility mapping
/// brings low to low, medium and high to high, xhigh and max to max). GLM 5.2 has no low: Z.ai maps "low/medium" to
/// high and xhigh to max (same page), and OpenRouter's public catalog (GET https://openrouter.ai/api/v1/models, same
/// date) lists only xhigh / high / none for z-ai/glm-5.2, so 5.2 gets high or xhigh. Mistral's ReasoningEffort enum
/// (https://docs.mistral.ai/openapi.yaml, same date) is none / minimal / low / medium / high / xhigh, no max: on a host
/// that does not take max, GLM 5.3 stops at high. The reasoning counts against max_tokens
/// (https://openrouter.ai/docs/guides/best-practices/reasoning-tokens.md, same date), hence the room added for it.
/// </summary>
public class GlmRequestTests
{
    private static readonly GlmModel Glm53 = GlmDialect.Recognise("glm-5.3")!;
    private static readonly GlmModel Glm52 = GlmDialect.Recognise("glm-5.2")!;
    private static readonly GlmModel Glm51 = GlmDialect.Recognise("glm-5.1")!;
    private static readonly GlmModel Glm4 = GlmDialect.Recognise("glm-4")!;

    [Theory]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "high")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.XHigh, "max")]
    [InlineData(ReasoningEffort.Max, "max")]
    public void TheEffort_IsBroughtOntoGlmsScale(ReasoningEffort effort, string expected)
    {
        GlmDialect.EffortFor(Glm53, new ReasoningOptions { Effort = effort }).Should().Be(expected);
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "high")]
    [InlineData(ReasoningEffort.Medium, "high")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.XHigh, "xhigh")]
    [InlineData(ReasoningEffort.Max, "xhigh")]
    public void Glm52_GetsOnlyTheEffortsEveryHostTakes_HighOrXHigh(ReasoningEffort effort, string expected)
    {
        GlmDialect.EffortFor(Glm52, new ReasoningOptions { Effort = effort }).Should().Be(expected);
        GlmDialect.EffortFor(GlmDialect.Recognise("z-ai/glm-5.2")!, new ReasoningOptions { Effort = effort }).Should().Be(expected);
        GlmDialect.EffortFor(GlmDialect.Recognise("zai-glm-5-2")!, new ReasoningOptions { Effort = effort }).Should().Be(expected);
    }

    [Fact]
    public void NoEffortAsked_SendsNone_AndNeverNoneOrAnotherWord()
    {
        GlmDialect.EffortFor(Glm53, null).Should().BeNull();
        GlmDialect.EffortFor(Glm53, new ReasoningOptions()).Should().BeNull();
        GlmDialect.EffortFor(Glm53, new ReasoningOptions { Effort = (ReasoningEffort)42 }).Should().BeNull();

        foreach (var effort in Enum.GetValues<ReasoningEffort>())
        {
            GlmDialect.EffortFor(Glm53, new ReasoningOptions { Effort = effort }).Should().BeOneOf("low", "high", "max");
            GlmDialect.EffortFor(Glm52, new ReasoningOptions { Effort = effort }).Should().BeOneOf("high", "xhigh");
        }

        GlmDialect.EffortFor(Glm52, new ReasoningOptions { Effort = (ReasoningEffort)42 }).Should().BeNull();
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "low", GlmDialect.LowEffortHeadroom)]
    [InlineData(ReasoningEffort.Medium, "high", GlmDialect.HighEffortHeadroom)]
    [InlineData(ReasoningEffort.High, "high", GlmDialect.HighEffortHeadroom)]
    [InlineData(ReasoningEffort.XHigh, "high", GlmDialect.HighEffortHeadroom)]
    [InlineData(ReasoningEffort.Max, "high", GlmDialect.HighEffortHeadroom)]
    public void OnAHostWithoutMax_Glm53StopsAtHigh_AndTheRoomFollows(ReasoningEffort effort, string expected, int room)
    {
        var reasoning = new ReasoningOptions { Effort = effort };

        GlmDialect.EffortFor(Glm53, reasoning, hostTakesMax: false).Should().Be(expected);
        GlmDialect.EffortFor(GlmDialect.Recognise("zai-glm-5-3")!, reasoning, hostTakesMax: false).Should().Be(expected);
        GlmDialect.EffortFor(GlmDialect.Recognise("zai-glm-latest")!, reasoning, hostTakesMax: false).Should().Be(expected);
        GlmDialect.ReasoningHeadroom(Glm53, reasoning, hostTakesMax: false).Should().Be(room);
        GlmDialect.MaxTokens(Glm53, 1_000, reasoning, 4096, hostTakesMax: false).Should().Be(1_000 + room);
    }

    [Theory]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.XHigh)]
    [InlineData(ReasoningEffort.Max)]
    public void OnAHostWithoutMax_Glm52KeepsHighOrXHigh(ReasoningEffort effort)
    {
        // GLM 5.2 is never sent max: its high / xhigh are in Mistral's enum, so a host without max changes nothing.
        var reasoning = new ReasoningOptions { Effort = effort };

        GlmDialect.EffortFor(Glm52, reasoning, hostTakesMax: false).Should().Be(GlmDialect.EffortFor(Glm52, reasoning));
        GlmDialect.ReasoningHeadroom(Glm52, reasoning, hostTakesMax: false).Should().Be(GlmDialect.ReasoningHeadroom(Glm52, reasoning));
    }

    [Fact]
    public void OnAHostWithoutMax_NoEffortAsked_SendsNone_AndKeepsTheRoomOfTheModelsDefault()
    {
        GlmDialect.EffortFor(Glm53, null, hostTakesMax: false).Should().BeNull();
        GlmDialect.EffortFor(Glm53, new ReasoningOptions(), hostTakesMax: false).Should().BeNull();
        GlmDialect.EffortFor(Glm51, new ReasoningOptions { Effort = ReasoningEffort.Max }, hostTakesMax: false).Should().BeNull();

        foreach (var effort in Enum.GetValues<ReasoningEffort>())
        {
            GlmDialect.EffortFor(Glm53, new ReasoningOptions { Effort = effort }, hostTakesMax: false).Should().BeOneOf("low", "high");
        }

        // No effort sent, the host's default runs: the room stays the one for GLM 5.3's own default, max.
        GlmDialect.ReasoningHeadroom(Glm53, null, hostTakesMax: false).Should().Be(GlmDialect.MaxEffortHeadroom);
        GlmDialect.ReasoningHeadroom(Glm53, new ReasoningOptions { BudgetTokens = 10_000 }, hostTakesMax: false).Should().Be(10_000);
    }

    [Fact]
    public void AGlmThatTakesNoEffort_IsSentNone()
    {
        GlmDialect.EffortFor(Glm51, new ReasoningOptions { Effort = ReasoningEffort.Max }).Should().BeNull();
        GlmDialect.EffortFor(Glm4, new ReasoningOptions { Effort = ReasoningEffort.Low }).Should().BeNull();
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, GlmDialect.LowEffortHeadroom)]
    [InlineData(ReasoningEffort.Medium, GlmDialect.HighEffortHeadroom)]
    [InlineData(ReasoningEffort.High, GlmDialect.HighEffortHeadroom)]
    [InlineData(ReasoningEffort.XHigh, GlmDialect.MaxEffortHeadroom)]
    [InlineData(ReasoningEffort.Max, GlmDialect.MaxEffortHeadroom)]
    public void TheRoomForTheReasoning_FollowsTheEffortSent(ReasoningEffort effort, int expected)
    {
        GlmDialect.ReasoningHeadroom(Glm53, new ReasoningOptions { Effort = effort }).Should().Be(expected);
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, GlmDialect.HighEffortHeadroom)]
    [InlineData(ReasoningEffort.Medium, GlmDialect.HighEffortHeadroom)]
    [InlineData(ReasoningEffort.High, GlmDialect.HighEffortHeadroom)]
    [InlineData(ReasoningEffort.XHigh, GlmDialect.MaxEffortHeadroom)]
    [InlineData(ReasoningEffort.Max, GlmDialect.MaxEffortHeadroom)]
    public void Glm52_TheRoomFollowsTheEffortItRunsAt_NotTheOneAsked(ReasoningEffort effort, int expected)
    {
        // Z.ai runs GLM 5.2's low at high, and xhigh at max: a low-effort room would leave a short answer at `length`.
        GlmDialect.ReasoningHeadroom(Glm52, new ReasoningOptions { Effort = effort }).Should().Be(expected);
    }

    [Fact]
    public void Unasked_TheRoomIsTheDefaultEffortsMax_BecauseAGlmReasonsAnyway()
    {
        GlmDialect.ReasoningHeadroom(Glm53, null).Should().Be(GlmDialect.MaxEffortHeadroom);
        GlmDialect.ReasoningHeadroom(Glm53, new ReasoningOptions()).Should().Be(GlmDialect.MaxEffortHeadroom);
        GlmDialect.ReasoningHeadroom(Glm51, null).Should().Be(GlmDialect.HighEffortHeadroom, "it decides alone");
        GlmDialect.ReasoningHeadroom(Glm51, new ReasoningOptions { Effort = ReasoningEffort.Low }).Should().Be(GlmDialect.HighEffortHeadroom);
        GlmDialect.ReasoningHeadroom(Glm4, null).Should().Be(0, "it does not reason");
    }

    [Fact]
    public void ABudget_IsTheRoom()
    {
        GlmDialect.ReasoningHeadroom(Glm53, new ReasoningOptions { Effort = ReasoningEffort.Low, BudgetTokens = 10_000 }).Should().Be(10_000);
        GlmDialect.ReasoningHeadroom(Glm53, new ReasoningOptions { BudgetTokens = 0 }).Should().Be(GlmDialect.MaxEffortHeadroom);
        GlmDialect.ReasoningHeadroom(Glm4, new ReasoningOptions { BudgetTokens = 10_000 }).Should().Be(0);
    }

    [Fact]
    public void MaxTokens_IsTheAnswersRoomPlusTheReasonings()
    {
        GlmDialect.MaxTokens(Glm53, null, null, 4096).Should().Be(4096 + GlmDialect.MaxEffortHeadroom);
        GlmDialect.MaxTokens(Glm53, 500, new ReasoningOptions { Effort = ReasoningEffort.Low }, 4096).Should().Be(500 + GlmDialect.LowEffortHeadroom);
        GlmDialect.MaxTokens(Glm53, 500, new ReasoningOptions { BudgetTokens = 2_000 }, 4096).Should().Be(2_500);
        GlmDialect.MaxTokens(Glm4, 500, null, 4096).Should().Be(500, "no reasoning, no room, no known cap");
        GlmDialect.MaxTokens(Glm4, int.MaxValue, null, 4096).Should().Be(int.MaxValue);
    }

    [Fact]
    public void MaxTokens_NeverGoesAboveTheModelsOutputLimit()
    {
        GlmDialect.MaxTokens(Glm53, 120_000, null, 4096).Should().Be(131_072);
        GlmDialect.MaxTokens(Glm53, 1_000_000, null, 4096).Should().Be(131_072);
        GlmDialect.MaxTokens(Glm53, int.MaxValue, new ReasoningOptions { BudgetTokens = int.MaxValue }, 4096).Should().Be(131_072);
    }

    [Fact]
    public void ANullModel_IsRefused()
    {
        var effort = () => GlmDialect.EffortFor(null!, null);
        var room = () => GlmDialect.ReasoningHeadroom(null!, null);
        var max = () => GlmDialect.MaxTokens(null!, null, null, 1);

        effort.Should().Throw<ArgumentNullException>();
        room.Should().Throw<ArgumentNullException>();
        max.Should().Throw<ArgumentNullException>();
    }
}
