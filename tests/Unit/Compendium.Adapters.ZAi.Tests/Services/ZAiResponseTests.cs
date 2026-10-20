// -----------------------------------------------------------------------
// <copyright file="ZAiResponseTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.ZAi.Tests.TestSupport;
using Compendium.Adapters.ZAi.Tools;

namespace Compendium.Adapters.ZAi.Tests.Services;

/// <summary>
/// What comes back from Z.ai, written from its documentation (no recorded call; read 2026-10-20): the stream of
/// https://docs.z.ai/guides/capabilities/streaming.md — <c>delta.reasoning_content</c> while the model thinks, then
/// <c>delta.content</c>, then a last chunk with <c>finish_reason</c>, an empty <c>content</c> and the <c>usage</c> — and the
/// response of https://docs.z.ai/guides/capabilities/thinking.md (<c>message.reasoning_content</c> beside
/// <c>message.content</c>). The tool call in text is the shape a caller gets when it asks for <c>&lt;tool_call&gt;</c> in
/// the answer with <c>&lt;/tool_call&gt;</c> as a stop sequence: the stop sequence itself never comes back.
/// </summary>
public class ZAiResponseTests
{
    // The same logical answer the Mistral and OpenRouter GLM tests read: the same chunks must come out.
    private const string Reasoning = "Need the search tool. Calling it.";
    private const string Answer = "<tool_call>{\"name\": \"search\", \"arguments\": {}}";

    private const string Stream =
        "data: {\"id\":\"g1\",\"created\":1790000000,\"model\":\"glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"Need the search tool.\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"id\":\"g1\",\"created\":1790000000,\"model\":\"glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\" Calling it.\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"id\":\"g1\",\"created\":1790000000,\"model\":\"glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"<tool_call>{\\\"name\\\": \\\"sea\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"id\":\"g1\",\"created\":1790000000,\"model\":\"glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"rch\\\", \\\"arguments\\\": {}}\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"id\":\"g1\",\"created\":1790000000,\"model\":\"glm-5.3\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}],\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":30,\"total_tokens\":80,\"prompt_tokens_details\":{\"cached_tokens\":0}}}\n\n"
        + "data: [DONE]\n\n";

    private static async Task<(List<CompletionChunk> Chunks, string Body)> StreamAsync(string sse, ReasoningOptions? reasoning)
    {
        var (sut, _, body) = TestFactories.Replying("text/event-stream", sse);
        var request = TestFactories.Request() with
        {
            Reasoning = reasoning,
            StopSequences = new List<string> { "</tool_call>" },
        };
        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(request, CancellationToken.None))
        {
            r.IsSuccess.Should().BeTrue(r.IsFailure ? r.Error.Message : null);
            chunks.Add(r.Value);
        }

        return (chunks, body()!);
    }

    [Fact]
    public async Task AStream_Asked_ReadsTheReasoningApart_AndTheToolCallWhole()
    {
        var (chunks, body) = await StreamAsync(Stream, new ReasoningOptions());

        string.Concat(chunks.Select(c => c.ReasoningDelta)).Should().Be(Reasoning);
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be(Answer, "the reasoning is never part of the answer");
        chunks.Should().HaveCount(5);
        chunks.Select(c => c.Index).Should().Equal(0, 1, 2, 3, 4);
        chunks[^1].IsFinal.Should().BeTrue();
        chunks[^1].FinishReason.Should().Be(FinishReason.Stop);
        chunks[^1].Usage!.PromptTokens.Should().Be(50);
        chunks[^1].Usage!.CompletionTokens.Should().Be(30);
        chunks[^1].Usage!.ReasoningTokens.Should().BeNull("Z.ai documents no reasoning count");

        using var sent = JsonDocument.Parse(body);
        sent.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
        sent.RootElement.GetProperty("stop")[0].GetString().Should().Be("</tool_call>");
    }

    [Fact]
    public async Task AStream_NotAsked_LeavesTheReasoningOut_AndSkipsItsChunks()
    {
        var (chunks, _) = await StreamAsync(Stream, null);

        chunks.Should().OnlyContain(c => c.ReasoningDelta == null);
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be(Answer);
        chunks.Should().HaveCount(3, "the two reasoning-only deltas have nothing to pass on");
        chunks.Select(c => c.Index).Should().Equal(0, 1, 2);
        chunks[^1].IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task AStream_AskedWithoutTheText_LeavesTheReasoningOut()
    {
        var (chunks, _) = await StreamAsync(Stream, new ReasoningOptions { IncludeReasoning = false });

        chunks.Should().OnlyContain(c => c.ReasoningDelta == null);
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be(Answer);
    }

    [Theory]
    [InlineData("network_error", FinishReason.Other)]
    [InlineData("sensitive", FinishReason.ContentFilter)]
    [InlineData("model_context_window_exceeded", FinishReason.Length)]
    [InlineData("tool_calls", FinishReason.ToolCall)]
    public async Task AStream_EndsWithZaisFinishReasons(string reason, FinishReason expected)
    {
        // https://docs.z.ai/api-reference/api-code.md: a stream that fails mid-way says so in finish_reason.
        var sse = "data: {\"id\":\"g2\",\"model\":\"glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Part\"},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"g2\",\"model\":\"glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\"},\"finish_reason\":\"" + reason + "\"}]}\n\n"
            + "data: {\"id\":\"g2\",\"model\":\"glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"never read\"},\"finish_reason\":null}]}\n\n";

        var (chunks, _) = await StreamAsync(sse, null);

        chunks[^1].FinishReason.Should().Be(expected);
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be("Part");
    }

    [Fact]
    public async Task AStream_SkipsWhatIsNotData_AndAChunkItCannotRead()
    {
        var sse = ": keep-alive\n\nevent: message\n"
            + "data: {not json}\n\n"
            + "data: {\"id\":\"g3\",\"model\":\"glm-5.3\",\"choices\":[]}\n\n"
            + "data: {\"id\":\"g3\",\"model\":\"glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"},\"finish_reason\":\"stop\"}]}\n\n";

        var (chunks, _) = await StreamAsync(sse, null);

        chunks.Should().ContainSingle().Which.ContentDelta.Should().Be("Hi");
    }

