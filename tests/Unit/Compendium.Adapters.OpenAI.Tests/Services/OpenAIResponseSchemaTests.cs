// -----------------------------------------------------------------------
// <copyright file="OpenAIResponseSchemaTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.OpenAI.Http;
using Compendium.Adapters.OpenAI.StructuredOutputs;
using Compendium.Adapters.OpenAI.Tests.TestSupport;

namespace Compendium.Adapters.OpenAI.Tests.Services;

/// <summary>The portable <see cref="CompletionRequest.ResponseSchema"/> on OpenAI's <c>response_format</c>.</summary>
public class OpenAIResponseSchemaTests
{
    private const string Schema =
        """{"type":"object","properties":{"output":{"enum":["billing","none"]}},"required":["output"],"additionalProperties":false}""";

    private const string EmptyCompletion = """{"id":"x","model":"m","created":0,"choices":[]}""";

    private static readonly ResponseSchema Route = new() { Name = "route", SchemaJson = Schema };

    [Fact]
    public async Task CompleteAsync_WithResponseSchema_SendsItAsJsonSchema()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "*/chat/completions", EmptyCompletion);

        var result = await sut.CompleteAsync(Request("gpt-4o-mini") with { ResponseSchema = Route with { Strict = false } });

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
        var (sut, handler, _) = Create();
        var body = Capture(handler, "*/chat/completions", "data: [DONE]\n\n", "text/event-stream");

        await foreach (var _ in sut.StreamCompleteAsync(Request("gpt-5") with { ResponseSchema = Route }))
        {
        }

        var format = JsonDocument.Parse(body()!).RootElement.GetProperty("response_format");
        format.GetProperty("type").GetString().Should().Be("json_schema");
        format.GetProperty("json_schema").GetProperty("strict").GetBoolean().Should().BeTrue();
        format.GetProperty("json_schema").GetProperty("schema").GetRawText().Should().Be(Schema);
    }

    [Fact]
    public async Task CompleteAsync_WithResponseSchemaAndTheOpenAIKeys_SendsTheResponseSchema()
    {
        var (sut, handler, _) = Create(o => o.UseStructuredOutputsByDefault = true);
        var body = Capture(handler, "*/chat/completions", EmptyCompletion);
        var request = Request("gpt-4o-mini")
            .WithStructuredOutput("""{"type":"object"}""", "legacy", strict: false)
            .WithJsonMode();
        request = request with { ResponseSchema = Route };

        await sut.CompleteAsync(request);

        var schema = JsonDocument.Parse(body()!).RootElement.GetProperty("response_format").GetProperty("json_schema");
        schema.GetProperty("name").GetString().Should().Be("route");
        schema.GetProperty("strict").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("gpt-3.5-turbo")]
    [InlineData("gpt-4")]
    [InlineData("gpt-4-turbo")]
    [InlineData("gpt-4o-2024-05-13")]
    [InlineData("o1-mini")]
    public async Task CompleteAsync_OnAModelWithoutJsonSchema_SendsTheRequestWithoutItAndWarns(string model)
    {
        var (sut, handler, logger) = Create();
        var body = Capture(handler, "*/chat/completions", EmptyCompletion);

        var result = await sut.CompleteAsync(Request(model) with { ResponseSchema = Route });

        result.IsSuccess.Should().BeTrue();
        JsonDocument.Parse(body()!).RootElement.TryGetProperty("response_format", out _).Should().BeFalse();
        logger.Warnings.Should().ContainSingle().Which.Should().Contain(model).And.Contain("route").And.Contain("not sent");
    }

    [Fact]
    public async Task CompleteAsync_OnAModelWithoutJsonSchema_KeepsTheOpenAIKeys()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "*/chat/completions", EmptyCompletion);

        await sut.CompleteAsync(Request("gpt-3.5-turbo").WithJsonMode() with { ResponseSchema = Route });

        JsonDocument.Parse(body()!).RootElement.GetProperty("response_format").GetProperty("type").GetString()
            .Should().Be("json_object");
    }

    [Fact]
    public async Task CompleteAsync_WithAnInvalidResponseSchema_FailsBeforeAnyRequest()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "*/chat/completions", EmptyCompletion);

        var result = await sut.CompleteAsync(Request("gpt-4o") with { ResponseSchema = Route with { SchemaJson = "{oops" } });

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
        body().Should().BeNull();
    }

    [Fact]
    public async Task StreamCompleteAsync_WithAnInvalidResponseSchema_EndsWithOneFailureBeforeAnyRequest()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "*/chat/completions", "data: [DONE]\n\n", "text/event-stream");

        var chunks = new List<Result<CompletionChunk>>();
        await foreach (var chunk in sut.StreamCompleteAsync(Request("gpt-4o") with { ResponseSchema = Route with { Name = "a b" } }))
        {
            chunks.Add(chunk);
        }

        chunks.Should().ContainSingle().Which.Error.Code.Should().Be("AI.InvalidRequest");
        body().Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_WithAnInvalidResponseSchemaOnAModelWithoutJsonSchema_IgnoresIt()
    {
        var (sut, handler, _) = Create();
        var body = Capture(handler, "*/chat/completions", EmptyCompletion);

        var result = await sut.CompleteAsync(Request("gpt-3.5-turbo") with { ResponseSchema = Route with { SchemaJson = "[]" } });

        result.IsSuccess.Should().BeTrue();
        body().Should().NotBeNull();
    }

    [Theory]
    [InlineData("gpt-4o", true)]
    [InlineData("gpt-4o-mini", true)]
    [InlineData("gpt-4o-2024-08-06", true)]
    [InlineData("gpt-4.1", true)]
    [InlineData("gpt-5", true)]
    [InlineData("o1", true)]
    [InlineData("o3-mini", true)]
    [InlineData("", true)]
    [InlineData("GPT-3.5-TURBO", false)]
    [InlineData("gpt-4", false)]
    [InlineData("gpt-4-0613", false)]
    [InlineData("gpt-4o-2024-05-13", false)]
    [InlineData("o1-preview", false)]
    [InlineData("chatgpt-4o-latest", false)]
    [InlineData("text-embedding-3-small", false)]
    [InlineData("whisper-1", false)]
    public void SupportsJsonSchema_ReadsTheModelId(string model, bool expected)
    {
        IAIProvider sut = Create().Provider;

        sut.SupportsJsonSchema(model).Should().Be(expected);
    }

    private static CompletionRequest Request(string model) => TestFactories.SimpleCompletionRequest(model);

    private static (OpenAIAIProvider Provider, MockHttpMessageHandler Handler, RecordingLogger<OpenAIAIProvider> Logger) Create(
        Action<OpenAIOptions>? configure = null)
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient(configure);
        var logger = new RecordingLogger<OpenAIAIProvider>();
        var provider = new OpenAIAIProvider(httpClient, Options.Create(TestFactories.DefaultOptions(configure)), logger);
        return (provider, handler, logger);
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
