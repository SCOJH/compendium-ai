// -----------------------------------------------------------------------
// <copyright file="ZAiToolCallingExtensionsTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.ZAi.Tests.TestSupport;
using Compendium.Adapters.ZAi.Tools;

namespace Compendium.Adapters.ZAi.Tests.Tools;

public class ZAiToolCallingExtensionsTests
{
    [Fact]
    public void WithTools_AddsTheToolsAndTheChoice_AndKeepsTheOtherParameters()
    {
        var tools = new List<AgentTool> { new("search", "Searches.") };
        var request = TestFactories.Request() with { AdditionalParameters = new Dictionary<string, object> { ["x"] = 1 } };

        var withTools = request.WithTools(tools, "auto");
        var withoutChoice = TestFactories.Request().WithTools(tools);

        withTools.AdditionalParameters![ZAiToolCallingExtensions.ToolsKey].Should().BeSameAs(tools);
        withTools.AdditionalParameters[ZAiToolCallingExtensions.ToolChoiceKey].Should().Be("auto");
        withTools.AdditionalParameters["x"].Should().Be(1);
        withoutChoice.AdditionalParameters.Should().NotContainKey(ZAiToolCallingExtensions.ToolChoiceKey);
        request.AdditionalParameters.Should().NotContainKey(ZAiToolCallingExtensions.ToolsKey, "the request is copied, not changed");
    }

    [Fact]
    public void GetToolCalls_IsEmpty_WhenTheModelCalledNone()
    {
        var response = new CompletionResponse
        {
            Id = "r",
            Model = "glm-5.3",
            Content = "ok",
            FinishReason = FinishReason.Stop,
            Usage = new UsageStats { PromptTokens = 0, CompletionTokens = 0 },
        };

        response.GetToolCalls().Should().BeEmpty();
        (response with { Metadata = new Dictionary<string, object> { [ZAiToolCallingExtensions.ToolCallsMetadataKey] = "no" } })
            .GetToolCalls().Should().BeEmpty();
    }

    [Fact]
    public void NullArguments_AreRefused()
    {
        var noRequest = () => ((CompletionRequest)null!).WithTools(new List<AgentTool>());
        var noTools = () => TestFactories.Request().WithTools(null!);
        var noResponse = () => ((CompletionResponse)null!).GetToolCalls();

        noRequest.Should().Throw<ArgumentNullException>();
        noTools.Should().Throw<ArgumentNullException>();
        noResponse.Should().Throw<ArgumentNullException>();
    }
}
