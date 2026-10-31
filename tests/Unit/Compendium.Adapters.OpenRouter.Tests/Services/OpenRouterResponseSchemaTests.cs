// -----------------------------------------------------------------------
// <copyright file="OpenRouterResponseSchemaTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.OpenRouter.Tests.TestSupport;

namespace Compendium.Adapters.OpenRouter.Tests.Services;

/// <summary>
/// The portable <see cref="CompletionRequest.ResponseSchema"/> on OpenRouter, which this adapter does not ask to follow a schema:
/// the request goes without it, without an error, and a warning says so.
/// </summary>
public class OpenRouterResponseSchemaTests
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
            .Which.Message.Should().Contain("anthropic/claude-3.5-sonnet").And.Contain("route").And.Contain("not sent");
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

        sut.SupportsJsonSchema("anthropic/claude-3.5-sonnet").Should().BeFalse();
    }

    private static CompletionRequest Request() => TestFactories.SimpleCompletionRequest();

    private static (OpenRouterAIProvider Provider, TestFactories.RecordingLogger<OpenRouterAIProvider> Logger, Func<string?> Body) Create(bool stream = false)
    {
        var (client, handler) = TestFactories.CreateHttpClient();
        var logger = new TestFactories.RecordingLogger<OpenRouterAIProvider>();
        var provider = new OpenRouterAIProvider(client, Options.Create(TestFactories.DefaultOptions()), logger);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond(stream ? "text/event-stream" : "application/json", stream ? "data: [DONE]\n\n" : """{"id":"x","model":"m","created":0,"choices":[]}""");
        return (provider, logger, () => body);
    }
}
