// -----------------------------------------------------------------------
// <copyright file="ZAiRequestTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.ZAi.Tests.TestSupport;
using Compendium.Adapters.ZAi.Tools;

namespace Compendium.Adapters.ZAi.Tests.Services;

/// <summary>
/// What goes on the wire to Z.ai (https://docs.z.ai/api-reference/llm/chat-completion.md and
/// https://docs.z.ai/guides/capabilities/thinking.md, read 2026-10-20): <c>thinking: {type: "enabled"}</c> when reasoning
/// is asked for and never <c>disabled</c> (an error on GLM 5.3), <c>reasoning_effort</c> on GLM's low / high / max scale,
/// <c>max_tokens</c> with room for the reasoning (at most 131 072), only <c>tool_choice: "auto"</c>.
/// </summary>
public class ZAiRequestTests
{
    private const string Reply = """{"id":"r","created":1790000000,"model":"glm-5.3","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}]}""";

    private static async Task<JsonElement> SendAsync(CompletionRequest request)
    {
        var (sut, _, body) = TestFactories.Replying("application/json", Reply);
        var result = await sut.CompleteAsync(request, CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        return JsonDocument.Parse(body()!).RootElement.Clone();
    }

    [Fact]
    public async Task Unasked_TheRequestHasNoThinkingAndNoEffort_ButRoomForTheReasoning()
    {
        var (sut, _, body) = TestFactories.Replying("application/json", Reply);

        await sut.CompleteAsync(TestFactories.Request(), CancellationToken.None);

        body().Should().Be("""{"model":"glm-5.3","messages":[{"role":"user","content":"Hello"}],"temperature":0.7,"max_tokens":36864,"stream":false}""");
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "low", 4096 + 4096)]
    [InlineData(ReasoningEffort.Medium, "high", 4096 + 16384)]
    [InlineData(ReasoningEffort.High, "high", 4096 + 16384)]
    [InlineData(ReasoningEffort.XHigh, "max", 4096 + 32768)]
    [InlineData(ReasoningEffort.Max, "max", 4096 + 32768)]
    public async Task Asked_ThinkingIsEnabled_AndTheEffortIsOnGlmsScale(ReasoningEffort effort, string wire, int maxTokens)
    {
        var sent = await SendAsync(TestFactories.Request() with { Reasoning = new ReasoningOptions { Effort = effort } });

        sent.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
        sent.GetProperty("reasoning_effort").GetString().Should().Be(wire);
        sent.GetProperty("max_tokens").GetInt32().Should().Be(maxTokens);
    }

    [Fact]
    public async Task AskedWithoutAnEffort_ThinkingIsEnabled_TheEffortIsZaisDefault()
    {
        var sent = await SendAsync(TestFactories.Request() with { Reasoning = new ReasoningOptions { IncludeReasoning = false } });

        sent.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
        sent.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ThinkingIsNeverDisabled()
    {
        foreach (var reasoning in new ReasoningOptions?[] { null, new(), new() { IncludeReasoning = false }, new() { Effort = ReasoningEffort.Low } })
        {
            var sent = await SendAsync(TestFactories.Request() with { Reasoning = reasoning });
            sent.GetRawText().Should().NotContain("disabled");
        }
    }

    [Fact]
    public async Task AGlmThatTakesNoEffort_GetsThinkingButNoEffort()
    {
        var sent = await SendAsync(TestFactories.Request("glm-4.6") with { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Max } });

        sent.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
        sent.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        sent.GetProperty("max_tokens").GetInt32().Should().Be(4096 + 16384);
    }

    [Fact]
    public async Task AModelTheDialectDoesNotKnow_IsSentPlain()
    {
        var sent = await SendAsync(TestFactories.Request("charglm-4") with { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Max } });

