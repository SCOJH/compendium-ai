// -----------------------------------------------------------------------
// <copyright file="GeminiResponseSchemaTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Gemini.StructuredOutputs;
using Compendium.Adapters.Gemini.Tests.TestSupport;

namespace Compendium.Adapters.Gemini.Tests.Services;

/// <summary>The portable <see cref="CompletionRequest.ResponseSchema"/> on Gemini's <c>responseJsonSchema</c>.</summary>
public class GeminiResponseSchemaTests
{
    private const string Schema =
        """{"type":"object","properties":{"output":{"enum":["billing","none"]}},"required":["output"],"additionalProperties":false}""";

    private static readonly ResponseSchema Route = new() { Name = "route", SchemaJson = Schema };

    [Fact]
    public async Task CompleteAsync_WithResponseSchema_SendsItAsResponseJsonSchema()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "*:generateContent*", """{"candidates":[]}""");

        var result = await sut.CompleteAsync(Request("gemini-2.5-flash") with { ResponseSchema = Route });

        result.IsSuccess.Should().BeTrue();
        var config = JsonDocument.Parse(body()!).RootElement.GetProperty("generationConfig");
        config.GetProperty("responseMimeType").GetString().Should().Be("application/json");
        config.GetProperty("responseJsonSchema").GetRawText().Should().Be(Schema);
        config.TryGetProperty("responseSchema", out _).Should().BeFalse("responseJsonSchema must go without responseSchema");
    }

    [Fact]
    public async Task StreamCompleteAsync_WithResponseSchema_SendsItAsResponseJsonSchema()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "*:streamGenerateContent*", "data: [DONE]\n\n", "text/event-stream");

        await foreach (var _ in sut.StreamCompleteAsync(Request("models/gemini-3-pro") with { ResponseSchema = Route }))
        {
        }

        JsonDocument.Parse(body()!).RootElement.GetProperty("generationConfig").GetProperty("responseJsonSchema")
            .GetRawText().Should().Be(Schema);
    }

    [Fact]
    public async Task CompleteAsync_WithResponseSchemaAndTheGeminiKey_SendsTheResponseSchemaAlone()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "*:generateContent*", """{"candidates":[]}""");

        await sut.CompleteAsync(Request("gemini-2.0-flash").WithStructuredOutput("""{"type":"OBJECT"}""") with { ResponseSchema = Route });

        var config = JsonDocument.Parse(body()!).RootElement.GetProperty("generationConfig");
        config.GetProperty("responseJsonSchema").GetRawText().Should().Be(Schema);
        config.TryGetProperty("responseSchema", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("gemini-1.5-pro")]
    [InlineData("gemini-pro")]
    [InlineData("gemma-3-27b-it")]
    public async Task CompleteAsync_OnAModelWithoutJsonSchema_SendsTheRequestWithoutItAndWarns(string model)
    {
        var (sut, handler, logger) = Create();
        var body = Capture(handler, "*:generateContent*", """{"candidates":[]}""");

        var result = await sut.CompleteAsync(Request(model) with { ResponseSchema = Route });

        result.IsSuccess.Should().BeTrue();
        var config = JsonDocument.Parse(body()!).RootElement.GetProperty("generationConfig");
        config.TryGetProperty("responseJsonSchema", out _).Should().BeFalse();
        config.TryGetProperty("responseMimeType", out _).Should().BeFalse();
        logger.Warnings.Should().ContainSingle().Which.Should().Contain(model).And.Contain("route");
    }

    [Fact]
    public async Task CompleteAsync_WithAnInvalidResponseSchema_FailsBeforeAnyRequest()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "*:generateContent*", """{"candidates":[]}""");

        var result = await sut.CompleteAsync(Request("gemini-2.5-pro") with { ResponseSchema = Route with { SchemaJson = "{oops" } });

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
        body().Should().BeNull();
    }

    [Fact]
    public async Task StreamCompleteAsync_WithAnInvalidResponseSchema_EndsWithOneFailureBeforeAnyRequest()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "*:streamGenerateContent*", "data: [DONE]\n\n", "text/event-stream");

        var chunks = new List<Result<CompletionChunk>>();
        await foreach (var chunk in sut.StreamCompleteAsync(Request("gemini-2.5-pro") with { ResponseSchema = Route with { Name = "" } }))
        {
            chunks.Add(chunk);
        }

        chunks.Should().ContainSingle().Which.Error.Code.Should().Be("AI.InvalidRequest");
        body().Should().BeNull();
    }

    [Theory]
    [InlineData("gemini-2.0-flash", true)]
    [InlineData("models/gemini-2.5-pro", true)]
    [InlineData("gemini-3-pro-preview", true)]
    [InlineData("", true)]
    [InlineData("gemini-1.5-flash", false)]
    [InlineData("models/gemini-1.0-pro", false)]
    [InlineData("gemini-pro-vision", false)]
    [InlineData("gemma-3-12b-it", false)]
    [InlineData("text-embedding-004", false)]
    public void SupportsJsonSchema_ReadsTheModelId(string model, bool expected)
    {
        IAIProvider sut = Create().Provider;

        sut.SupportsJsonSchema(model).Should().Be(expected);
    }

    private static CompletionRequest Request(string model) => TestFactories.SimpleCompletionRequest(model);

    private static (GeminiAIProvider Provider, MockHttpMessageHandler Handler, RecordingLogger<GeminiAIProvider> Logger) Create()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var logger = new RecordingLogger<GeminiAIProvider>();
        return (new GeminiAIProvider(httpClient, Options.Create(TestFactories.DefaultOptions()), logger), handler, logger);
    }

    private static Func<string?> Capture(MockHttpMessageHandler handler, string url, string response, string mediaType = "application/json")
    {
        string? body = null;
        handler.When(HttpMethod.Post, url)
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond(mediaType, response);
        return () => body;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IEnumerable<string> Warnings => Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
