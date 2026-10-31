// -----------------------------------------------------------------------
// <copyright file="MistralNativeToolCallTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Mistral.Tests.TestSupport;
using Compendium.Adapters.Mistral.Tools;

namespace Compendium.Adapters.Mistral.Tests.Services;

/// <summary>
/// Native tool calls on Mistral's wire. No recorded call: every fixture was written from Mistral's documentation, read
/// 2026-10-21.
/// <list type="bullet">
/// <item><description>https://docs.mistral.ai/studio/conversations/function-calling.md: the response
/// (<c>finish_reason: "tool_calls"</c>, <c>content: ""</c>, a call <c>D681PevKs</c> to <c>retrieve_payment_status</c>),
/// the assistant message sent back as is, and the tool message <c>{role: "tool", name, content, tool_call_id}</c>.</description></item>
/// <item><description>https://docs.mistral.ai/openapi.yaml: <c>DeltaMessage.tool_calls</c> is a list of
/// <c>ToolCall {id (default "null"), type, function {name, arguments: object or string}, index (default 0)}</c>;
/// <c>AssistantMessage</c> takes <c>tool_calls</c>, <c>ToolMessage</c> takes <c>tool_call_id</c> and <c>name</c>; the stream's
/// <c>finish_reason</c> includes <c>tool_calls</c>.</description></item>
/// </list>
/// The guide shows no stream. Mistral usually sends a call whole in one delta; the split shapes below (id and name first,
/// arguments in pieces, as OpenAI-compatible streams do) are read too.
/// </summary>
public class MistralNativeToolCallTests
{
    private const string CallId = "D681PevKs";
    private const string ToolName = "retrieve_payment_status";
    private const string Arguments = "{\"transaction_id\": \"T1001\"}";

    private static readonly ToolCall Call = new() { Id = CallId, Name = ToolName, ArgumentsJson = Arguments };

    private static readonly IReadOnlyList<AgentTool> Tools = new List<AgentTool>
    {
        new(ToolName, "Payment status of a transaction.", """{"type":"object","properties":{"transaction_id":{"type":"string"}},"required":["transaction_id"]}"""),
    };

    // ---------- the conversation, sent ----------

    [Fact]
    public async Task TheConversation_GoesInMistralsToolFormat()
    {
        var request = Conversation(
            Message.User("Status of transaction T1001?"),
            Message.Assistant(string.Empty, new[] { Call }),
            Message.ToolResult(CallId, "{\"status\": \"Paid\"}"));

        var sent = await SendAsync(request);

        sent.GetProperty("messages").GetRawText().Should().Be(
            """[{"role":"user","content":"Status of transaction T1001?"},"""
            + """{"role":"assistant","content":"","tool_calls":[{"id":"D681PevKs","type":"function","function":{"name":"retrieve_payment_status","arguments":"{\u0022transaction_id\u0022: \u0022T1001\u0022}"}}]},"""
            + """{"role":"tool","content":"{\u0022status\u0022: \u0022Paid\u0022}","name":"retrieve_payment_status","tool_call_id":"D681PevKs"}]""",
            "the assistant turn carries its call, the tool message the call's id and the called tool's name, as Mistral's guide shows them");
        sent.GetProperty("tool_choice").GetString().Should().Be("auto");
    }

    [Fact]
    public async Task SeveralCallsInOneTurn_GoInOrder_EachAnsweredByItsId()
    {
        var second = new ToolCall { Id = "Kq3x9ZbT1", Name = "retrieve_payment_date", ArgumentsJson = Arguments };
        var request = Conversation(
            Message.User("Status and date of T1001?"),
            Message.Assistant("Checking both.", new[] { Call, second }),
            Message.ToolResult(second.Id, "{\"date\": \"2021-10-05\"}"),
            Message.ToolResult(CallId, "{\"status\": \"Paid\"}"));

        var sent = await SendAsync(request);

        var messages = sent.GetProperty("messages");
        var calls = messages[1].GetProperty("tool_calls");
        calls.GetArrayLength().Should().Be(2);
        calls[0].GetProperty("id").GetString().Should().Be(CallId);
        calls[1].GetProperty("id").GetString().Should().Be("Kq3x9ZbT1");
        calls[1].GetProperty("function").GetProperty("name").GetString().Should().Be("retrieve_payment_date");
        messages[1].GetProperty("content").GetString().Should().Be("Checking both.");
        messages[2].GetProperty("tool_call_id").GetString().Should().Be("Kq3x9ZbT1");
        messages[2].GetProperty("name").GetString().Should().Be("retrieve_payment_date", "each result takes the name of the call it answers");
        messages[3].GetProperty("tool_call_id").GetString().Should().Be(CallId);
        messages[3].GetProperty("name").GetString().Should().Be(ToolName);
    }

