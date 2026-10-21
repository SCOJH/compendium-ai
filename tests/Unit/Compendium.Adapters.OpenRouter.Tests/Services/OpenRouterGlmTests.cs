// -----------------------------------------------------------------------
// <copyright file="OpenRouterGlmTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.OpenRouter.Tests.TestSupport;

namespace Compendium.Adapters.OpenRouter.Tests.Services;

/// <summary>
/// GLM through OpenRouter (<c>z-ai/glm-5.3</c>), through the GLM dialect. Written from OpenRouter's documentation (no
/// recorded call), read 2026-10-20: https://openrouter.ai/docs/guides/best-practices/reasoning-tokens.md (the
/// <c>reasoning</c> request object, <c>message.reasoning</c>, <c>delta.reasoning</c> / <c>reasoning_details</c>, the
/// reasoning counted against <c>max_tokens</c>), https://openrouter.ai/docs/api_reference/streaming.md (the
/// <c>: OPENROUTER PROCESSING</c> comments, and the usage frame after the last content chunk that repeats its finish
/// reason), the public catalog https://openrouter.ai/api/v1/models (GLM 5.3: reasoning mandatory, efforts max / high / low;
/// GLM 5.2: efforts xhigh / high / none).
/// The <c>format</c> of GLM's <c>reasoning_details</c> is not documented; only their <c>reasoning.text</c> entries are read.
/// </summary>
public class OpenRouterGlmTests
{
    // The same logical answer the Z.ai and Mistral GLM tests read: the same chunks must come out.
    private const string Reasoning = "Need the search tool. Calling it.";
    private const string Answer = "<tool_call>{\"name\": \"search\", \"arguments\": {}}";

    private const string Stream =
        ": OPENROUTER PROCESSING\n\n"
        + "data: {\"id\":\"gen-1\",\"object\":\"chat.completion.chunk\",\"created\":1790000000,\"model\":\"z-ai/glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning\":\"Need the search tool.\",\"reasoning_details\":[{\"type\":\"reasoning.text\",\"text\":\"Need the search tool.\",\"format\":\"unknown\",\"index\":0}]},\"finish_reason\":null}]}\n\n"
        + ": OPENROUTER PROCESSING\n\n"
        + "data: {\"id\":\"gen-1\",\"object\":\"chat.completion.chunk\",\"created\":1790000000,\"model\":\"z-ai/glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"<tool_call>{\\\"name\\\": \\\"sea\",\"reasoning\":\" Calling it.\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"id\":\"gen-1\",\"object\":\"chat.completion.chunk\",\"created\":1790000000,\"model\":\"z-ai/glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"rch\\\", \\\"arguments\\\": {}}\"},\"finish_reason\":\"stop\",\"native_finish_reason\":\"stop\"}]}\n\n"
        + "data: {\"id\":\"gen-1\",\"object\":\"chat.completion.chunk\",\"created\":1790000000,\"model\":\"z-ai/glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\",\"role\":\"assistant\"},\"finish_reason\":\"stop\",\"native_finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":30,\"total_tokens\":80,\"completion_tokens_details\":{\"reasoning_tokens\":12}}}\n\n"
        + "data: [DONE]\n\n";

    private static async Task<(List<CompletionChunk> Chunks, JsonElement Sent)> StreamAsync(
        string sse, ReasoningOptions? reasoning, string model = "z-ai/glm-5.3", int? maxTokens = null)
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("text/event-stream", sse);
        var request = TestFactories.SimpleCompletionRequest(model) with
        {
            Reasoning = reasoning,
            MaxTokens = maxTokens,
            StopSequences = new List<string> { "</tool_call>" },
        };

        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(request, CancellationToken.None))
        {
            r.IsSuccess.Should().BeTrue(r.IsFailure ? r.Error.Message : null);
            chunks.Add(r.Value);
        }

