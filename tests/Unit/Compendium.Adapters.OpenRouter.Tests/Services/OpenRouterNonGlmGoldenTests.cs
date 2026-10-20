// -----------------------------------------------------------------------
// <copyright file="OpenRouterNonGlmGoldenTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.OpenRouter.Tests.TestSupport;

namespace Compendium.Adapters.OpenRouter.Tests.Services;

/// <summary>
/// The GLM dialect must not move a byte for any other model. Each golden below was produced by the adapter as it stood
/// before the dialect existed (the reasoning channel of #30, 8c11ca7): the exact JSON put on the wire and the exact
/// <see cref="CompletionResponse"/> / <see cref="CompletionChunk"/> sequence read back, serialised. A non-GLM model that
/// returns OpenRouter's <c>reasoning</c> field keeps having it ignored, with or without <see cref="ReasoningOptions"/>.
/// </summary>
public class OpenRouterNonGlmGoldenTests
{
    private static readonly JsonSerializerOptions Golden = new() { WriteIndented = false };

    private static (OpenRouterAIProvider Sut, Func<string> Body) Create(string mediaType, string reply)
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
            .Respond(mediaType, reply);
        return (sut, () => body!);
    }


    [Fact]
    public async Task ACompletionWithReasoningAsked_IsTheSameOnTheWireAndBack()
    {
        var (sut, body) = Create("application/json", """
            {"id":"gen-1","model":"anthropic/claude-3.5-sonnet","created":1790000000,
             "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"Answer.","reasoning":"Weighing it."}}],
             "usage":{"prompt_tokens":12,"completion_tokens":30,"total_tokens":42,"completion_tokens_details":{"reasoning_tokens":20}}}
            """);
        var request = new CompletionRequest
        {
            Model = "anthropic/claude-3.5-sonnet",
            SystemPrompt = "Be brief.",
            Messages = new List<Message> { Message.User("Hi"), new() { Role = MessageRole.User, Content = "Go", Name = "bob" } },
            Temperature = 0.2f,
            MaxTokens = 300,
            TopP = 0.8f,
            FrequencyPenalty = 0.1f,
            PresencePenalty = 0.3f,
            StopSequences = new List<string> { "</tool_call>" },
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High, BudgetTokens = 1000 },
        };

        var result = await sut.CompleteAsync(request, CancellationToken.None);

        var response = JsonSerializer.Serialize(result.Value, Golden);
        body().Should().Be(ReasoningRequest);
        response.Should().Be(ReasoningResponse);
    }

    [Fact]
    public async Task AReasoningStream_IsTheSameOnTheWireAndBack()
    {
        var (sut, body) = Create("text/event-stream",
            ": OPENROUTER PROCESSING\n\n"
            + "data: {\"id\":\"gen-2\",\"model\":\"deepseek/deepseek-r1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning\":\"Think.\"},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"gen-2\",\"model\":\"deepseek/deepseek-r1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Done.\"},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: {\"id\":\"gen-2\",\"model\":\"deepseek/deepseek-r1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\",\"role\":\"assistant\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":9,\"total_tokens\":13,\"completion_tokens_details\":{\"reasoning_tokens\":6}}}\n\n"
            + "data: [DONE]\n\n");
        var request = TestFactories.SimpleCompletionRequest("deepseek/deepseek-r1") with
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low, IncludeReasoning = false },
        };

        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(request, CancellationToken.None))
        {
            chunks.Add(r.Value);
        }

        var read = JsonSerializer.Serialize(chunks, Golden);
        body().Should().Be(ReasoningStreamRequest);
        read.Should().Be(ReasoningStreamChunks);
    }

    [Fact]
    public async Task APlainStream_IsTheSameOnTheWireAndBack()
    {
        var (sut, body) = Create("text/event-stream",
            "data: {\"id\":\"gen-3\",\"model\":\"anthropic/claude-3.5-sonnet\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"gen-3\",\"model\":\"anthropic/claude-3.5-sonnet\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"!\"},\"finish_reason\":\"length\"}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":2,\"total_tokens\":4}}\n\n"
            + "data: [DONE]\n\n");

        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None))
        {
            chunks.Add(r.Value);
        }

        var read = JsonSerializer.Serialize(chunks, Golden);
        body().Should().Be(PlainStreamRequest);
        read.Should().Be(PlainStreamChunks);
    }

    private const string ReasoningRequest =
        """{"model":"anthropic/claude-3.5-sonnet","messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"Hi"},{"role":"user","content":"Go","name":"bob"}],"temperature":0.2,"max_tokens":300,"top_p":0.8,"frequency_penalty":0.1,"presence_penalty":0.3,"stop":["\u003C/tool_call\u003E"],"stream":false}""";
    private const string ReasoningResponse =
        """{"Id":"gen-1","Model":"anthropic/claude-3.5-sonnet","Content":"Answer.","FinishReason":"Stop","Reasoning":null,"Usage":{"PromptTokens":12,"CompletionTokens":30,"TotalTokens":42,"ReasoningTokens":null,"EstimatedCostUsd":null},"CreatedAt":"2026-09-21T14:13:20Z","Metadata":null}""";
    private const string ReasoningStreamRequest =
        """{"model":"deepseek/deepseek-r1","messages":[{"role":"user","content":"Hello"}],"temperature":0.7,"max_tokens":4096,"stream":true}""";
    private const string ReasoningStreamChunks =
        """[{"Id":"gen-2","ContentDelta":"","ReasoningDelta":null,"Index":0,"IsFinal":false,"FinishReason":null,"Usage":null},{"Id":"gen-2","ContentDelta":"Done.","ReasoningDelta":null,"Index":1,"IsFinal":true,"FinishReason":"Stop","Usage":null}]""";
    private const string PlainStreamRequest =
        """{"model":"anthropic/claude-3.5-sonnet","messages":[{"role":"user","content":"Hello"}],"temperature":0.7,"max_tokens":4096,"stream":true}""";
    private const string PlainStreamChunks =
        """[{"Id":"gen-3","ContentDelta":"Hi","ReasoningDelta":null,"Index":0,"IsFinal":false,"FinishReason":null,"Usage":null},{"Id":"gen-3","ContentDelta":"!","ReasoningDelta":null,"Index":1,"IsFinal":true,"FinishReason":"Length","Usage":{"PromptTokens":2,"CompletionTokens":2,"TotalTokens":4,"ReasoningTokens":null,"EstimatedCostUsd":null}}]""";
}
