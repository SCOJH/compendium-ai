using System.Text.Json;
using Compendium.Abstractions.AI.Models;
using AwesomeAssertions;

namespace Compendium.Abstractions.AI.Tests.Models;

/// <summary>
/// Native tool calls in the abstraction: <see cref="ToolCall"/>, <see cref="Message.ToolCalls"/> /
/// <see cref="Message.ToolCallId"/>, <see cref="CompletionResponse.ToolCalls"/>, <see cref="CompletionChunk.ToolCalls"/>.
/// All optional: unset, every record is the one it was, down to its JSON.
/// </summary>
public class ToolCallTests
{
    private static readonly ToolCall Call = new() { Id = "D681PevKs", Name = "retrieve_payment_status", ArgumentsJson = "{}" };

    [Fact]
    public void TheMessagesAlreadyThere_CarryNoCallAndNoCallId()
    {
        foreach (var message in new[] { Message.System("s"), Message.User("u"), Message.Assistant("a"), new Message { Role = MessageRole.Tool, Content = "t" } })
        {
            message.ToolCalls.Should().BeNull();
            message.ToolCallId.Should().BeNull();
        }
    }

    [Fact]
    public void AnAssistantMessage_CanCarryTheCallsOfItsTurn()
    {
        var calls = new[] { Call };

        var message = Message.Assistant(string.Empty, calls);

        message.Role.Should().Be(MessageRole.Assistant);
        message.Content.Should().BeEmpty();
        message.ToolCalls.Should().BeSameAs(calls);
        message.ToolCallId.Should().BeNull();
        message.Name.Should().BeNull();
    }

    [Fact]
    public void AnAssistantMessage_WithNoCallList_IsRefused()
    {
        var act = () => Message.Assistant("text", null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("toolCalls");
    }

    [Fact]
    public void AToolResult_AnswersACallByItsId()
    {
        var message = Message.ToolResult("D681PevKs", "{\"status\":\"Paid\"}");

        message.Role.Should().Be(MessageRole.Tool);
        message.Content.Should().Be("{\"status\":\"Paid\"}");
        message.ToolCallId.Should().Be("D681PevKs");
        message.ToolCalls.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AToolResult_WithoutACallId_IsRefused(string? id)
    {
        var act = () => Message.ToolResult(id!, "result");

        act.Should().Throw<ArgumentException>().WithParameterName("toolCallId");
    }

    [Fact]
    public void ToolCalls_AreValues()
    {
        var same = new ToolCall { Id = "D681PevKs", Name = "retrieve_payment_status", ArgumentsJson = "{}" };

        same.Should().Be(Call);
        (same with { ArgumentsJson = "{\"a\":1}" }).Should().NotBe(Call);
    }

    [Fact]
    public void AMessageWithoutTheNewFields_HasTheJsonItHadBefore()
    {
        var json = JsonSerializer.Serialize(new Message { Role = MessageRole.User, Content = "Hi", Name = "alice" });

        json.Should().Be("""{"Role":"User","Content":"Hi","Name":"alice"}""");
    }

    [Fact]
    public void AMessageWithTheNewFields_WritesThem()
    {
        var assistant = JsonSerializer.Serialize(Message.Assistant(string.Empty, new[] { Call }));
        var tool = JsonSerializer.Serialize(Message.ToolResult("D681PevKs", "done"));

        assistant.Should().Be("""{"Role":"Assistant","Content":"","Name":null,"ToolCalls":[{"Id":"D681PevKs","Name":"retrieve_payment_status","ArgumentsJson":"{}"}]}""");
        tool.Should().Be("""{"Role":"Tool","Content":"done","Name":null,"ToolCallId":"D681PevKs"}""");
    }

    [Fact]
    public void AResponseAndAChunkWithoutCalls_HaveTheJsonTheyHadBefore()
    {
        var created = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
        var response = JsonSerializer.Serialize(new CompletionResponse
        {
            Id = "r",
            Model = "m",
            Content = "Paid.",
            FinishReason = FinishReason.Stop,
            Usage = new UsageStats { PromptTokens = 1, CompletionTokens = 2 },
            CreatedAt = created,
        });
        var chunk = JsonSerializer.Serialize(new CompletionChunk { Id = "c", ContentDelta = "x", Index = 3, IsFinal = true, FinishReason = FinishReason.Stop });

        response.Should().Be("""{"Id":"r","Model":"m","Content":"Paid.","FinishReason":"Stop","Reasoning":null,"Usage":{"PromptTokens":1,"CompletionTokens":2,"TotalTokens":3,"ReasoningTokens":null,"EstimatedCostUsd":null},"CreatedAt":"2026-10-21T00:00:00Z","Metadata":null}""");
        chunk.Should().Be("""{"Id":"c","ContentDelta":"x","ReasoningDelta":null,"Index":3,"IsFinal":true,"FinishReason":"Stop","Usage":null}""");
    }

    [Fact]
    public void AResponseAndAChunkWithCalls_CarryAndWriteThem()
    {
        var calls = new[] { Call };
        var response = new CompletionResponse
        {
            Id = "r",
            Model = "m",
            Content = string.Empty,
            FinishReason = FinishReason.ToolCall,
            Usage = new UsageStats { PromptTokens = 1, CompletionTokens = 2 },
            ToolCalls = calls,
        };
        var chunk = new CompletionChunk { Id = "c", ContentDelta = string.Empty, IsFinal = true, FinishReason = FinishReason.ToolCall, ToolCalls = calls };

        response.ToolCalls.Should().BeSameAs(calls);
        chunk.ToolCalls.Should().BeSameAs(calls);
        JsonSerializer.Serialize(response).Should().Contain("""
            "Reasoning":null,"ToolCalls":[{"Id":"D681PevKs","Name":"retrieve_payment_status","ArgumentsJson":"{}"}],"Usage"
            """);
        JsonSerializer.Serialize(chunk).Should().EndWith("""
            "Usage":null,"ToolCalls":[{"Id":"D681PevKs","Name":"retrieve_payment_status","ArgumentsJson":"{}"}]}
            """);
    }

    [Fact]
    public void ARecordWithNoCall_HasNone()
    {
        new CompletionResponse
        {
            Id = "r",
            Model = "m",
            Content = "c",
            FinishReason = FinishReason.Stop,
            Usage = new UsageStats { PromptTokens = 0, CompletionTokens = 0 },
        }.ToolCalls.Should().BeNull();
        new CompletionChunk { Id = "c", ContentDelta = "x" }.ToolCalls.Should().BeNull();
    }
}
