// -----------------------------------------------------------------------
// <copyright file="MercuryNativeToolCallFieldsTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Mercury.Tests.TestSupport;

namespace Compendium.Adapters.Mercury.Tests.Services;

/// <summary>
/// The abstraction's native tool-call fields (<see cref="Message.ToolCalls"/>, <see cref="Message.ToolCallId"/>,
/// <see cref="CompletionChunk.ToolCalls"/>, <see cref="CompletionResponse.ToolCalls"/>) are not read by this adapter: it
/// ignores them. A conversation that carries them goes on the wire as the same conversation without them, and a stream
/// with <c>tool_calls</c> deltas ends as it did before.
/// </summary>
public class MercuryNativeToolCallFieldsTests
{
    private static readonly ToolCall Call = new() { Id = "call_1", Name = "get_weather", ArgumentsJson = "{\"city\":\"Paris\"}" };

    [Fact]
    public async Task AConversationWithTheNewFields_GoesOnTheWireAsWithoutThem()
    {
        var with = new List<Message>
        {
            Message.User("Weather in Paris?"),
            Message.Assistant("Checking.", new[] { Call }),
            Message.ToolResult(Call.Id, "{\"tempC\":18}"),
        };
        var without = new List<Message>
        {
            Message.User("Weather in Paris?"),
            Message.Assistant("Checking."),
            new() { Role = MessageRole.Tool, Content = "{\"tempC\":18}" },
        };

        var sentWith = await SendAsync(with);
        var sentWithout = await SendAsync(without);

        sentWith.Should().Be(sentWithout);
        sentWith.Should().NotContain("call_1");
    }

    [Fact]
    public async Task AStreamWithToolCallDeltas_EndsAsBefore_AndReadsNoCall()
    {
        var sse =
            "data: {\"id\":\"s\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"\"}}]},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"s\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"city\\\":\\\"Paris\\\"}\"}}]},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"s\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":7,\"total_tokens\":12}}\n\n"
            + "data: [DONE]\n\n";
        var (client, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(client);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("text/event-stream", sse);

        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None))
        {
            r.IsSuccess.Should().BeTrue(r.IsFailure ? r.Error.Message : null);
            chunks.Add(r.Value);
        }

        chunks.Should().NotBeEmpty();
        chunks[^1].IsFinal.Should().BeTrue();
        chunks[^1].FinishReason.Should().Be(FinishReason.ToolCall);
        chunks.Should().OnlyContain(c => c.ToolCalls == null, "this adapter does not read native tool calls from a stream");
    }

    private static async Task<string> SendAsync(IReadOnlyList<Message> messages)
    {
        var (client, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(client);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"r","object":"chat.completion","created":0,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""");

        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest() with { Messages = messages }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        return body!;
    }
}