        sent.TryGetProperty("thinking", out _).Should().BeFalse();
        sent.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        sent.GetProperty("max_tokens").GetInt32().Should().Be(4096);
    }

    [Fact]
    public async Task MaxTokens_StaysUnderTheOutputLimit()
    {
        var sent = await SendAsync(TestFactories.Request() with { MaxTokens = 200_000 });

        sent.GetProperty("max_tokens").GetInt32().Should().Be(131_072);
    }

    [Fact]
    public async Task TheRestOfTheRequest_IsCarried()
    {
        var sent = await SendAsync(new CompletionRequest
        {
            Model = "glm-5.3",
            SystemPrompt = "Be brief.",
            Messages = new List<Message> { Message.User("Hi"), Message.Assistant("Yes?"), new() { Role = MessageRole.User, Content = "Go", Name = "ignored" } },
            Temperature = 1.0f,
            TopP = 0.95f,
            MaxTokens = 100,
            FrequencyPenalty = 0.5f,
            PresencePenalty = 0.5f,
            StopSequences = new List<string> { "</tool_call>" },
            RequestId = "request-123",
            UserId = "user-456",
        });

        sent.GetRawText().Should().Be(
            """{"model":"glm-5.3","messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"Hi"},{"role":"assistant","content":"Yes?"},{"role":"user","content":"Go"}],"temperature":1,"top_p":0.95,"max_tokens":32868,"stop":["\u003C/tool_call\u003E"],"stream":false,"request_id":"request-123","user_id":"user-456"}""",
            "no penalties and no name: Z.ai's schema has neither");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]
    public async Task ARequestIdZaiWouldRefuse_IsLeftOut(string requestId)
    {
        var sent = await SendAsync(TestFactories.Request() with { RequestId = requestId, UserId = "u" });

        sent.TryGetProperty("request_id", out _).Should().BeFalse();
        sent.TryGetProperty("user_id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task NoModel_TakesTheDefault()
    {
        var sent = await SendAsync(TestFactories.Request(string.Empty));

        sent.GetProperty("model").GetString().Should().Be(ZAiOptions.DefaultChatModel);
    }

    [Fact]
    public async Task Tools_AreSent_WithAuto()
    {
        var request = TestFactories.Request().WithTools(
            new List<AgentTool> { new("search", "Searches.", """{"type":"object","properties":{"q":{"type":"string"}}}"""), new("noop", "No schema.", "not json") },
            "auto");

        var sent = await SendAsync(request);

        var tools = sent.GetProperty("tools");
        tools.GetArrayLength().Should().Be(2);
        tools[0].GetProperty("type").GetString().Should().Be("function");
        tools[0].GetProperty("function").GetProperty("name").GetString().Should().Be("search");
        tools[0].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString().Should().Be("object");
        tools[1].GetProperty("function").TryGetProperty("parameters", out _).Should().BeFalse();
        sent.GetProperty("tool_choice").GetString().Should().Be("auto");
    }

    [Fact]
    public async Task NoToolsAndNoChoice_NothingIsSent()
    {
        var request = TestFactories.Request() with { AdditionalParameters = new Dictionary<string, object> { ["other"] = 1 } };

        var sent = await SendAsync(request.WithTools(new List<AgentTool>()));

        sent.TryGetProperty("tools", out _).Should().BeFalse();
        sent.TryGetProperty("tool_choice", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("required")]
    [InlineData("none")]
    [InlineData("search")]
    public async Task AToolChoiceZaiDoesNotHave_IsRefused_BeforeAnyCall(string choice)
    {
        var (sut, _, body) = TestFactories.Replying("application/json", Reply);
        var request = TestFactories.Request().WithTools(new List<AgentTool> { new("search", "Searches.") }, choice);

        var result = await sut.CompleteAsync(request, CancellationToken.None);
        var streamed = new List<Result<CompletionChunk>>();
        await foreach (var r in sut.StreamCompleteAsync(request, CancellationToken.None))
        {
            streamed.Add(r);
        }

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
        streamed.Should().ContainSingle().Which.Error.Code.Should().Be("AI.InvalidRequest");
        body().Should().BeNull("nothing was sent");
    }
}
