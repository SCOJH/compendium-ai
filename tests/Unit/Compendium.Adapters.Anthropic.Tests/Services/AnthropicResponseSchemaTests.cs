// -----------------------------------------------------------------------
// <copyright file="AnthropicResponseSchemaTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Anthropic.Tests.TestSupport;

namespace Compendium.Adapters.Anthropic.Tests.Services;

/// <summary>The portable <see cref="CompletionRequest.ResponseSchema"/> on Claude's <c>output_config.format</c>.</summary>
public class AnthropicResponseSchemaTests
{
    private const string Schema =
        """{"type":"object","properties":{"output":{"enum":["billing","none"]}},"required":["output"],"additionalProperties":false}""";

    private const string Message =
        """{"id":"m1","type":"message","role":"assistant","model":"claude-opus-4-7","content":[{"type":"text","text":"{\"output\":\"billing\"}"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""";

    private static readonly ResponseSchema Route = new() { Name = "route", SchemaJson = Schema, Strict = false };

    [Fact]
    public async Task CompleteAsync_WithResponseSchema_SendsItAsOutputConfigFormat()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "application/json", Message);

        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest("claude-sonnet-4-5-20250929") with { ResponseSchema = Route });

        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be("""{"output":"billing"}""");
        var format = JsonDocument.Parse(body()!).RootElement.GetProperty("output_config").GetProperty("format");
        format.GetProperty("type").GetString().Should().Be("json_schema");
        format.GetProperty("schema").GetRawText().Should().Be(Schema);
        format.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("type", "schema");
    }

    [Fact]
    public async Task StreamCompleteAsync_WithResponseSchema_SendsItAsOutputConfigFormat()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "text/event-stream", "data: {\"type\":\"message_stop\"}\n");

        await foreach (var _ in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest("claude-fable-5-1") with { ResponseSchema = Route }))
        {
        }

        JsonDocument.Parse(body()!).RootElement.GetProperty("output_config").GetProperty("format").GetProperty("schema")
            .GetRawText().Should().Be(Schema);
    }

    [Fact]
    public async Task CompleteAsync_WithResponseSchemaAndReasoning_KeepsTheEffortBesideTheFormat()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "application/json", Message);
        var request = TestFactories.SimpleCompletionRequest("claude-opus-4-7") with
        {
            ResponseSchema = Route,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
        };

        await sut.CompleteAsync(request);

        var config = JsonDocument.Parse(body()!).RootElement.GetProperty("output_config");
        config.GetProperty("effort").GetString().Should().Be("low");
        config.GetProperty("format").GetProperty("schema").GetRawText().Should().Be(Schema);
    }

    [Theory]
    [InlineData("claude-3-7-sonnet-latest")]
    [InlineData("claude-3-5-haiku-20241022")]
    [InlineData("claude-sonnet-4-20250514")]
    [InlineData("claude-opus-4-1")]
    public async Task CompleteAsync_OnAModelWithoutStructuredOutput_SendsTheRequestWithoutItAndWarns(string model)
    {
        var (sut, handler, logger) = Create();
        var body = Capture(handler, "application/json", Message);

        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(model) with { ResponseSchema = Route });

        result.IsSuccess.Should().BeTrue();
        JsonDocument.Parse(body()!).RootElement.TryGetProperty("output_config", out _).Should().BeFalse();
        logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain(model).And.Contain("route");
    }

    [Fact]
    public async Task CompleteAsync_WithAnInvalidResponseSchema_FailsBeforeAnyRequest()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "application/json", Message);

        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest("claude-opus-5-5") with { ResponseSchema = Route with { SchemaJson = "{oops" } });

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
        body().Should().BeNull();
    }

    [Fact]
    public async Task StreamCompleteAsync_WithAnInvalidResponseSchema_EndsWithOneFailureBeforeAnyRequest()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "text/event-stream", "data: {\"type\":\"message_stop\"}\n");

        var chunks = new List<Result<CompletionChunk>>();
        await foreach (var chunk in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest("claude-opus-5-5") with { ResponseSchema = Route with { SchemaJson = "[]" } }))
        {
            chunks.Add(chunk);
        }

        chunks.Should().ContainSingle().Which.Error.Code.Should().Be("AI.InvalidRequest");
        body().Should().BeNull();
    }

    [Theory]
    [InlineData("claude-sonnet-4-5", true)]
    [InlineData("claude-haiku-4-5-20251001", true)]
    [InlineData("claude-opus-4-5-20251101", true)]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-sonnet-5-5", true)]
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-fable-5-1", true)]
    [InlineData("claude-mythos-preview", true)]
    [InlineData("anthropic.claude-opus-4-7", true)]
    [InlineData("claude-opus-4-1", false)]
    [InlineData("claude-sonnet-4-20250514", false)]
    [InlineData("claude-3-7-sonnet-latest", false)]
    [InlineData("gpt-4o", false)]
    [InlineData("", false)]
    public void SupportsJsonSchema_ReadsTheModelId(string model, bool expected)
    {
        IAIProvider sut = Create().Provider;

        // An empty id is the default model, claude-3-7-sonnet-latest here.
        sut.SupportsJsonSchema(model).Should().Be(expected);
    }

    private static (AnthropicAIProvider Provider, MockHttpMessageHandler Handler, TestFactories.RecordingLogger<AnthropicAIProvider> Logger) Create()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var logger = new TestFactories.RecordingLogger<AnthropicAIProvider>();
        return (new AnthropicAIProvider(httpClient, Options.Create(TestFactories.DefaultOptions()), logger), handler, logger);
    }

    private static Func<string?> Capture(MockHttpMessageHandler handler, string mediaType, string response)
    {
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond(mediaType, response);
        return () => body;
    }
}
