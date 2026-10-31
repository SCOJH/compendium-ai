// -----------------------------------------------------------------------
// <copyright file="ZAiResponseSchemaTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.ZAi.Tests.TestSupport;

namespace Compendium.Adapters.ZAi.Tests.Services;

/// <summary>
/// The portable <see cref="CompletionRequest.ResponseSchema"/> on Z.ai, which this adapter does not ask to follow a schema:
/// the request goes without it, without an error, and a warning says so.
/// </summary>
public class ZAiResponseSchemaTests
{
    private const string Schema =
        """{"type":"object","properties":{"output":{"enum":["billing","none"]}},"required":["output"],"additionalProperties":false}""";

    private static readonly ResponseSchema Route = new() { Name = "route", SchemaJson = Schema };

    [Fact]
    public async Task CompleteAsync_WithResponseSchema_SendsTheRequestWithoutItAndWarns()
    {
        var (sut, logger, body) = Create();

        var result = await sut.CompleteAsync(Request() with { ResponseSchema = Route });

        result.IsSuccess.Should().BeTrue();
        body().Should().NotContain("billing").And.NotContain("json_schema");
        logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("glm-5.3").And.Contain("route").And.Contain("not sent");
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

        sut.SupportsJsonSchema("glm-5.3").Should().BeFalse();
    }

    private static CompletionRequest Request() => TestFactories.Request();

    private static (ZAiAIProvider Provider, RecordingLogger<ZAiAIProvider> Logger, Func<string?> Body) Create(bool stream = false)
    {
        var (client, handler) = TestFactories.CreateHttpClient();
        var logger = new RecordingLogger<ZAiAIProvider>();
        var provider = new ZAiAIProvider(client, Options.Create(TestFactories.DefaultOptions()), logger);
        string? body = null;
        handler.When(HttpMethod.Post, "https://api.z.ai/api/paas/v4/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond(stream ? "text/event-stream" : "application/json", stream ? "data: [DONE]\n\n" : """{"id":"g1","created":1790000000,"model":"glm-5.3","choices":[]}""");
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
