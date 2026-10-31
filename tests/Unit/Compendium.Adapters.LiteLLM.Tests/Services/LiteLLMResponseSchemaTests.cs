// -----------------------------------------------------------------------
// <copyright file="LiteLLMResponseSchemaTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.LiteLLM.StructuredOutputs;
using Compendium.Adapters.LiteLLM.Tests.TestSupport;

namespace Compendium.Adapters.LiteLLM.Tests.Services;

/// <summary>The portable <see cref="CompletionRequest.ResponseSchema"/> on LiteLLM's <c>response_format.json_schema</c>.</summary>
public class LiteLLMResponseSchemaTests
{
    private const string Schema =
        """{"type":"object","properties":{"output":{"enum":["billing","none"]}},"required":["output"],"additionalProperties":false}""";

    private const string EmptyCompletion = """{"id":"x","model":"m","created":0,"choices":[]}""";

    private static readonly ResponseSchema Route = new() { Name = "route", SchemaJson = Schema };

    [Fact]
    public async Task CompleteAsync_WithResponseSchema_SendsItAsJsonSchema()
    {
        var (sut, handler) = Create();
        var body = Capture(handler, EmptyCompletion);

        var result = await sut.CompleteAsync(Request() with { ResponseSchema = Route with { Strict = false } });

        result.IsSuccess.Should().BeTrue();
        var format = JsonDocument.Parse(body()!).RootElement.GetProperty("response_format");
        format.GetProperty("type").GetString().Should().Be("json_schema");
        format.GetProperty("json_schema").GetProperty("name").GetString().Should().Be("route");
        format.GetProperty("json_schema").GetProperty("strict").GetBoolean().Should().BeFalse();
        format.GetProperty("json_schema").GetProperty("schema").GetRawText().Should().Be(Schema);
    }

    [Fact]
    public async Task StreamCompleteAsync_WithResponseSchema_SendsItAsJsonSchema()
    {
        var (sut, handler) = Create();
        var body = Capture(handler, "data: [DONE]\n\n", "text/event-stream");

        await foreach (var _ in sut.StreamCompleteAsync(Request() with { ResponseSchema = Route }))
        {
        }

        var format = JsonDocument.Parse(body()!).RootElement.GetProperty("response_format");
        format.GetProperty("type").GetString().Should().Be("json_schema");
        format.GetProperty("json_schema").GetProperty("strict").GetBoolean().Should().BeTrue();
        format.GetProperty("json_schema").GetProperty("schema").GetRawText().Should().Be(Schema);
    }

    [Fact]
    public async Task CompleteAsync_WithResponseSchemaAndTheLiteLLMKeys_SendsTheResponseSchema()
    {
        var (sut, handler) = Create(o => o.UseStructuredOutputsByDefault = true);
        var body = Capture(handler, EmptyCompletion);
        var request = Request()
            .WithStructuredOutput("""{"type":"object"}""", "legacy", strict: false)
            .WithJsonMode();
        request = request with { ResponseSchema = Route };

        await sut.CompleteAsync(request);

        var schema = JsonDocument.Parse(body()!).RootElement.GetProperty("response_format").GetProperty("json_schema");
        schema.GetProperty("name").GetString().Should().Be("route");
        schema.GetProperty("strict").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task CompleteAsync_WithoutResponseSchema_SendsNoResponseFormat()
    {
        var (sut, handler) = Create();
        var body = Capture(handler, EmptyCompletion);

        await sut.CompleteAsync(Request());

        JsonDocument.Parse(body()!).RootElement.TryGetProperty("response_format", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_WithAnInvalidResponseSchema_FailsBeforeAnyRequest()
    {
        var (sut, handler) = Create();
        var body = Capture(handler, EmptyCompletion);

        var result = await sut.CompleteAsync(Request() with { ResponseSchema = Route with { SchemaJson = "{oops" } });

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
        body().Should().BeNull();
    }

    [Fact]
    public async Task StreamCompleteAsync_WithAnInvalidResponseSchema_EndsWithOneFailureBeforeAnyRequest()
    {
        var (sut, handler) = Create();
        var body = Capture(handler, "data: [DONE]\n\n", "text/event-stream");

        var chunks = new List<Result<CompletionChunk>>();
        await foreach (var chunk in sut.StreamCompleteAsync(Request() with { ResponseSchema = Route with { SchemaJson = "[]" } }))
        {
            chunks.Add(chunk);
        }

        chunks.Should().ContainSingle().Which.Error.Code.Should().Be("AI.InvalidRequest");
        body().Should().BeNull();
    }

    [Fact]
    public void SupportsJsonSchema_IsTrueWhateverTheModel()
    {
        IAIProvider sut = Create().Provider;

        sut.SupportsJsonSchema("openai/gpt-4o").Should().BeTrue();
        sut.SupportsJsonSchema(string.Empty).Should().BeTrue();
    }

    private static CompletionRequest Request() => TestFactories.SimpleCompletionRequest("openai/gpt-4o");

    private static (LiteLLMAIProvider Provider, MockHttpMessageHandler Handler) Create(Action<LiteLLMOptions>? configure = null)
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient(configure);
        return (TestFactories.CreateProvider(httpClient, configure), handler);
    }

    private static Func<string?> Capture(MockHttpMessageHandler handler, string response, string mediaType = "application/json")
    {
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond(mediaType, response);
        return () => body;
    }
}
