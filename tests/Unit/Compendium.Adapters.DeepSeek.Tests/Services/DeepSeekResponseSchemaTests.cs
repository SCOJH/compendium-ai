// -----------------------------------------------------------------------
// <copyright file="DeepSeekResponseSchemaTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.DeepSeek.Tests.TestSupport;

namespace Compendium.Adapters.DeepSeek.Tests.Services;

/// <summary>
/// The portable <see cref="CompletionRequest.ResponseSchema"/> on DeepSeek, which has a JSON mode and no schema: the
/// request asks for JSON, without the schema, and a warning says so.
/// </summary>
public class DeepSeekResponseSchemaTests
{
    private const string Schema =
        """{"type":"object","properties":{"output":{"enum":["billing","none"]}},"required":["output"],"additionalProperties":false}""";

    private static readonly ResponseSchema Route = new() { Name = "route", SchemaJson = Schema };

    [Fact]
    public async Task CompleteAsync_WithResponseSchema_SendsJsonModeWithoutTheSchemaAndWarns()
    {
        var (sut, logger, body) = Create();

        var result = await sut.CompleteAsync(Request() with { ResponseSchema = Route });

        result.IsSuccess.Should().BeTrue();
        body().Should().NotContain("billing").And.NotContain("json_schema");
        JsonDocument.Parse(body()!).RootElement.GetProperty("response_format").GetRawText()
            .Should().Be("""{"type":"json_object"}""");
        logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("deepseek-chat").And.Contain("route").And.Contain("JSON mode");
    }

    [Fact]
    public async Task StreamCompleteAsync_WithResponseSchema_WarnsOnce()
    {
        var (sut, logger, body) = Create(stream: true);

        await foreach (var _ in sut.StreamCompleteAsync(Request() with { ResponseSchema = Route }))
        {
        }

        body().Should().NotContain("billing");
        logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle();
    }

    [Fact]
    public async Task CompleteAsync_WithAnInvalidResponseSchema_IgnoresItWithoutAnError()
    {
        var (sut, _, body) = Create();

        var result = await sut.CompleteAsync(Request() with { ResponseSchema = Route with { SchemaJson = "{oops" } });

        result.IsSuccess.Should().BeTrue();
        body().Should().NotBeNull();
    }

    [Fact]
    public async Task CompleteAsync_WithoutResponseSchema_SendsNoFormatAndDoesNotWarn()
    {
        var (sut, logger, body) = Create();

        await sut.CompleteAsync(Request());

        body().Should().NotContain("response_format");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void SupportsJsonSchema_IsFalse()
    {
        IAIProvider sut = Create().Provider;

        sut.SupportsJsonSchema("deepseek-chat").Should().BeFalse();
    }

    private static CompletionRequest Request() => TestFactories.SimpleCompletionRequest("deepseek-chat");

    private static (DeepSeekAIProvider Provider, RecordingLogger<DeepSeekAIProvider> Logger, Func<string?> Body) Create(bool stream = false)
    {
        var (client, handler) = TestFactories.CreateHttpClient();
        var logger = new RecordingLogger<DeepSeekAIProvider>();
        var provider = new DeepSeekAIProvider(client, Options.Create(TestFactories.DefaultOptions()), logger);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond(stream ? "text/event-stream" : "application/json", stream ? "data: [DONE]\n\n" : """{"id":"x","model":"m","created":0,"choices":[]}""");
        return (provider, logger, () => body);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

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
