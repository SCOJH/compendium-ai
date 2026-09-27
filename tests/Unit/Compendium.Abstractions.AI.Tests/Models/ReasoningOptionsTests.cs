using Compendium.Abstractions.AI.Models;
using AwesomeAssertions;

namespace Compendium.Abstractions.AI.Tests.Models;

/// <summary>The reasoning channel is opt-in: a request, a chunk and a response without it are the ones they were.</summary>
public class ReasoningOptionsTests
{
    [Fact]
    public void ARequest_HasNoReasoning_UntilOneIsAskedFor()
    {
        new CompletionRequest { Model = "m", Messages = [Message.User("hi")] }.Reasoning.Should().BeNull();
        new CompletionChunk { Id = "c", ContentDelta = "x" }.ReasoningDelta.Should().BeNull();
        new UsageStats { PromptTokens = 1, CompletionTokens = 2 }.ReasoningTokens.Should().BeNull();
    }

    [Fact]
    public void AskedFor_TheReasoningIsReturned_ByDefault_AtTheProvidersEffort()
    {
        var options = new ReasoningOptions();

        options.IncludeReasoning.Should().BeTrue();
        options.Effort.Should().BeNull();
        options.BudgetTokens.Should().BeNull();
    }

    [Fact]
    public void TheEfforts_AreOrderedFromTheLeast()
    {
        Enum.GetValues<ReasoningEffort>().Should().Equal(ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.XHigh, ReasoningEffort.Max);
    }
}
