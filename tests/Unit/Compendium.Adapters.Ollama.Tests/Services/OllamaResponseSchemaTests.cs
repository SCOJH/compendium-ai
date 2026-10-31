// -----------------------------------------------------------------------
// <copyright file="OllamaResponseSchemaTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Ollama.Tests.TestSupport;

namespace Compendium.Adapters.Ollama.Tests.Services;

/// <summary>The portable <see cref="CompletionRequest.ResponseSchema"/> on Ollama's <c>format</c>.</summary>
public class OllamaResponseSchemaTests
{
    private const string Schema =
        """{"type":"object","properties":{"output":{"enum":["billing","none"]}},"required":["output"],"additionalProperties":false}""";

    private const string Done = """{"model":"llama3.2","message":{"role":"assistant","content":"{}"},"done":true,"done_reason":"stop"}""";

    private static readonly ResponseSchema Route = new() { Name = "route", SchemaJson = Schema, Strict = false };

    [Fact]
    public async Task CompleteAsync_WithResponseSchema_SendsItAsFormat()
    {
        var (sut, handler) = TestFactories.CreateProviderWithHandler();
        var body = Capture(handler, "application/json");

        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest() with { ResponseSchema = Route });

        result.IsSuccess.Should().BeTrue();
        JsonDocument.Parse(body()!).RootElement.GetProperty("format").GetRawText().Should().Be(Schema);
    }

    [Fact]
    public async Task StreamCompleteAsync_WithResponseSchema_SendsItAsFormat()
    {
        var (sut, handler) = TestFactories.CreateProviderWithHandler();
        var body = Capture(handler, "application/x-ndjson");

        await foreach (var _ in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest() with { ResponseSchema = Route }))
        {
        }

        JsonDocument.Parse(body()!).RootElement.GetProperty("format").GetRawText().Should().Be(Schema);
    }

    [Fact]
    public async Task CompleteAsync_WithoutResponseSchema_SendsNoFormat()
    {
        var (sut, handler) = TestFactories.CreateProviderWithHandler();
        var body = Capture(handler, "application/json");

        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest());

        JsonDocument.Parse(body()!).RootElement.TryGetProperty("format", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_WithAnInvalidResponseSchema_FailsBeforeAnyRequest()
    {
        var (sut, handler) = TestFactories.CreateProviderWithHandler();
        var body = Capture(handler, "application/json");

        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest() with { ResponseSchema = Route with { SchemaJson = "1" } });

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
        body().Should().BeNull();
    }

    [Fact]
    public async Task StreamCompleteAsync_WithAnInvalidResponseSchema_EndsWithOneFailureBeforeAnyRequest()
    {
        var (sut, handler) = TestFactories.CreateProviderWithHandler();
        var body = Capture(handler, "application/x-ndjson");

        var chunks = new List<Result<CompletionChunk>>();
        await foreach (var chunk in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest() with { ResponseSchema = Route with { SchemaJson = "{" } }))
        {
            chunks.Add(chunk);
        }

        chunks.Should().ContainSingle().Which.Error.Code.Should().Be("AI.InvalidRequest");
        body().Should().BeNull();
    }

    [Fact]
    public void SupportsJsonSchema_IsTrueWhateverTheModel()
    {
        IAIProvider sut = TestFactories.CreateProviderWithHandler().Provider;

        sut.SupportsJsonSchema("llama3.2").Should().BeTrue();
        sut.SupportsJsonSchema(string.Empty).Should().BeTrue();
    }

    private static Func<string?> Capture(MockHttpMessageHandler handler, string mediaType)
    {
        string? body = null;
        handler.When(HttpMethod.Post, "*/api/chat")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond(mediaType, Done);
        return () => body;
    }
}