        return (chunks, JsonDocument.Parse(body!).RootElement.Clone());
    }

    [Fact]
    public async Task AStream_Asked_ReadsTheReasoningApart_AndTheUsageFrame()
    {
        var (chunks, _) = await StreamAsync(Stream, new ReasoningOptions());

        string.Concat(chunks.Select(c => c.ReasoningDelta)).Should().Be(Reasoning, "reasoning, not reasoning_details as well: the same text twice");
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be(Answer, "the reasoning is never part of the answer");
        chunks.Should().HaveCount(4);
        chunks.Select(c => c.Index).Should().Equal(0, 1, 2, 3);
        chunks.Take(3).Should().OnlyContain(c => !c.IsFinal, "the finish reason of the last content chunk waits for the usage frame");
        chunks[^1].IsFinal.Should().BeTrue();
        chunks[^1].FinishReason.Should().Be(FinishReason.Stop);
        chunks[^1].Usage!.PromptTokens.Should().Be(50);
        chunks[^1].Usage!.CompletionTokens.Should().Be(30);
        chunks[^1].Usage!.ReasoningTokens.Should().Be(12);
    }

    [Fact]
    public async Task AStream_NotAsked_LeavesTheReasoningOut()
    {
        var (chunks, sent) = await StreamAsync(Stream, null);

        chunks.Should().OnlyContain(c => c.ReasoningDelta == null);
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be(Answer);
        chunks.Should().HaveCount(3, "the reasoning-only delta has nothing to pass on");
        chunks[^1].Usage!.ReasoningTokens.Should().Be(12);
        sent.TryGetProperty("reasoning", out _).Should().BeFalse("nothing to say: GLM 5.3 reasons anyway");
        sent.GetProperty("max_tokens").GetInt32().Should().Be(4096 + 32_768);
        sent.GetProperty("stop")[0].GetString().Should().Be("</tool_call>");
    }

    [Fact]
    public async Task AStream_WithoutAUsageFrame_StillEndsWithAFinalChunk()
    {
        var sse =
            "data: {\"id\":\"gen-2\",\"model\":\"z-ai/glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Done.\"},\"finish_reason\":\"length\"}]}\n\n"
            + "data: [DONE]\n\n";

        var (chunks, _) = await StreamAsync(sse, null);

        chunks.Should().HaveCount(2);
        chunks[0].ContentDelta.Should().Be("Done.");
        chunks[0].IsFinal.Should().BeFalse();
        chunks[1].IsFinal.Should().BeTrue();
        chunks[1].FinishReason.Should().Be(FinishReason.Length);
        chunks[1].Usage.Should().BeNull();
    }

    [Fact]
    public async Task AStream_WhereTheUsageComesWithTheFinishReason_EndsThere()
    {
        var sse =
            "data: {\"id\":\"gen-3\",\"model\":\"z-ai/glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\",\"reasoning_details\":[{\"type\":\"reasoning.text\",\"text\":\"Only in details.\"},{\"type\":\"reasoning.encrypted\",\"data\":\"x\"}]},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}}\n\n"
            + "data: {\"id\":\"gen-3\",\"model\":\"z-ai/glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"never read\"},\"finish_reason\":null}]}\n\n";

        var (chunks, _) = await StreamAsync(sse, new ReasoningOptions());

        chunks.Should().ContainSingle();
        chunks[0].ContentDelta.Should().Be("Hi");
        chunks[0].ReasoningDelta.Should().Be("Only in details.");
        chunks[0].IsFinal.Should().BeTrue();
        chunks[0].Usage!.ReasoningTokens.Should().BeNull();
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "low", 4_096)]
    [InlineData(ReasoningEffort.Medium, "high", 16_384)]
    [InlineData(ReasoningEffort.High, "high", 16_384)]
    [InlineData(ReasoningEffort.XHigh, "max", 32_768)]
    [InlineData(ReasoningEffort.Max, "max", 32_768)]
    public async Task TheRequest_Asked_SendsTheEffortOnGlmsScale(ReasoningEffort effort, string wire, int room)
    {
        var (_, sent) = await StreamAsync(Stream, new ReasoningOptions { Effort = effort }, maxTokens: 1_000);

        var reasoning = sent.GetProperty("reasoning");
        reasoning.GetProperty("effort").GetString().Should().Be(wire);
        reasoning.TryGetProperty("exclude", out _).Should().BeFalse();
        reasoning.TryGetProperty("max_tokens", out _).Should().BeFalse("OpenRouter turns a budget into an effort for GLM");
        reasoning.TryGetProperty("enabled", out _).Should().BeFalse("GLM 5.3's reasoning is mandatory");
        sent.GetProperty("max_tokens").GetInt32().Should().Be(1_000 + room);
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "high", 16_384)]
    [InlineData(ReasoningEffort.High, "high", 16_384)]
    [InlineData(ReasoningEffort.XHigh, "xhigh", 32_768)]
    [InlineData(ReasoningEffort.Max, "xhigh", 32_768)]
    public async Task TheRequest_ToGlm52_SendsOnlyAnEffortTheCatalogLists(ReasoningEffort effort, string wire, int room)
    {
        var (_, sent) = await StreamAsync(Stream, new ReasoningOptions { Effort = effort }, model: "z-ai/glm-5.2", maxTokens: 1_000);

        sent.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be(wire).And.BeOneOf("xhigh", "high");
        sent.GetProperty("max_tokens").GetInt32().Should().Be(1_000 + room);
    }

    [Fact]
    public async Task TheRequest_AskedWithoutTheText_Excludes()
    {
        var (chunks, sent) = await StreamAsync(Stream, new ReasoningOptions { IncludeReasoning = false }, model: "~z-ai/glm-latest");

        var reasoning = sent.GetProperty("reasoning");
        reasoning.GetProperty("exclude").GetBoolean().Should().BeTrue();
        reasoning.TryGetProperty("effort", out _).Should().BeFalse();
        chunks.Should().OnlyContain(c => c.ReasoningDelta == null, "even if a provider sends it anyway");
    }

    [Fact]
    public async Task TheRequest_ToAGlmThatTakesNoEffort_SendsNoReasoningObject()
    {
        var (_, sent) = await StreamAsync(Stream, new ReasoningOptions { Effort = ReasoningEffort.Max }, model: "z-ai/glm-4.6");

        sent.TryGetProperty("reasoning", out _).Should().BeFalse();
        sent.GetProperty("max_tokens").GetInt32().Should().Be(4096 + 16_384);
    }

    [Fact]
    public async Task AResponse_ReadsTheReasoningApart_AndItsTokens()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", """
            {"id":"gen-4","model":"z-ai/glm-5.3","created":1790000000,
             "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"Answer.","reasoning":"Weighing it.",
               "reasoning_details":[{"type":"reasoning.text","text":"Weighing it.","format":"unknown","index":0}]}}],
             "usage":{"prompt_tokens":12,"completion_tokens":30,"total_tokens":42,"completion_tokens_details":{"reasoning_tokens":20}}}
            """);

        var asked = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest("z-ai/glm-5.3") with { Reasoning = new ReasoningOptions() }, CancellationToken.None);
        var unasked = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest("z-ai/glm-5.3"), CancellationToken.None);

        asked.Value.Content.Should().Be("Answer.");
        asked.Value.Reasoning.Should().Be("Weighing it.");
        asked.Value.Usage.ReasoningTokens.Should().Be(20);
        asked.Value.FinishReason.Should().Be(FinishReason.Stop);
        unasked.Value.Content.Should().Be("Answer.");
        unasked.Value.Reasoning.Should().BeNull();
    }

    [Fact]
    public async Task AResponse_WithReasoningOfAnotherShape_ReadsAsNone()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", """
            {"id":"gen-5","model":"z-ai/glm-5.3","created":1790000000,
             "choices":[{"index":0,"finish_reason":"sensitive","message":{"role":"assistant","content":"","reasoning":{"odd":true},"reasoning_details":"odd"}}]}
            """);

        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest("z-ai/glm-5.3") with { Reasoning = new ReasoningOptions() }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Reasoning.Should().BeNull();
        result.Value.FinishReason.Should().Be(FinishReason.ContentFilter);
        result.Value.Usage.PromptTokens.Should().Be(0);
    }

    [Fact]
    public async Task AStreamThatFails_ReturnsTheError()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond(HttpStatusCode.PaymentRequired, "application/json", """{"error":{"code":"402","message":"Insufficient credits"}}""");

        var results = new List<Result<CompletionChunk>>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest("z-ai/glm-5.3"), CancellationToken.None))
        {
            results.Add(r);
        }

        results.Should().ContainSingle().Which.Error.Code.Should().Be("AI.InsufficientCredits");
    }
}
