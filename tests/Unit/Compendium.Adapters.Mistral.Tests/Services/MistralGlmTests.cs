// -----------------------------------------------------------------------
// <copyright file="MistralGlmTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Mistral.Tests.TestSupport;
using Compendium.Adapters.Mistral.Tools;

namespace Compendium.Adapters.Mistral.Tests.Services;

/// <summary>
/// GLM 5.3 served by Mistral (<c>zai-glm-5-3</c>), through the GLM dialect. Written from Mistral's documentation (no
/// recorded call), read 2026-10-20: https://docs.mistral.ai/studio/conversations/reasoning.md — "zai-glm-5-3 supports low,
/// high, and max — not none. Unlike Mistral models, message.content is always returned as a list of chunks (thinking +
/// text) regardless of the reasoning_effort value; there is no plain-string mode" — and, for the stream, "a list
/// containing a ThinkChunk", then "a single list containing both a closing ThinkChunk and the first TextChunk", then the
/// answer: a plain string for Mistral's models, lists for GLM if there is truly no string mode. Both are read. The usage
/// comes on the chunk that carries the finish reason (https://docs.mistral.ai/studio/conversations/chat-completion.md).
/// The effort: the reasoning guide lists low / high / max for zai-glm-5-3, but the API's ReasoningEffort enum
/// (https://docs.mistral.ai/openapi.yaml, same date) is none / minimal / low / medium / high / xhigh, with no max, so GLM
/// 5.3 is sent low or high, and GLM 5.2 high or xhigh.
/// </summary>
public class MistralGlmTests
{
    // The same logical answer the Z.ai and OpenRouter GLM tests read: the same chunks must come out.
    private const string Reasoning = "Need the search tool. Calling it.";
    private const string Answer = "<tool_call>{\"name\": \"search\", \"arguments\": {}}";