    [Fact]
    public async Task AStreamThatFails_ReturnsTheError()
    {
        var (sut, _, _) = TestFactories.Replying(
            "application/json", """{"error":{"code":"1113","message":"Insufficient balance or no resource package. Please recharge."}}""", HttpStatusCode.TooManyRequests);

        var results = new List<Result<CompletionChunk>>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.Request(), CancellationToken.None))
        {
            results.Add(r);
        }

        results.Should().ContainSingle().Which.Error.Code.Should().Be("AI.InsufficientCredits");
    }

    [Fact]
    public async Task AResponse_Asked_ReturnsTheReasoningApart()
    {
        var (sut, _, _) = TestFactories.Replying("application/json", """
            {"id":"g4","request_id":"r4","created":1677652288,"model":"glm-5.3",
             "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant",
               "content":"Artificial intelligence has ...","reasoning_content":"Let me analyze this question ..."}}],
             "usage":{"completion_tokens":239,"prompt_tokens":8,"prompt_tokens_details":{"cached_tokens":0},"total_tokens":247}}
            """);

        var asked = await sut.CompleteAsync(TestFactories.Request() with { Reasoning = new ReasoningOptions() }, CancellationToken.None);
        var unasked = await sut.CompleteAsync(TestFactories.Request(), CancellationToken.None);

        asked.Value.Content.Should().Be("Artificial intelligence has ...");
        asked.Value.Reasoning.Should().Be("Let me analyze this question ...");
        asked.Value.FinishReason.Should().Be(FinishReason.Stop);
        asked.Value.Usage.PromptTokens.Should().Be(8);
        asked.Value.Usage.CompletionTokens.Should().Be(239);
        asked.Value.Usage.ReasoningTokens.Should().BeNull();
        asked.Value.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1677652288).UtcDateTime);
        asked.Value.Metadata.Should().BeNull();
        unasked.Value.Content.Should().Be("Artificial intelligence has ...");
        unasked.Value.Reasoning.Should().BeNull("not asked for, and never in the answer");
    }

    [Fact]
    public async Task AResponse_ReadsNativeToolCalls_WithArgumentsAsAStringOrAnObject()
    {
        // The response schema types arguments as an object; the examples send a JSON string. Both are read.
        var (sut, _, _) = TestFactories.Replying("application/json", """
            {"id":"g5","created":1790000000,"model":"glm-5.3",
             "choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"reasoning_content":"Two tools.",
               "tool_calls":[{"id":"call_1","type":"function","function":{"name":"search","arguments":"{\"q\":\"x\"}"}},
                             {"id":"call_2","type":"function","function":{"name":"lookup","arguments":{"id":7}}},
                             {"id":"call_3","type":"function","function":{"name":"ping"}}]}}],
             "usage":{"prompt_tokens":20,"completion_tokens":10,"total_tokens":30,"completion_tokens_details":{"reasoning_tokens":4}}}
            """);

        var result = await sut.CompleteAsync(TestFactories.Request(), CancellationToken.None);

        result.Value.FinishReason.Should().Be(FinishReason.ToolCall);
        result.Value.Content.Should().BeEmpty();
        var calls = result.Value.GetToolCalls();
        calls.Select(c => c.ToolName).Should().Equal("search", "lookup", "ping");
        calls.Select(c => c.ArgumentsJson).Should().Equal("{\"q\":\"x\"}", "{\"id\":7}", "{}");
        result.Value.Usage.ReasoningTokens.Should().Be(4, "read if a reasoning count ever comes");
    }

    [Fact]
    public async Task AnEmptyResponse_ReadsAsNothing()
    {
        var (sut, _, _) = TestFactories.Replying("application/json", """{"id":"g6","model":"glm-5.3","choices":[]}""");

        var result = await sut.CompleteAsync(TestFactories.Request(), CancellationToken.None);

        result.Value.Content.Should().BeEmpty();
        result.Value.FinishReason.Should().Be(FinishReason.InProgress);
        result.Value.Usage.PromptTokens.Should().Be(0);
        result.Value.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task AFailure_IsReturned()
    {
        var (sut, _, _) = TestFactories.Replying(
            "application/json", """{"error":{"code":1211,"message":"Unknown Model, please check the model code."}}""", HttpStatusCode.BadRequest);

        var result = await sut.CompleteAsync(TestFactories.Request("glm-9"), CancellationToken.None);

        result.Error.Code.Should().Be("AI.ModelNotFound");
    }
}
