// -----------------------------------------------------------------------
// <copyright file="MistralHttpClientTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Mistral.Http;
using Compendium.Adapters.Mistral.Http.Models;
using Compendium.Adapters.Mistral.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Compendium.Adapters.Mistral.Tests.Http;

public class MistralHttpClientTests
{
    [Fact]
    public void Ctor_SetsBearerTokenAndAppendsV1ToBaseAddress()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var inner = new HttpClient(handler);
        var options = TestFactories.DefaultOptions(o => o.ApiKey = "abc123");

        // Act
        _ = new MistralHttpClient(inner, Options.Create(options), NullLogger<MistralHttpClient>.Instance);

        // Assert — Mistral exposes its REST surface under /v1/, so the http client appends it.
        inner.BaseAddress!.ToString().Should().Be("https://api.mistral.ai/v1/");
        inner.DefaultRequestHeaders.Authorization!.Scheme.Should().Be("Bearer");
        inner.DefaultRequestHeaders.Authorization!.Parameter.Should().Be("abc123");
    }

    [Fact]
    public void Ctor_DoesNotDoubleAppendV1_WhenBaseUrlAlreadyEndsInV1()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var inner = new HttpClient(handler);
        var options = TestFactories.DefaultOptions(o => o.BaseUrl = "https://eu-proxy.test/v1");

        // Act
        _ = new MistralHttpClient(inner, Options.Create(options), NullLogger<MistralHttpClient>.Instance);

        // Assert
        inner.BaseAddress!.ToString().Should().Be("https://eu-proxy.test/v1/");
    }

    [Fact]
    public void Ctor_DoesNotOverridePreSetBaseAddress()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var inner = new HttpClient(handler) { BaseAddress = new Uri("https://eu-proxy.test/v1/") };
        var options = TestFactories.DefaultOptions();

        // Act
        _ = new MistralHttpClient(inner, Options.Create(options), NullLogger<MistralHttpClient>.Instance);

        // Assert
        inner.BaseAddress!.ToString().Should().Be("https://eu-proxy.test/v1/");
    }

    [Fact]
    public void Ctor_SkipsAuthorizationHeader_WhenApiKeyEmpty()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var inner = new HttpClient(handler);
        var options = TestFactories.DefaultOptions(o => o.ApiKey = string.Empty);

        // Act
        _ = new MistralHttpClient(inner, Options.Create(options), NullLogger<MistralHttpClient>.Instance);

        // Assert — caller must override BaseUrl + ApiKey themselves; we don't ship a default.
        inner.DefaultRequestHeaders.Contains("Authorization").Should().BeFalse();
    }

    [Fact]
    public async Task CreateEmbeddingsAsync_OnInvalidJsonResponse_ReturnsProviderError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/embeddings").Respond("application/json", "not json");

        // Act
        var result = await client.CreateEmbeddingsAsync(
            new MistralEmbeddingsRequest { Model = "m", Input = new List<string> { "x" } },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
    }

    [Fact]
    public async Task CreateEmbeddingsAsync_OnHttpRequestException_ReturnsProviderError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/embeddings").Throw(new HttpRequestException("nope"));

        // Act
        var result = await client.CreateEmbeddingsAsync(
            new MistralEmbeddingsRequest { Model = "m", Input = new List<string> { "x" } },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("nope");
    }

    [Fact]
    public async Task CreateEmbeddingsAsync_OnTimeout_ReturnsTimeoutError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/embeddings").Throw(new TaskCanceledException("slow"));

        // Act
        var result = await client.CreateEmbeddingsAsync(
            new MistralEmbeddingsRequest { Model = "m", Input = new List<string> { "x" } },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.Timeout");
    }

    [Fact]
    public async Task CreateEmbeddingsAsync_WhenCallerCancels_RethrowsCancellation()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Post, "*/embeddings").Throw(new TaskCanceledException("c"));

        // Act
        var act = async () => await client.CreateEmbeddingsAsync(
            new MistralEmbeddingsRequest { Model = "m", Input = new List<string> { "x" } },
            cts.Token);

        // Assert
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task LogsRequestAndResponse_WhenEnableLoggingTrue()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var inner = new HttpClient(handler);
        var options = TestFactories.DefaultOptions(o => o.EnableLogging = true);
        var logger = new RecordingLogger<MistralHttpClient>();
        var client = new MistralHttpClient(inner, Options.Create(options), logger);

        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        // Act
        await client.CreateChatCompletionAsync(
            new MistralChatCompletionRequest
            {
                Model = "m",
                Messages = new List<MistralChatMessage> { new() { Role = "user", Content = "hi" } }
            },
            CancellationToken.None);

        // Assert
        logger.Entries.Should().Contain(e => e.Message.Contains("Mistral request:"));
        logger.Entries.Should().Contain(e => e.Message.Contains("Mistral response"));
    }

    [Fact]
    public async Task ParseErrorBody_HandlesMistralNativeShapeWithDetail()
    {
        // Arrange — Mistral's native error envelope: top-level "message" is an object with a "detail" string.
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(HttpStatusCode.UnprocessableEntity, "application/json",
                """{"message":{"detail":"invalid model id"},"type":"invalid_request","code":"E101"}""");

        // Act
        var result = await client.CreateChatCompletionAsync(
            new MistralChatCompletionRequest
            {
                Model = "m",
                Messages = new List<MistralChatMessage> { new() { Role = "user", Content = "hi" } }
            },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("invalid model id");
    }

    [Fact]
    public async Task ParseErrorBody_HandlesMistralNativeShapeWithStringMessage()
    {
        // Arrange — variant: top-level "message" is a plain string.
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(HttpStatusCode.BadRequest, "application/json",
                """{"message":"prompt too long","type":"invalid_request","code":"E102"}""");

        // Act
        var result = await client.CreateChatCompletionAsync(
            new MistralChatCompletionRequest
            {
                Model = "m",
                Messages = new List<MistralChatMessage> { new() { Role = "user", Content = "hi" } }
            },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("prompt too long");
    }

    [Fact]
    public async Task ParseErrorBody_HandlesMistralNativeShapeWithComplexMessageObject()
    {
        // Arrange — fallback: "message" is an object without a string "detail". We surface raw JSON.
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(HttpStatusCode.BadRequest, "application/json",
                """{"message":{"errors":["a","b"]},"code":"E103"}""");

        // Act
        var result = await client.CreateChatCompletionAsync(
            new MistralChatCompletionRequest
            {
                Model = "m",
                Messages = new List<MistralChatMessage> { new() { Role = "user", Content = "hi" } }
            },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("errors");
    }

    [Fact]
    public async Task ParseErrorBody_HandlesOpenAICompatErrorShape()
    {
        // Arrange — alternate shape: error envelope as { error: { message, code } }
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(HttpStatusCode.BadRequest, "application/json",
                """{"error":{"message":"bad request","code":"BR1","type":"invalid_request"}}""");

        // Act
        var result = await client.CreateChatCompletionAsync(
            new MistralChatCompletionRequest
            {
                Model = "m",
                Messages = new List<MistralChatMessage> { new() { Role = "user", Content = "hi" } }
            },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("bad request");
    }

    [Fact]
    public async Task ParseErrorBody_HandlesNonJsonBody()
    {
        // Arrange — last-resort fallback when JSON parsing fails: surface raw body.
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(HttpStatusCode.BadGateway, "text/plain", "upstream down");

        // Act
        var result = await client.CreateChatCompletionAsync(
            new MistralChatCompletionRequest
            {
                Model = "m",
                Messages = new List<MistralChatMessage> { new() { Role = "user", Content = "hi" } }
            },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("upstream down");
    }

    [Fact]
    public async Task ParseErrorBody_HandlesEmptyBody()
    {
        // Arrange — final fallback: even the body is blank.
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(HttpStatusCode.InternalServerError, "application/json", string.Empty);

        // Act
        var result = await client.CreateChatCompletionAsync(
            new MistralChatCompletionRequest
            {
                Model = "m",
                Messages = new List<MistralChatMessage> { new() { Role = "user", Content = "hi" } }
            },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        // Falls back to HttpStatusCode.ToString() — "InternalServerError".
        result.Error.Message.Should().Contain("InternalServerError");
    }

    [Fact]
    public async Task ListModelsAsync_OnHttpRequestException_ReturnsProviderError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Get, "*/models").Throw(new HttpRequestException("dns fail"));

        // Act
        var result = await client.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("dns fail");
    }

    [Fact]
    public async Task ListModelsAsync_WhenCallerCancels_RethrowsCancellation()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Get, "*/models").Throw(new TaskCanceledException("c"));

        // Act
        var act = async () => await client.ListModelsAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task StreamChatCompletionAsync_WhenTheReadFails_EndsWithStreamInterrupted()
    {
        // Arrange: one delta, then the connection drops.
        var (client, handler) = TestFactories.CreateHttpClient();
        var sse = "data: {\"id\":\"s\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n";
        handler.When(HttpMethod.Post, "*/chat/completions").Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CutStream(System.Text.Encoding.UTF8.GetBytes(sse))),
        });
        var done = false;

        // Act: read to the end, past the error.
        var results = new List<Result<MistralStreamChunk>>();
        await foreach (var r in client.StreamChatCompletionAsync(
            new MistralChatCompletionRequest { Model = "m", Messages = new List<MistralChatMessage>() },
            CancellationToken.None,
            () => done = true))
        {
            results.Add(r);
        }

        // Assert
        results.Should().HaveCount(2);
        results[0].IsSuccess.Should().BeTrue();
        results[1].IsFailure.Should().BeTrue();
        results[1].Error.Code.Should().Be("AI.StreamInterrupted");
        results[1].Error.Message.Should().Contain("The connection was reset.");
        done.Should().BeFalse("no [DONE] was read");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