    // Lists only: the thinking, the turn (closing thinking + first text), the rest of the answer as a text chunk.
    private const string ListsOnly =
        "data: {\"id\":\"g\",\"object\":\"chat.completion.chunk\",\"created\":1790000000,\"model\":\"zai-glm-5-3\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}],\"usage\":null}\n\n"
        + "data: {\"id\":\"g\",\"object\":\"chat.completion.chunk\",\"created\":1790000000,\"model\":\"zai-glm-5-3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":[{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\"Need the search tool.\"}]}]},\"finish_reason\":null}],\"usage\":null}\n\n"
        + "data: {\"id\":\"g\",\"object\":\"chat.completion.chunk\",\"created\":1790000000,\"model\":\"zai-glm-5-3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":[{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\" Calling it.\"}]},{\"type\":\"text\",\"text\":\"<tool_call>{\\\"name\\\": \\\"sea\"}]},\"finish_reason\":null}],\"usage\":null}\n\n"
        + "data: {\"id\":\"g\",\"object\":\"chat.completion.chunk\",\"created\":1790000000,\"model\":\"zai-glm-5-3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":[{\"type\":\"text\",\"text\":\"rch\\\", \\\"arguments\\\": {}}\"}]},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":30,\"total_tokens\":80}}\n\n"
        + "data: [DONE]\n\n";

    // Mistral's own shape after the turn: the rest of the answer as a plain string.
    private const string ListsThenString =
        "data: {\"id\":\"g\",\"model\":\"zai-glm-5-3\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"id\":\"g\",\"model\":\"zai-glm-5-3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":[{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\"Need the search tool.\"}]}]},\"finish_reason\":null}]}\n\n"
        + "data: {\"id\":\"g\",\"model\":\"zai-glm-5-3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":[{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\" Calling it.\"}]},{\"type\":\"text\",\"text\":\"<tool_call>{\\\"name\\\": \\\"sea\"}]},\"finish_reason\":null}]}\n\n"
        + "data: {\"id\":\"g\",\"model\":\"zai-glm-5-3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"rch\\\", \\\"arguments\\\": {}}\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":30,\"total_tokens\":80}}\n\n"
        + "data: [DONE]\n\n";

    private static async Task<(List<CompletionChunk> Chunks, JsonElement Sent)> StreamAsync(
        string sse, ReasoningOptions? reasoning, string model = "zai-glm-5-3", int? maxTokens = null)
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

    [Theory]
    [InlineData(ListsOnly)]
    [InlineData(ListsThenString)]
    public async Task AStream_Asked_ReadsTheThinkingApart_AndTheToolCallWhole(string sse)
    {
        var (chunks, _) = await StreamAsync(sse, new ReasoningOptions());

        string.Concat(chunks.Select(c => c.ReasoningDelta)).Should().Be(Reasoning);
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be(Answer, "the thinking is never part of the answer");
        chunks.Should().HaveCount(3, "the empty first delta has nothing to pass on");
        chunks.Select(c => c.Index).Should().Equal(0, 1, 2);
        chunks[^1].IsFinal.Should().BeTrue();
        chunks[^1].FinishReason.Should().Be(FinishReason.Stop);
        chunks[^1].Usage!.PromptTokens.Should().Be(50);
        chunks[^1].Usage!.CompletionTokens.Should().Be(30);
        chunks[^1].Usage!.ReasoningTokens.Should().BeNull("Mistral does not count the reasoning apart");
    }

    [Theory]
    [InlineData(ListsOnly)]
    [InlineData(ListsThenString)]
    public async Task AStream_NotAsked_LeavesTheThinkingOut_AndStillReadsTheWholeAnswer(string sse)
    {
        var (chunks, _) = await StreamAsync(sse, null);

        chunks.Should().OnlyContain(c => c.ReasoningDelta == null);
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be(Answer);
        chunks.Should().HaveCount(2, "the thinking-only delta has nothing to pass on");
        chunks[^1].IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task AStream_ReadsSeveralThinkingChunks_AnEmptyOne_AndATextPartWithNoType()
    {
        var sse =
            "data: {\"id\":\"g\",\"model\":\"zai-glm-5-3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":[{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\"a\"}]},{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\"b\"},{\"type\":\"text\",\"text\":\"c\"}]}]},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"g\",\"model\":\"zai-glm-5-3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":[{\"type\":\"thinking\",\"thinking\":[]}]},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"g\",\"model\":\"zai-glm-5-3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":[{\"type\":\"text\",\"text\":\"An\"},{\"text\":\"swer\"},{\"type\":\"image_url\",\"image_url\":\"x\"}]},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"g\",\"model\":\"zai-glm-5-3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":null},\"finish_reason\":\"model_length\"}]}\n\n";

        var (chunks, _) = await StreamAsync(sse, new ReasoningOptions());

        string.Concat(chunks.Select(c => c.ReasoningDelta)).Should().Be("abc");
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be("Answer", "a text part with no type is text (TextChunk.type defaults to text)");
        chunks.Should().HaveCount(3, "the empty thinking chunk has nothing to pass on");
        chunks[^1].FinishReason.Should().Be(FinishReason.Length);
    }

    [Fact]
    public async Task TheRequest_Unasked_SendsNoEffortNorThinking_ButRoomForTheReasoning()
    {
        var (_, sent) = await StreamAsync(ListsOnly, null);

        sent.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        sent.TryGetProperty("thinking", out _).Should().BeFalse("Mistral has no thinking field");
        sent.TryGetProperty("prompt_mode", out _).Should().BeFalse();
        sent.GetProperty("max_tokens").GetInt32().Should().Be(4096 + 32_768, "GLM 5.3 reasons anyway, and no effort is sent: its own default, max, may run");
        sent.GetProperty("temperature").GetSingle().Should().Be(0.7f, "sampling is kept: GLM takes it");
        sent.GetProperty("stop")[0].GetString().Should().Be("</tool_call>");
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "low", 1_000 + 4_096)]
    [InlineData(ReasoningEffort.Medium, "high", 1_000 + 16_384)]
    [InlineData(ReasoningEffort.High, "high", 1_000 + 16_384)]
    [InlineData(ReasoningEffort.XHigh, "high", 1_000 + 16_384)]
    [InlineData(ReasoningEffort.Max, "high", 1_000 + 16_384)]
    public async Task TheRequest_Asked_TakesTheEffortOnGlmsScale_UpToHigh_SinceTheEnumHasNoMax(ReasoningEffort effort, string wire, int maxTokens)
    {
        var (_, sent) = await StreamAsync(ListsOnly, new ReasoningOptions { Effort = effort }, maxTokens: 1_000);

        sent.GetProperty("reasoning_effort").GetString().Should().Be(wire).And.NotBe("max");
        sent.GetProperty("max_tokens").GetInt32().Should().Be(maxTokens, "the room follows the effort sent");
        sent.TryGetProperty("thinking", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "high", 1_000 + 16_384)]
    [InlineData(ReasoningEffort.Medium, "high", 1_000 + 16_384)]
    [InlineData(ReasoningEffort.High, "high", 1_000 + 16_384)]
    [InlineData(ReasoningEffort.XHigh, "xhigh", 1_000 + 32_768)]
    [InlineData(ReasoningEffort.Max, "xhigh", 1_000 + 32_768)]
    public async Task TheRequest_ToGlm52_KeepsHighOrXHigh_WhichTheEnumLists(ReasoningEffort effort, string wire, int maxTokens)
    {
        var (_, sent) = await StreamAsync(ListsOnly, new ReasoningOptions { Effort = effort }, model: "zai-glm-5-2", maxTokens: 1_000);

        sent.GetProperty("reasoning_effort").GetString().Should().Be(wire);
        sent.GetProperty("max_tokens").GetInt32().Should().Be(maxTokens);
    }

    [Fact]
    public async Task TheRequest_AskedWithoutAnEffort_SendsNone_NotMagistralsHigh()
    {
        var (_, sent) = await StreamAsync(ListsOnly, new ReasoningOptions { BudgetTokens = 3_000 }, model: "zai-glm-latest", maxTokens: 1_000);

        sent.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        sent.GetProperty("max_tokens").GetInt32().Should().Be(4_000);
    }

    [Fact]
    public async Task TheRequest_StaysUnderTheOutputLimit()
    {
        var (_, sent) = await StreamAsync(ListsOnly, null, model: "zai-glm-5-2", maxTokens: 500_000);

        sent.GetProperty("max_tokens").GetInt32().Should().Be(131_072);
    }

    [Fact]
    public async Task AResponse_InAList_WithNativeToolCalls_ReadsTheAnswerTheThinkingAndTheCalls()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", """
            {"id":"g1","object":"chat.completion","created":1790000000,"model":"zai-glm-5-3",
             "usage":{"prompt_tokens":40,"completion_tokens":120,"total_tokens":160,"prompt_tokens_details":{"cached_tokens":0}},
             "choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant",
               "tool_calls":[{"id":"t1","type":"function","function":{"name":"search","arguments":"{\"q\":\"x\"}"},"index":0}],
               "content":[{"type":"thinking","thinking":[{"type":"text","text":"The user asks for x."}]},{"type":"text","text":"Searching."}]}}]}
            """);
        var request = TestFactories.SimpleCompletionRequest("zai-glm-5-3")
            .WithTools(new List<AgentTool> { new("search", "Searches.") }, "auto");

        var asked = await sut.CompleteAsync(request with { Reasoning = new ReasoningOptions() }, CancellationToken.None);
        var unasked = await sut.CompleteAsync(request, CancellationToken.None);

        asked.Value.Content.Should().Be("Searching.");
        asked.Value.Reasoning.Should().Be("The user asks for x.");
        asked.Value.FinishReason.Should().Be(FinishReason.ToolCall);
        asked.Value.GetToolCalls().Should().ContainSingle().Which.ArgumentsJson.Should().Be("{\"q\":\"x\"}");
        asked.Value.Usage.CompletionTokens.Should().Be(120);
        unasked.Value.Content.Should().Be("Searching.");
        unasked.Value.Reasoning.Should().BeNull("not asked for, and never in the answer");
    }

    [Fact]
    public async Task TheModelList_DescribesTheGlms()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/models").Respond("application/json", """{"data":[{"id":"zai-glm-5-3"},{"id":"mistral-large-latest"}]}""");

        var result = await sut.ListModelsAsync(CancellationToken.None);

        var glm = result.Value[0];
        glm.ContextWindow.Should().Be(1_048_576);
        glm.MaxOutputTokens.Should().Be(131_072);
        glm.SupportsTools.Should().BeTrue();
        glm.Metadata.Should().Contain("reasoning", "always");
        result.Value[1].ContextWindow.Should().BeNull("a non-GLM model is listed as before");
        result.Value[1].Metadata.Should().BeNull();
    }
}
