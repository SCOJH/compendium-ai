// -----------------------------------------------------------------------
// <copyright file="GlmResponseTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.ZAi.Glm.Tests;

/// <summary>
/// What comes back from a GLM, normalised the same on every host: the answer alone in ContentDelta / Content, the
/// reasoning apart and only when asked for, GLM's finish reasons (Z.ai's sensitive, model_context_window_exceeded and
/// network_error: https://docs.z.ai/api-reference/llm/chat-completion.md, read 2026-10-20).
/// </summary>
public class GlmResponseTests
{
    private static readonly ReasoningOptions Asked = new();
    private static readonly ReasoningOptions AskedWithoutText = new() { IncludeReasoning = false };

    [Theory]
    [InlineData("stop", FinishReason.Stop)]
    [InlineData("STOP", FinishReason.Stop)]
    [InlineData("length", FinishReason.Length)]
    [InlineData("model_length", FinishReason.Length)]
    [InlineData("model_context_window_exceeded", FinishReason.Length)]
    [InlineData("content_filter", FinishReason.ContentFilter)]
    [InlineData("sensitive", FinishReason.ContentFilter)]
    [InlineData("tool_calls", FinishReason.ToolCall)]
    [InlineData("function_call", FinishReason.ToolCall)]
    [InlineData("network_error", FinishReason.Other)]
    [InlineData("error", FinishReason.Other)]
    [InlineData(null, FinishReason.InProgress)]
    public void TheFinishReasons(string? reason, FinishReason expected)
    {
        GlmDialect.MapFinishReason(reason).Should().Be(expected);
    }

    [Fact]
    public void TheReasoning_IsReturnedOnlyWhenAskedFor()
    {
        GlmDialect.Surface("Weighing it.", Asked).Should().Be("Weighing it.");
        GlmDialect.Surface("Weighing it.", AskedWithoutText).Should().BeNull();
        GlmDialect.Surface("Weighing it.", null).Should().BeNull();
        GlmDialect.Surface(string.Empty, Asked).Should().BeNull();
        GlmDialect.Surface(null, Asked).Should().BeNull();
    }

    [Fact]
    public void AChunk_CarriesTheAnswerAlone_AndTheReasoningApart()
    {
        var chunk = GlmDialect.Chunk("g", 3, "<tool_call>", "Need a tool.", null, null, Asked)!;

        chunk.ContentDelta.Should().Be("<tool_call>");
        chunk.ReasoningDelta.Should().Be("Need a tool.");
        chunk.Index.Should().Be(3);
        chunk.IsFinal.Should().BeFalse();
        chunk.FinishReason.Should().BeNull();
        chunk.Usage.Should().BeNull();
    }

    [Fact]
    public void AReasoningOnlyDelta_NotAskedFor_IsSkipped_AndNeverBecomesAnswer()
    {
        GlmDialect.Chunk("g", 0, null, "Need a tool.", null, null, null).Should().BeNull();
        GlmDialect.Chunk("g", 0, string.Empty, "Need a tool.", null, null, AskedWithoutText).Should().BeNull();
        GlmDialect.Chunk("g", 0, "Hi", "Need a tool.", null, null, null)!.ContentDelta.Should().Be("Hi");
        GlmDialect.Chunk("g", 0, "Hi", "Need a tool.", null, null, null)!.ReasoningDelta.Should().BeNull();
    }

    [Fact]
    public void AnEmptyDelta_IsSkipped_ButTheLastChunkAndTheUsageNever()
    {
        GlmDialect.Chunk("g", 0, string.Empty, null, null, null, Asked).Should().BeNull();

        var last = GlmDialect.Chunk("g", 5, string.Empty, null, "stop", null, Asked)!;
        last.IsFinal.Should().BeTrue();
        last.FinishReason.Should().Be(FinishReason.Stop);
        last.ContentDelta.Should().BeEmpty();

        var usage = new UsageStats { PromptTokens = 8, CompletionTokens = 262 };
        var accounting = GlmDialect.Chunk("g", 6, null, null, null, usage, null)!;
        accounting.Usage.Should().BeSameAs(usage);
        accounting.IsFinal.Should().BeFalse();
    }

    [Fact]
    public void Describe_FillsWhatTheHostLeftUnset_AndKeepsWhatItSaid()
    {
        var listed = new AIModel
        {
            Id = "zai-glm-5-3",
            Name = "zai-glm-5-3",
            Provider = "mistral",
            MaxOutputTokens = 100_000,
            Metadata = new Dictionary<string, object> { ["specialty"] = "code" },
        };

        var described = GlmDialect.Describe(listed, GlmDialect.Recognise(listed.Id)!);

        described.ContextWindow.Should().Be(1_048_576);
        described.MaxOutputTokens.Should().Be(100_000, "the host's own value wins");
        described.Metadata.Should().Contain(GlmDialect.ReasoningMetadataKey, "always").And.Contain("specialty", "code");
        described.Id.Should().Be("zai-glm-5-3");
        listed.Metadata.Should().NotContainKey(GlmDialect.ReasoningMetadataKey, "the host's description is not changed in place");
    }

    [Fact]
    public void Describe_MarksVisionAndDefaultReasoning_AndNothingForAnOldGlm()
    {
        var flash = GlmDialect.Describe(new AIModel { Id = "glm-5.3-flash", Name = "f", Provider = "zai" }, GlmDialect.Recognise("glm-5.3-flash")!);
        flash.SupportsVision.Should().BeTrue();

        var older = GlmDialect.Describe(new AIModel { Id = "glm-5.1", Name = "o", Provider = "zai" }, GlmDialect.Recognise("glm-5.1")!);
        older.Metadata.Should().Contain(GlmDialect.ReasoningMetadataKey, "by-default");

        var old = GlmDialect.Describe(new AIModel { Id = "glm-4", Name = "o", Provider = "zai" }, GlmDialect.Recognise("glm-4")!);
        old.Metadata.Should().BeNull();
        old.ContextWindow.Should().BeNull();
    }

    [Fact]
    public void Describe_RefusesNulls()
    {
        var noListed = () => GlmDialect.Describe(null!, GlmDialect.Recognise("glm-5.3")!);
        var noModel = () => GlmDialect.Describe(new AIModel { Id = "x", Name = "x", Provider = "x" }, null!);

        noListed.Should().Throw<ArgumentNullException>();
        noModel.Should().Throw<ArgumentNullException>();
    }
}
