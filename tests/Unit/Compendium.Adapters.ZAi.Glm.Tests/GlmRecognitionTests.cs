// -----------------------------------------------------------------------
// <copyright file="GlmRecognitionTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.ZAi.Glm.Tests;

/// <summary>
/// A GLM id is read the same whatever host names it. The ids are the hosts' own, read on 2026-10-20: Z.ai's API
/// (https://docs.z.ai/api-reference/llm/chat-completion.md), Mistral's model pages
/// (https://docs.mistral.ai/models/zai-glm-5-3.md: zai-glm-5-3, zai-glm-5, zai-glm-latest), OpenRouter's public catalog
/// (https://openrouter.ai/api/v1/models, filtered on z-ai/), Hugging Face (zai-org/GLM-5.3).
/// </summary>
public class GlmRecognitionTests
{
    [Theory]
    [InlineData("glm-5.3", 5, 3, null, false)]
    [InlineData("zai-glm-5-3", 5, 3, null, false)]
    [InlineData("ZAI-GLM-5-3", 5, 3, null, false)]
    [InlineData("zai-glm-5-2", 5, 2, null, false)]
    [InlineData("zai-glm-5", 5, 3, null, false)] // Mistral's alias of its newest GLM
    [InlineData("zai-glm-latest", 5, 3, null, false)]
    [InlineData("z-ai/glm-5.3", 5, 3, null, false)]
    [InlineData("~z-ai/glm-latest", 5, 3, null, false)]
    [InlineData("~z-ai/glm-flash-latest", 5, 3, "flash", true)]
    [InlineData("z-ai/glm-5.3:batch", 5, 3, null, false)]
    [InlineData("z-ai/glm-5.3-prime", 5, 3, "prime", false)]
    [InlineData("glm-5.3-flash", 5, 3, "flash", true)]
    [InlineData("glm-5.3-flashx", 5, 3, "flashx", true)]
    [InlineData("z-ai/glm-5", 5, 0, null, false)] // GLM-5 itself, not an alias, outside Mistral
    [InlineData("glm-5", 5, 0, null, false)]
    [InlineData("z-ai/glm-5-turbo", 5, 0, "turbo", false)]
    [InlineData("z-ai/glm-5v-turbo", 5, 0, "turbo", true)]
    [InlineData("glm-5.1", 5, 1, null, false)]
    [InlineData("glm-4.7", 4, 7, null, false)]
    [InlineData("z-ai/glm-4.7-flash", 4, 7, "flash", false)]
    [InlineData("glm-4.6", 4, 6, null, false)]
    [InlineData("z-ai/glm-4.6v", 4, 6, null, true)]
    [InlineData("z-ai/glm-4.5-air", 4, 5, "air", false)]
    [InlineData("zai-org/GLM-5.3", 5, 3, null, false)]
    [InlineData("openrouter/z-ai/glm-5.3", 5, 3, null, false)]
    [InlineData("  glm-5.3  ", 5, 3, null, false)]
    public void AGlmId_IsReadWhateverTheHost(string id, int major, int minor, string? variant, bool vision)
    {
        GlmDialect.TryRecognise(id, out var model).Should().BeTrue();

        model!.Major.Should().Be(major);
        model.Minor.Should().Be(minor);
        model.Variant.Should().Be(variant);
        model.Vision.Should().Be(vision);
        model.Id.Should().Be(id.Trim(), "the id stays the host's");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mistral-large-latest")]
    [InlineData("magistral-medium-latest")]
    [InlineData("gpt-5")]
    [InlineData("anthropic/claude-3.5-sonnet")]
    [InlineData("glm")]
    [InlineData("glm-")]
    [InlineData("z-ai/glm-")]
    [InlineData("glmx-1")]
    [InlineData("my-glm-proxy")]
    [InlineData("chatglm3-6b")]
    [InlineData("glm-4-9b-chat")]
    [InlineData("glm-4-32b-0414")]
    [InlineData("glm-5.3-finetune")]
    [InlineData("glm-5.3/other")]
    public void AnythingElse_IsNotGlm(string? id)
    {
        GlmDialect.TryRecognise(id, out var model).Should().BeFalse();
        model.Should().BeNull();
        GlmDialect.Recognise(id).Should().BeNull();
    }

    [Fact]
    public void Glm53_AlwaysReasons_TakesAnEffort_1MWindow_128kOut()
    {
        var model = GlmDialect.Recognise("zai-glm-5-3")!;

        model.AlwaysReasons.Should().BeTrue();
        model.ReasonsByDefault.Should().BeTrue();
        model.TakesReasoningEffort.Should().BeTrue();
        model.ContextWindow.Should().Be(1_048_576);
        model.MaxOutputTokens.Should().Be(131_072);
    }

    [Fact]
    public void Glm52_ReasonsByDefault_TakesAnEffort_ButCanBeTurnedOff()
    {
        var model = GlmDialect.Recognise("glm-5.2")!;

        model.AlwaysReasons.Should().BeFalse();
        model.ReasonsByDefault.Should().BeTrue();
        model.TakesReasoningEffort.Should().BeTrue();
        model.ContextWindow.Should().Be(1_048_576);
    }

    [Theory]
    [InlineData("glm-5.1", 204_800, 131_072)]
    [InlineData("glm-5", 204_800, 131_072)]
    [InlineData("z-ai/glm-5v-turbo", 202_752, 131_072)]
    [InlineData("glm-4.7", 204_800, 131_072)]
    [InlineData("glm-4.6", 204_800, 131_072)]
    [InlineData("z-ai/glm-4.6v", 131_072, 32_768)]
    [InlineData("z-ai/glm-4.5", 131_072, 98_304)]
    [InlineData("z-ai/glm-4.5v", 65_536, 16_384)]
    public void TheEarlierReasoners_DecideAlone_WithTheirOwnLimits(string id, int context, int output)
    {
        var model = GlmDialect.Recognise(id)!;

        model.AlwaysReasons.Should().BeFalse();
        model.ReasonsByDefault.Should().BeTrue();
        model.TakesReasoningEffort.Should().BeFalse();
        model.ContextWindow.Should().Be(context);
        model.MaxOutputTokens.Should().Be(output);
    }

    [Theory]
    [InlineData("glm-4")]
    [InlineData("glm-4.1v")]
    [InlineData("glm-3")]
    public void TheOlderGlms_DoNotReason_AndHaveNoKnownLimits(string id)
    {
        var model = GlmDialect.Recognise(id)!;

        model.ReasonsByDefault.Should().BeFalse();
        model.AlwaysReasons.Should().BeFalse();
        model.TakesReasoningEffort.Should().BeFalse();
        model.ContextWindow.Should().BeNull();
        model.MaxOutputTokens.Should().BeNull();
    }

    [Fact]
    public void ALaterVersion_TakesTheNewestKnownBehaviour()
    {
        var model = GlmDialect.Recognise("glm-6")!;

        model.AlwaysReasons.Should().BeTrue();
        model.TakesReasoningEffort.Should().BeTrue();
        model.IsAtLeast(5, 3).Should().BeTrue();
        model.IsAtLeast(6, 1).Should().BeFalse();
    }
}
