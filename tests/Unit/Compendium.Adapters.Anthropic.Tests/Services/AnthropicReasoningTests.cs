// -----------------------------------------------------------------------
// <copyright file="AnthropicReasoningTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Anthropic.Tests.TestSupport;

namespace Compendium.Adapters.Anthropic.Tests.Services;

/// <summary>
/// Extended thinking (<see cref="ReasoningOptions"/>) as each Claude model takes it: adaptive with an effort from Opus and
/// Sonnet 4.6 on (no budget, which Opus 4.7, Sonnet 5 and Fable refuse), a budget before them, nothing on the models
/// without thinking. The thinking counts against max_tokens, so the answer keeps its room, and takes no sampling
/// parameter. Its text comes back apart from the answer.
/// </summary>
public class AnthropicReasoningTests
{
    private static async Task<JsonElement> SentBodyAsync(CompletionRequest request)
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","content":[]}""");
        await sut.CompleteAsync(request, CancellationToken.None);
        return JsonDocument.Parse(body!).RootElement.Clone();
    }

    private static CompletionRequest Request(string model, ReasoningOptions? reasoning) =>
        TestFactories.SimpleCompletionRequest(model) with { MaxTokens = 4096, Temperature = 0.2f, TopP = 0.9f, Reasoning = reasoning };

    [Theory]
    [InlineData("claude-opus-5", ReasoningEffort.XHigh, "xhigh")]
    [InlineData("claude-opus-5-5", ReasoningEffort.Max, "max")]
    [InlineData("claude-sonnet-5", ReasoningEffort.Low, "low")]
    [InlineData("claude-opus-4-7", ReasoningEffort.XHigh, "xhigh")]
    [InlineData("claude-fable-5-1", ReasoningEffort.Medium, "medium")]
    [InlineData("claude-sonnet-4-6", ReasoningEffort.XHigh, "high")]
    [InlineData("claude-opus-4-6", ReasoningEffort.High, "high")]
    public async Task AnAdaptiveModel_GetsAdaptiveThinking_AndItsEffort_NeverABudget_NorASamplingParameter(string model, ReasoningEffort effort, string wire)
    {
        var sent = await SentBodyAsync(Request(model, new ReasoningOptions { Effort = effort, BudgetTokens = 2000 }));

        var thinking = sent.GetProperty("thinking");
        thinking.GetProperty("type").GetString().Should().Be("adaptive");
        thinking.GetProperty("display").GetString().Should().Be("summarized", "from Opus 4.7 on the thinking text is omitted unless asked for");
        thinking.TryGetProperty("budget_tokens", out _).Should().BeFalse("a budget is a 400 on Opus 4.7, Sonnet 5 and Fable");
        sent.GetProperty("output_config").GetProperty("effort").GetString().Should().Be(wire);
        sent.GetProperty("max_tokens").GetInt32().Should().Be(4096 + 2000, "the thinking counts against max_tokens: the budget is headroom, the answer keeps its 4096");
        sent.TryGetProperty("temperature", out _).Should().BeFalse();
        sent.TryGetProperty("top_p", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("claude-sonnet-4-5", ReasoningEffort.High, null, 16_000)]
    [InlineData("claude-haiku-4-5-20251001", ReasoningEffort.Low, null, 2_048)]
    [InlineData("claude-opus-4-1-20250805", null, null, 8_192)]
    [InlineData("claude-sonnet-4-20250514", null, 100, ClaudeReasoning.MinBudgetTokens)]
    [InlineData("claude-3-7-sonnet-latest", ReasoningEffort.Max, 5_000, 5_000)]
    public async Task ABudgetedModel_GetsABudget_AboveTheMinimum_AndMaxTokensRaisedByIt(string model, ReasoningEffort? effort, int? budget, int expected)
    {
        var sent = await SentBodyAsync(Request(model, new ReasoningOptions { Effort = effort, BudgetTokens = budget }));

        var thinking = sent.GetProperty("thinking");
        thinking.GetProperty("type").GetString().Should().Be("enabled");
        thinking.GetProperty("budget_tokens").GetInt32().Should().Be(expected);
        sent.GetProperty("max_tokens").GetInt32().Should().Be(4096 + expected, "budget_tokens must stay below max_tokens, and the answer keeps its 4096");
        sent.TryGetProperty("output_config", out _).Should().BeFalse("Sonnet and Haiku 4.5 refuse an effort");
        sent.TryGetProperty("temperature", out _).Should().BeFalse("thinking takes no temperature");
        sent.TryGetProperty("top_p", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AModelWithoutThinking_GetsTheRequestAsItWas()
    {
        var sent = await SentBodyAsync(Request("claude-3-5-haiku-latest", new ReasoningOptions { Effort = ReasoningEffort.High }));

        sent.TryGetProperty("thinking", out _).Should().BeFalse();
        sent.TryGetProperty("output_config", out _).Should().BeFalse();
        sent.GetProperty("max_tokens").GetInt32().Should().Be(4096);
        sent.GetProperty("temperature").GetSingle().Should().Be(0.2f);
    }

    [Fact]
    public async Task WithoutReasoning_TheRequestIsTheOneItWas()
    {
        var sent = await SentBodyAsync(Request("claude-sonnet-4-6", null));

        sent.TryGetProperty("thinking", out _).Should().BeFalse();
        sent.TryGetProperty("output_config", out _).Should().BeFalse();
        sent.GetProperty("max_tokens").GetInt32().Should().Be(4096);
        sent.GetProperty("temperature").GetSingle().Should().Be(0.2f);
    }

    [Fact]
    public async Task ThinkingWithoutItsText_AsksForItOmitted_AndNoEffort_LeavesTheModelsOwn()
    {
        var sent = await SentBodyAsync(Request("claude-opus-5", new ReasoningOptions { IncludeReasoning = false }));

        sent.GetProperty("thinking").GetProperty("display").GetString().Should().Be("omitted");
        sent.TryGetProperty("output_config", out _).Should().BeFalse();
        sent.GetProperty("max_tokens").GetInt32().Should().Be(4096);
    }

    [Fact]
    public async Task AStream_ReturnsTheThinkingApartFromTheAnswer()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var stream = string.Join("\n",
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-opus-5\",\"usage\":{\"input_tokens\":7,\"output_tokens\":0}}}",
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"Let me \"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"think.\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Answer.\"}}",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":40}}",
            "data: {\"type\":\"message_stop\"}",
            "");
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("text/event-stream", stream);

        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(Request("claude-opus-5", new ReasoningOptions()), CancellationToken.None))
        {
            r.IsSuccess.Should().BeTrue();
            chunks.Add(r.Value);
        }

        chunks.Select(c => c.ReasoningDelta).Where(d => d is not null).Should().Equal("Let me ", "think.");
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be("Answer.", "the thinking is never part of the answer");
        chunks.Where(c => c.ReasoningDelta is not null).Should().OnlyContain(c => c.ContentDelta.Length == 0);
        chunks[^1].IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task AModelThatThinksUnasked_HasItsThinkingLeftOutOfTheStream()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var stream = string.Join("\n",
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-opus-5\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"Unasked.\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Answer.\"}}",
            "data: {\"type\":\"message_stop\"}",
            "");
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("text/event-stream", stream);

        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(Request("claude-opus-5", null), CancellationToken.None))
        {
            chunks.Add(r.Value);
        }

        chunks.Should().OnlyContain(c => c.ReasoningDelta == null);
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be("Answer.");
    }

    [Fact]
    public async Task AResponse_ReturnsTheThinkingApartFromTheAnswer()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("application/json", """
            {"id":"m","model":"claude-sonnet-4-5","stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":9},
             "content":[{"type":"thinking","thinking":"Weighing it.","signature":"s"},{"type":"text","text":"Answer."}]}
            """);

        var result = await sut.CompleteAsync(Request("claude-sonnet-4-5", new ReasoningOptions()), CancellationToken.None);

        result.Value.Content.Should().Be("Answer.");
        result.Value.Reasoning.Should().Be("Weighing it.");
    }

    [Fact]
    public async Task AResponseOfOnlyThinking_HasNoAnswerText()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("application/json", """
            {"id":"m","model":"claude-opus-5","stop_reason":"max_tokens","content":[{"type":"thinking","thinking":"Still thinking"}]}
            """);

        var result = await sut.CompleteAsync(Request("claude-opus-5", new ReasoningOptions()), CancellationToken.None);

        result.Value.Content.Should().BeEmpty();
        result.Value.Reasoning.Should().Be("Still thinking");
        result.Value.FinishReason.Should().Be(FinishReason.Length);
    }

    [Theory]
    [InlineData("claude-opus-5", "Adaptive", true)]
    [InlineData("claude-mythos-5-1", "Adaptive", true)]
    [InlineData("anthropic.claude-opus-4-8", "Adaptive", true)]
    [InlineData("claude-sonnet-4-6", "Adaptive", false)]
    [InlineData("claude-opus-4-5-20251101", "Budget", false)]
    [InlineData("claude-haiku-4-5", "Budget", false)]
    [InlineData("claude-sonnet-4-20250514", "Budget", false)]
    [InlineData("claude-3-7-sonnet-latest", "Budget", false)]
    [InlineData("claude-3-5-sonnet-latest", "None", false)]
    [InlineData("gpt-4o", "None", false)]
    [InlineData("", "None", false)]
    public void TheModelsThinking_IsReadFromItsId(string model, string kind, bool xhigh)
    {
        var (actual, hasXHigh) = ClaudeReasoning.Of(model);
        actual.ToString().Should().Be(kind);
        hasXHigh.Should().Be(xhigh);
    }
}