    [Fact]
    public async Task AToolMessage_KeepsTheNameTheCallerGave()
    {
        var request = Conversation(
            Message.User("Status of transaction T1001?"),
            Message.Assistant(string.Empty, new[] { Call }),
            Message.ToolResult(CallId, "{}") with { Name = "payments" });

        var sent = await SendAsync(request);

        sent.GetProperty("messages")[2].GetProperty("name").GetString().Should().Be("payments");
    }

    [Fact]
    public async Task AToolMessage_ForACallNotInTheConversation_GoesWithItsIdAndNoName()
    {
        var request = Conversation(
            Message.User("Status of transaction T1001?"),
            Message.ToolResult(CallId, "{}"));

        var sent = await SendAsync(request);

        var tool = sent.GetProperty("messages")[1];
        tool.GetProperty("tool_call_id").GetString().Should().Be(CallId);
        tool.TryGetProperty("name", out _).Should().BeFalse("no call in the conversation gives one, and none is invented");
    }

    [Fact]
    public async Task TheNewFields_WhereMistralTakesNoneOfThem_AreLeftOut()
    {
        // On a role that does not take them, or empty, the fields change nothing: the body is the one the same
        // conversation without them puts on the wire.
        var with = Conversation(
            new Message { Role = MessageRole.User, Content = "Hi", ToolCalls = new[] { Call }, ToolCallId = CallId },
            new Message { Role = MessageRole.Assistant, Content = "Yes?", ToolCalls = Array.Empty<ToolCall>(), ToolCallId = CallId },
            new Message { Role = MessageRole.Tool, Content = "{}", ToolCalls = new[] { Call }, ToolCallId = string.Empty },
            new Message { Role = MessageRole.System, Content = "Be brief.", ToolCalls = new[] { Call }, ToolCallId = CallId });
        var without = Conversation(
            Message.User("Hi"),
            Message.Assistant("Yes?"),
            new Message { Role = MessageRole.Tool, Content = "{}" },
            Message.System("Be brief."));

        var sentWith = await SendRawAsync(with);
        var sentWithout = await SendRawAsync(without);

        sentWith.Should().Be(sentWithout);
        sentWith.Should().NotContain("tool_call_id").And.NotContain("\"tool_calls\"");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelToolCalls_IsSentAsAsked(bool allowed)
    {
        var sent = await SendAsync(Conversation(Message.User("Status of transaction T1001?")).WithParallelToolCalls(allowed));

        sent.GetProperty("parallel_tool_calls").GetBoolean().Should().Be(allowed);
        sent.GetProperty("tools").GetArrayLength().Should().Be(1, "the tools stay");
    }

    [Fact]
    public async Task ParallelToolCalls_Unasked_IsNotSent()
    {
        var sent = await SendAsync(Conversation(Message.User("Status of transaction T1001?")));

        sent.TryGetProperty("parallel_tool_calls", out _).Should().BeFalse("Mistral's default applies, as before");
    }

    // ---------- the calls, read back from a response ----------

    [Fact]
    public async Task AResponse_ReadsTheCallsWithTheirIds_AndKeepsTheMetadataAsBefore()
    {
        // The guide's response, as is.
        var response = await CompleteAsync("""
            {"id":"7cbd8962041442459eb3636e1e3cbf10","object":"chat.completion","model":"mistral-large-latest",
             "usage":{"prompt_tokens":94,"completion_tokens":30,"total_tokens":124},"created":1721403550,
             "choices":[{"index":0,"finish_reason":"tool_calls","message":{"content":"","tool_calls":[
               {"function":{"name":"retrieve_payment_status","arguments":"{\"transaction_id\": \"T1001\"}"},"id":"D681PevKs","type":"function"}],
               "prefix":false,"role":"assistant"}}]}
            """);

        response.ToolCalls.Should().BeEquivalentTo(new[] { Call });
        response.Content.Should().BeEmpty();
        response.FinishReason.Should().Be(FinishReason.ToolCall);
        var invocation = response.GetToolCalls().Should().ContainSingle().Subject;
        invocation.ToolName.Should().Be(ToolName);
        invocation.ArgumentsJson.Should().Be(Arguments);
    }

    [Fact]
    public async Task AResponse_ReadsArgumentsGivenAsAnObject_AsTheirJson()
    {
        // Mistral's FunctionCall.arguments is "an object or a string": an object used to fail the whole response.
        var response = await CompleteAsync("""
            {"id":"r","model":"mistral-large-latest","created":0,
             "choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":"","tool_calls":[
               {"id":"D681PevKs","type":"function","function":{"name":"retrieve_payment_status","arguments":{"transaction_id": "T1001"}}}]}}]}
            """);

        response.ToolCalls.Should().ContainSingle().Which.ArgumentsJson.Should().Be(Arguments);
        response.GetToolCalls().Should().ContainSingle().Which.ArgumentsJson.Should().Be(Arguments);
    }

    [Fact]
    public async Task AResponse_WithACallThatHasNoArgumentsOrNoId_ReadsAnEmptyObjectAndAnEmptyId()
    {
        var response = await CompleteAsync("""
            {"id":"r","model":"mistral-large-latest","created":0,
             "choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":"","tool_calls":[
               {"id":"null","type":"function","function":{"name":"list_transactions","arguments":""}}]}}]}
            """);

        var call = response.ToolCalls.Should().ContainSingle().Subject;
        call.Id.Should().BeEmpty("\"null\" is the schema's default, not an id");
        call.Name.Should().Be("list_transactions");
        call.ArgumentsJson.Should().Be("{}");
    }

    [Fact]
    public async Task AResponseWithoutCalls_HasNone()
    {
        var response = await CompleteAsync("""
            {"id":"r","model":"mistral-large-latest","created":0,
             "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"Paid.","tool_calls":[]}}]}
            """);

        response.ToolCalls.Should().BeNull();
        response.Content.Should().Be("Paid.");
    }

    // ---------- the calls, read back from a stream ----------

    [Theory]
    [InlineData("\"{\\\"transaction_id\\\": \\\"T1001\\\"}\"")]
    [InlineData("{\"transaction_id\": \"T1001\"}")]
    public async Task AStream_WithTheCallWholeInTheLastDelta_EndsWithIt(string arguments)
    {
        // What Mistral usually sends: the call whole, with the finish reason and the usage. The arguments as a string,
        // or as an object (the schema takes both).
        var chunks = await StreamAsync(
            "mistral-large-latest",
            null,
            """{"id":"s","model":"mistral-large-latest","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}""",
            """{"id":"s","model":"mistral-large-latest","choices":[{"index":0,"delta":{"tool_calls":[{"id":"D681PevKs","function":{"name":"retrieve_payment_status","arguments":"""
                + arguments
                + """},"index":0}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":94,"completion_tokens":30,"total_tokens":124}}""");

        chunks.Should().HaveCount(2, "one chunk per delta, as before");
        chunks[0].ToolCalls.Should().BeNull("the calls go out on the final chunk only");
        var last = chunks[^1];
        last.IsFinal.Should().BeTrue();
        last.FinishReason.Should().Be(FinishReason.ToolCall);
        last.ToolCalls.Should().BeEquivalentTo(new[] { Call });
        last.ContentDelta.Should().BeEmpty();
        last.Usage!.PromptTokens.Should().Be(94);
    }

    [Fact]
    public async Task AStream_WithTheArgumentsInPieces_JoinsThem()
    {
        // The id and the name first, then the arguments over three deltas: one repeats the name empty and gives the
        // schema's default id "null", the last one has no index and no id (it continues the last call).
        var chunks = await StreamAsync(
            "mistral-large-latest",
            null,
            """{"id":"s","model":"mistral-large-latest","choices":[{"index":0,"delta":{"role":"assistant","content":"Checking."},"finish_reason":null}]}""",
            """{"id":"s","model":"mistral-large-latest","choices":[{"index":0,"delta":{"tool_calls":[{"id":"D681PevKs","type":"function","function":{"name":"retrieve_payment_status","arguments":""},"index":0}]},"finish_reason":null}]}""",
            """{"id":"s","model":"mistral-large-latest","choices":[{"index":0,"delta":{"tool_calls":[{"id":"null","function":{"name":"","arguments":"{\"transaction_"},"index":0}]},"finish_reason":null}]}""",
            """{"id":"s","model":"mistral-large-latest","choices":[{"index":0,"delta":{"tool_calls":[{"function":{"name":"retrieve_payment_status","arguments":"id\": \"T1"},"index":0}]},"finish_reason":null}]}""",
            """{"id":"s","model":"mistral-large-latest","choices":[{"index":0,"delta":{"tool_calls":[{"function":{"arguments":"001\"}"}}]},"finish_reason":null}]}""",
            """{"id":"s","model":"mistral-large-latest","choices":[{"index":0,"delta":{"content":""},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":94,"completion_tokens":30,"total_tokens":124}}""");

        chunks.Should().HaveCount(6, "one chunk per delta, as before");
        chunks.Select(c => c.Index).Should().Equal(0, 1, 2, 3, 4, 5);
        chunks.Take(5).Should().OnlyContain(c => c.ToolCalls == null && !c.IsFinal);
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be("Checking.", "the text before the call is still the answer");
        chunks[^1].FinishReason.Should().Be(FinishReason.ToolCall);
        chunks[^1].ToolCalls.Should().BeEquivalentTo(new[] { Call }, "the name is taken once, never joined, and \"null\" never replaces the id");
    }

    public static TheoryData<string[]> SeveralCalls => new()
    {
        // By index, the pieces of the two calls interleaved.
        new[]
        {
            """{"id":"s","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"id":"aaaaaaaa1","function":{"name":"get_weather","arguments":"{\"city\":"},"index":0}]},"finish_reason":null}]}""",
            """{"id":"s","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"id":"bbbbbbbb2","function":{"name":"get_time","arguments":"{\"tz\":"},"index":1}]},"finish_reason":null}]}""",
            """{"id":"s","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"function":{"name":"","arguments":"\"Paris\"}"},"index":0},{"function":{"name":"","arguments":"\"CET\"}"},"index":1}]},"finish_reason":null}]}""",
            """{"id":"s","model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
        },

        // Whole, in one delta, both with Mistral's default index 0: told apart by their ids.
        new[]
        {
            """{"id":"s","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"id":"aaaaaaaa1","function":{"name":"get_weather","arguments":"{\"city\":\"Paris\"}"},"index":0},{"id":"bbbbbbbb2","function":{"name":"get_time","arguments":"{\"tz\":\"CET\"}"},"index":0}]},"finish_reason":"tool_calls"}]}""",
        },

        // Whole, with no index at all.
        new[]
        {
            """{"id":"s","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"id":"aaaaaaaa1","function":{"name":"get_weather","arguments":"{\"city\":\"Paris\"}"}},{"id":"bbbbbbbb2","function":{"name":"get_time","arguments":"{\"tz\":\"CET\"}"}}]},"finish_reason":"tool_calls"}]}""",
        },
    };

    [Theory]
    [MemberData(nameof(SeveralCalls))]
    public async Task AStream_WithSeveralCalls_KeepsThemApart_InTheOrderTheyStarted(string[] deltas)
    {
        var chunks = await StreamAsync("mistral-large-latest", null, deltas);

        chunks[^1].ToolCalls.Should().BeEquivalentTo(
            new[]
            {
                new ToolCall { Id = "aaaaaaaa1", Name = "get_weather", ArgumentsJson = "{\"city\":\"Paris\"}" },
                new ToolCall { Id = "bbbbbbbb2", Name = "get_time", ArgumentsJson = "{\"tz\":\"CET\"}" },
            },
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task AStream_WithACallThatHasNoArguments_ReadsAnEmptyObject()
    {
        var chunks = await StreamAsync(
            "mistral-large-latest",
            null,
            """{"id":"s","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"id":"cccccccc3","function":{"name":"list_transactions","arguments":""},"index":0}]},"finish_reason":"tool_calls"}]}""");

        chunks[^1].ToolCalls.Should().ContainSingle().Which.ArgumentsJson.Should().Be("{}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AGlmStream_EndsWithTheCall_Too(bool reasoningAsked)
    {
        // GLM served by Mistral takes the same path: its thinking chunks, then the call in pieces. A delta that carries
        // only a piece of a call has nothing else to pass on, so the dialect skips it; the call still ends up whole on
        // the final chunk.
        var chunks = await StreamAsync(
            "zai-glm-5-3",
            reasoningAsked ? new ReasoningOptions() : null,
            """{"id":"g","model":"zai-glm-5-3","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}""",
            """{"id":"g","model":"zai-glm-5-3","choices":[{"index":0,"delta":{"content":[{"type":"thinking","thinking":[{"type":"text","text":"Need the payment tool."}]}]},"finish_reason":null}]}""",
            """{"id":"g","model":"zai-glm-5-3","choices":[{"index":0,"delta":{"tool_calls":[{"id":"D681PevKs","function":{"name":"retrieve_payment_status","arguments":"{\"transaction_id\": "},"index":0}]},"finish_reason":null}]}""",
            """{"id":"g","model":"zai-glm-5-3","choices":[{"index":0,"delta":{"tool_calls":[{"id":"null","function":{"name":"","arguments":"\"T1001\"}"},"index":0}]},"finish_reason":null}]}""",
            """{"id":"g","model":"zai-glm-5-3","choices":[{"index":0,"delta":{"content":[]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":50,"completion_tokens":30,"total_tokens":80}}""");

        chunks.Should().HaveCount(reasoningAsked ? 2 : 1, "only the thinking, when asked for, and the final chunk have something to pass on");
        chunks.Select(c => c.Index).Should().Equal(reasoningAsked ? new[] { 0, 1 } : new[] { 0 });
        chunks[^1].IsFinal.Should().BeTrue();
        chunks[^1].FinishReason.Should().Be(FinishReason.ToolCall);
        chunks[^1].ToolCalls.Should().BeEquivalentTo(new[] { Call });
        chunks.Should().OnlyContain(c => c.ContentDelta.Length == 0, "the call is never answer text");
    }

    [Fact]
    public async Task AGlmConversation_GoesInMistralsToolFormat_Too()
    {
        var request = Conversation(
            Message.User("Status of transaction T1001?"),
            Message.Assistant(string.Empty, new[] { Call }),
            Message.ToolResult(CallId, "{\"status\": \"Paid\"}"));

        var sent = await SendAsync(request with { Model = "zai-glm-5-3" });

        var messages = sent.GetProperty("messages");
        messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString().Should().Be(CallId);
        messages[2].GetProperty("tool_call_id").GetString().Should().Be(CallId);
    }

    // ---------- helpers ----------

    private static CompletionRequest Conversation(params Message[] messages) =>
        new CompletionRequest { Model = "mistral-large-latest", Messages = messages }.WithTools(Tools, "auto");

    private static async Task<JsonElement> SendAsync(CompletionRequest request) =>
        JsonDocument.Parse(await SendRawAsync(request)).RootElement.Clone();

    private static async Task<string> SendRawAsync(CompletionRequest request)
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
            .Respond("application/json", """{"id":"r","model":"m","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}]}""");

        var result = await sut.CompleteAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        return body!;
    }

    private static async Task<CompletionResponse> CompleteAsync(string reply)
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", reply);

        var result = await sut.CompleteAsync(Conversation(Message.User("Status of transaction T1001?")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        return result.Value;
    }

    private static async Task<List<CompletionChunk>> StreamAsync(string model, ReasoningOptions? reasoning, params string[] deltas)
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var sse = string.Concat(deltas.Select(d => "data: " + d + "\n\n")) + "data: [DONE]\n\n";
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("text/event-stream", sse);
        var request = Conversation(Message.User("Status of transaction T1001?")) with { Model = model, Reasoning = reasoning };

        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(request, CancellationToken.None))
        {
            r.IsSuccess.Should().BeTrue(r.IsFailure ? r.Error.Message : null);
            chunks.Add(r.Value);
        }

        return chunks;
    }
}
