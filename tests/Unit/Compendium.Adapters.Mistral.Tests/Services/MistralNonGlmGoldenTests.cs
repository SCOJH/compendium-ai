// -----------------------------------------------------------------------
// <copyright file="MistralNonGlmGoldenTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Mistral.Tests.TestSupport;
using Compendium.Adapters.Mistral.Tools;

namespace Compendium.Adapters.Mistral.Tests.Services;

/// <summary>
/// The GLM dialect must not move a byte for any other model. Each golden below was produced by the adapter as it stood
/// before the dialect existed (the reasoning channel of #30, 8c11ca7): the exact JSON put on the wire, and the exact
/// <see cref="CompletionResponse"/> / <see cref="CompletionChunk"/> sequence / <see cref="AIModel"/> list read back,
/// serialised. They cover the non-GLM shapes the GLM path touches: reasoning on and off, tools, stop sequences, a
/// thinking stream with a text part that has no <c>type</c>, a plain stream, and the model list.
/// </summary>
public class MistralNonGlmGoldenTests
{
    private static readonly JsonSerializerOptions Golden = new() { WriteIndented = false };

    private static (MistralAIProvider Sut, MockHttpMessageHandler Handler, Func<string> Body) Create(string mediaType, string reply)
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
        return (sut, handler, () => body!);
    }


    [Fact]
    public async Task ACompletionWithToolsAndStop_IsTheSameOnTheWireAndBack()
    {
        var (sut, _, body) = Create("application/json", """
            {"id":"c1","model":"mistral-large-latest","created":1790000000,
             "choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":"Calling it.",
               "tool_calls":[{"id":"t1","type":"function","function":{"name":"search","arguments":"{\"q\":\"x\"}"}}]}}],
             "usage":{"prompt_tokens":40,"completion_tokens":12,"total_tokens":52}}
            """);
        var request = new CompletionRequest
        {
            Model = "mistral-large-latest",
            SystemPrompt = "Be brief.",
            Messages = new List<Message>
            {
                Message.User("Hi"),
                Message.Assistant("Yes?"),
                new() { Role = MessageRole.User, Content = "Find x", Name = "alice" },
            },
            Temperature = 0.4f,
            MaxTokens = 512,
            TopP = 0.9f,
            StopSequences = new List<string> { "</tool_call>" },
        }.WithTools(new List<AgentTool> { new("search", "Searches.", """{"type":"object","properties":{"q":{"type":"string"}}}""") }, "any");

        var result = await sut.CompleteAsync(request, CancellationToken.None);

        var response = JsonSerializer.Serialize(result.Value, Golden);
        body().Should().Be(ToolsRequest);
        response.Should().Be(ToolsResponse);
    }

    [Fact]
    public async Task AReasoningCompletion_IsTheSameOnTheWireAndBack()
    {
        var (sut, _, body) = Create("application/json", """
            {"id":"c2","model":"magistral-medium-latest","created":1790000001,
             "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant",
               "content":[{"type":"thinking","thinking":[{"type":"text","text":"Weighing it."}]},{"type":"text","text":"Answer."},{"text":" Untyped."}]}}],
             "usage":{"prompt_tokens":9,"completion_tokens":30,"total_tokens":39}}
            """);
        var request = TestFactories.SimpleCompletionRequest("magistral-medium-latest") with
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Max, BudgetTokens = 2048 },
        };

        var result = await sut.CompleteAsync(request, CancellationToken.None);

        var response = JsonSerializer.Serialize(result.Value, Golden);
        body().Should().Be(ReasoningRequest);
        response.Should().Be(ReasoningResponse);
    }

    [Fact]
    public async Task AThinkingStream_IsTheSameOnTheWireAndBack()
    {
        var (sut, _, body) = Create("text/event-stream",
            "data: {\"id\":\"s1\",\"model\":\"magistral-medium-latest\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"s1\",\"model\":\"magistral-medium-latest\",\"choices\":[{\"index\":0,\"delta\":{\"content\":[{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\"First.\"}]}]},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"s1\",\"model\":\"magistral-medium-latest\",\"choices\":[{\"index\":0,\"delta\":{\"content\":[{\"type\":\"thinking\",\"thinking\":[]}]},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"s1\",\"model\":\"magistral-medium-latest\",\"choices\":[{\"index\":0,\"delta\":{\"content\":[{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\" Then.\"}]},{\"type\":\"text\",\"text\":\"An\"},{\"text\":\"untyped\"}]},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"s1\",\"model\":\"magistral-medium-latest\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"swer.\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":7,\"total_tokens\":12}}\n\n"
            + "data: [DONE]\n\n");
        var request = TestFactories.SimpleCompletionRequest("magistral-medium-latest") with
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
            StopSequences = new List<string> { "</tool_call>" },
        };

        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(request, CancellationToken.None))
        {
            chunks.Add(r.Value);
        }

        var read = JsonSerializer.Serialize(chunks, Golden);
        body().Should().Be(ThinkingStreamRequest);
        read.Should().Be(ThinkingStreamChunks);
    }

    [Fact]
    public async Task APlainStream_IsTheSameOnTheWireAndBack()
    {
        var (sut, _, body) = Create("text/event-stream",
            "data: {\"id\":\"s2\",\"model\":\"mistral-small-latest\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hel\"},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"s2\",\"model\":\"mistral-small-latest\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"lo\"},\"finish_reason\":\"length\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n\n"
            + "data: [DONE]\n\n");

        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest("mistral-small-latest"), CancellationToken.None))
        {
            chunks.Add(r.Value);
        }

        var read = JsonSerializer.Serialize(chunks, Golden);
        body().Should().Be(PlainStreamRequest);
        read.Should().Be(PlainStreamChunks);
    }

    [Fact]
    public async Task TheModelList_IsTheSame()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/models").Respond("application/json", """
            {"data":[{"id":"mistral-large-latest","owned_by":"mistralai"},{"id":"pixtral-large-latest"},{"id":"codestral-latest"},
                     {"id":"mistral-embed"},{"id":"magistral-medium-latest","owned_by":"mistralai"}]}
            """);

        var result = await sut.ListModelsAsync(CancellationToken.None);

        var listed = JsonSerializer.Serialize(result.Value, Golden);
        listed.Should().Be(Models);
    }

    private const string ToolsRequest =
        """{"model":"mistral-large-latest","messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"Hi"},{"role":"assistant","content":"Yes?"},{"role":"user","content":"Find x","name":"alice"}],"temperature":0.4,"max_tokens":512,"top_p":0.9,"stop":["\u003C/tool_call\u003E"],"stream":false,"tools":[{"type":"function","function":{"name":"search","description":"Searches.","parameters":{"type":"object","properties":{"q":{"type":"string"}}}}}],"tool_choice":"any"}""";
    // The one golden native tool calls moved: the response carries tool_calls, so it now also reads them into
    // CompletionResponse.ToolCalls, with their ids. Every other byte is the same, Metadata's mistral.tool_calls included.
    private const string ToolsResponse =
        """{"Id":"c1","Model":"mistral-large-latest","Content":"Calling it.","FinishReason":"ToolCall","Reasoning":null,"ToolCalls":[{"Id":"t1","Name":"search","ArgumentsJson":"{\u0022q\u0022:\u0022x\u0022}"}],"Usage":{"PromptTokens":40,"CompletionTokens":12,"TotalTokens":52,"ReasoningTokens":null,"EstimatedCostUsd":null},"CreatedAt":"2026-09-21T14:13:20Z","Metadata":{"mistral.tool_calls":[{"ToolName":"search","ArgumentsJson":"{\u0022q\u0022:\u0022x\u0022}","ResultText":"","IsError":false,"Latency":"00:00:00"}]}}""";
    private const string ReasoningRequest =
        """{"model":"magistral-medium-latest","messages":[{"role":"user","content":"Hello"}],"temperature":0.7,"max_tokens":4096,"stream":false,"reasoning_effort":"high"}""";
    private const string ReasoningResponse =
        """{"Id":"c2","Model":"magistral-medium-latest","Content":"Answer. Untyped.","FinishReason":"Stop","Reasoning":"Weighing it.","Usage":{"PromptTokens":9,"CompletionTokens":30,"TotalTokens":39,"ReasoningTokens":null,"EstimatedCostUsd":null},"CreatedAt":"2026-09-21T14:13:21Z","Metadata":null}""";
    private const string ThinkingStreamRequest =
        """{"model":"magistral-medium-latest","messages":[{"role":"user","content":"Hello"}],"temperature":0.7,"max_tokens":4096,"stop":["\u003C/tool_call\u003E"],"stream":true,"reasoning_effort":"high"}""";
    private const string ThinkingStreamChunks =
        """[{"Id":"s1","ContentDelta":"","ReasoningDelta":null,"Index":0,"IsFinal":false,"FinishReason":null,"Usage":null},{"Id":"s1","ContentDelta":"","ReasoningDelta":"First.","Index":1,"IsFinal":false,"FinishReason":null,"Usage":null},{"Id":"s1","ContentDelta":"","ReasoningDelta":null,"Index":2,"IsFinal":false,"FinishReason":null,"Usage":null},{"Id":"s1","ContentDelta":"An","ReasoningDelta":" Then.","Index":3,"IsFinal":false,"FinishReason":null,"Usage":null},{"Id":"s1","ContentDelta":"swer.","ReasoningDelta":null,"Index":4,"IsFinal":true,"FinishReason":"Stop","Usage":{"PromptTokens":5,"CompletionTokens":7,"TotalTokens":12,"ReasoningTokens":null,"EstimatedCostUsd":null}}]""";
    private const string PlainStreamRequest =
        """{"model":"mistral-small-latest","messages":[{"role":"user","content":"Hello"}],"temperature":0.7,"max_tokens":4096,"stream":true}""";
    private const string PlainStreamChunks =
        """[{"Id":"s2","ContentDelta":"Hel","ReasoningDelta":null,"Index":0,"IsFinal":false,"FinishReason":null,"Usage":null},{"Id":"s2","ContentDelta":"lo","ReasoningDelta":null,"Index":1,"IsFinal":true,"FinishReason":"Length","Usage":{"PromptTokens":3,"CompletionTokens":2,"TotalTokens":5,"ReasoningTokens":null,"EstimatedCostUsd":null}}]""";
    private const string Models =
        """[{"Id":"mistral-large-latest","Name":"mistral-large-latest","Provider":"mistralai","ContextWindow":null,"MaxOutputTokens":null,"SupportsStreaming":true,"SupportsEmbeddings":false,"SupportsVision":false,"SupportsTools":true,"PricingInputPerMillion":null,"PricingOutputPerMillion":null,"Metadata":null},{"Id":"pixtral-large-latest","Name":"pixtral-large-latest","Provider":"mistral","ContextWindow":null,"MaxOutputTokens":null,"SupportsStreaming":true,"SupportsEmbeddings":false,"SupportsVision":true,"SupportsTools":true,"PricingInputPerMillion":null,"PricingOutputPerMillion":null,"Metadata":null},{"Id":"codestral-latest","Name":"codestral-latest","Provider":"mistral","ContextWindow":null,"MaxOutputTokens":null,"SupportsStreaming":true,"SupportsEmbeddings":false,"SupportsVision":false,"SupportsTools":true,"PricingInputPerMillion":null,"PricingOutputPerMillion":null,"Metadata":{"specialty":"code"}},{"Id":"mistral-embed","Name":"mistral-embed","Provider":"mistral","ContextWindow":null,"MaxOutputTokens":null,"SupportsStreaming":false,"SupportsEmbeddings":true,"SupportsVision":false,"SupportsTools":false,"PricingInputPerMillion":null,"PricingOutputPerMillion":null,"Metadata":null},{"Id":"magistral-medium-latest","Name":"magistral-medium-latest","Provider":"mistralai","ContextWindow":null,"MaxOutputTokens":null,"SupportsStreaming":true,"SupportsEmbeddings":false,"SupportsVision":false,"SupportsTools":true,"PricingInputPerMillion":null,"PricingOutputPerMillion":null,"Metadata":null}]""";
}
