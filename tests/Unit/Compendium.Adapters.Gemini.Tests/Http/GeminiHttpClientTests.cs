// -----------------------------------------------------------------------
// <copyright file="GeminiHttpClientTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Gemini.Http;
using Compendium.Adapters.Gemini.Http.Models;
using Compendium.Adapters.Gemini.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Compendium.Adapters.Gemini.Tests.Http;

public class GeminiHttpClientTests
{
    [Fact]
    public void Ctor_SetsBaseAddress()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var inner = new HttpClient(handler);
        var options = TestFactories.DefaultOptions();

        // Act
        var sut = new GeminiHttpClient(inner, Options.Create(options), NullLogger<GeminiHttpClient>.Instance);

        // Assert
        sut.Should().NotBeNull();
        inner.BaseAddress!.ToString().Should().StartWith("https://generativelanguage.googleapis.com");
        // Gemini doesn't use an Authorization header; auth is via the ?key= query param.
        inner.DefaultRequestHeaders.Authorization.Should().BeNull();
    }

    [Fact]
    public void Ctor_DoesNotOverridePreSetBaseAddress()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var inner = new HttpClient(handler) { BaseAddress = new Uri("https://proxy.test/v1/") };
        var options = TestFactories.DefaultOptions();

        // Act
        _ = new GeminiHttpClient(inner, Options.Create(options), NullLogger<GeminiHttpClient>.Instance);

        // Assert
        inner.BaseAddress!.ToString().Should().Be("https://proxy.test/v1/");
    }

    [Fact]
    public async Task GenerateContentAsync_AppendsApiKeyInQuery()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient(o => o.ApiKey = "k-secret");
        string? capturedUri = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { capturedUri = req.RequestUri!.ToString(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        // Act
        var result = await client.GenerateContentAsync(
            "gemini-2.0-flash",
            new GeminiGenerateContentRequest { Contents = new List<GeminiContent>() },
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        capturedUri.Should().NotBeNull();
        capturedUri!.Should().Contain("v1beta/models/gemini-2.0-flash:generateContent");
        capturedUri.Should().Contain("key=k-secret");
    }

    [Fact]
    public async Task GenerateContentAsync_StripsModelsPrefixFromIdentifier()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        string? capturedUri = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { capturedUri = req.RequestUri!.ToString(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        // Act
        await client.GenerateContentAsync(
            "models/gemini-1.5-pro",
            new GeminiGenerateContentRequest { Contents = new List<GeminiContent>() },
            CancellationToken.None);

        // Assert — only one "/models/" segment in the URI, even though caller passed "models/…".
        capturedUri.Should().Contain("/v1beta/models/gemini-1.5-pro:generateContent");
        capturedUri.Should().NotContain("/models/models/");
    }

    [Fact]
    public async Task GenerateContentAsync_OnTimeout_ReturnsTimeoutError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*:generateContent*")
            .Throw(new TaskCanceledException("slow"));

        // Act
        var result = await client.GenerateContentAsync(
            "gemini-2.0-flash",
            new GeminiGenerateContentRequest { Contents = new List<GeminiContent>() },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.Timeout");
    }

    [Fact]
    public async Task GenerateContentAsync_WhenCallerCancels_RethrowsCancellation()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Post, "*:generateContent*").Throw(new TaskCanceledException("c"));

        // Act
        var act = async () => await client.GenerateContentAsync(
            "gemini-2.0-flash",
            new GeminiGenerateContentRequest { Contents = new List<GeminiContent>() },
            cts.Token);

        // Assert
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task GenerateContentAsync_OnHttpRequestException_ReturnsProviderError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*:generateContent*").Throw(new HttpRequestException("dead"));

        // Act
        var result = await client.GenerateContentAsync(
            "gemini-2.0-flash",
            new GeminiGenerateContentRequest { Contents = new List<GeminiContent>() },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("dead");
    }

    [Fact]
    public async Task GenerateContentAsync_OnInvalidJsonResponse_ReturnsProviderError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*:generateContent*")
            .Respond("application/json", "not json");

        // Act
        var result = await client.GenerateContentAsync(
            "gemini-2.0-flash",
            new GeminiGenerateContentRequest { Contents = new List<GeminiContent>() },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
    }

    [Fact]
    public async Task GenerateContentAsync_OnEmptyBody_ReturnsProviderError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*:generateContent*")
            .Respond("application/json", "null");

        // Act
        var result = await client.GenerateContentAsync(
            "gemini-2.0-flash",
            new GeminiGenerateContentRequest { Contents = new List<GeminiContent>() },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
    }

    [Fact]
    public async Task EmbedContentAsync_OnTimeout_ReturnsTimeoutError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*:embedContent*").Throw(new TaskCanceledException("slow"));

        // Act
        var result = await client.EmbedContentAsync(
            "text-embedding-004",
            new GeminiEmbedContentRequest
            {
                Model = "models/text-embedding-004",
                Content = new GeminiContent { Parts = new List<GeminiPart> { new() { Text = "x" } } }
            },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.Timeout");
    }

    [Fact]
    public async Task EmbedContentAsync_OnHttpRequestException_ReturnsProviderError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*:embedContent*").Throw(new HttpRequestException("nope"));

        // Act
        var result = await client.EmbedContentAsync(
            "text-embedding-004",
            new GeminiEmbedContentRequest
            {
                Model = "models/text-embedding-004",
                Content = new GeminiContent { Parts = new List<GeminiPart> { new() { Text = "x" } } }
            },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
    }

    [Fact]
    public async Task EmbedContentAsync_WhenCallerCancels_RethrowsCancellation()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Post, "*:embedContent*").Throw(new TaskCanceledException("c"));

        // Act
        var act = async () => await client.EmbedContentAsync(
            "text-embedding-004",
            new GeminiEmbedContentRequest
            {
                Model = "models/text-embedding-004",
                Content = new GeminiContent { Parts = new List<GeminiPart> { new() { Text = "x" } } }
            },
            cts.Token);

        // Assert
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task BatchEmbedContentsAsync_OnTimeout_ReturnsTimeoutError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*:batchEmbedContents*").Throw(new TaskCanceledException("slow"));

        // Act
        var result = await client.BatchEmbedContentsAsync(
            "text-embedding-004",
            new GeminiBatchEmbedContentsRequest { Requests = new List<GeminiEmbedContentRequest>() },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.Timeout");
    }

    [Fact]
    public async Task BatchEmbedContentsAsync_OnHttpRequestException_ReturnsProviderError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*:batchEmbedContents*").Throw(new HttpRequestException("kaput"));

        // Act
        var result = await client.BatchEmbedContentsAsync(
            "text-embedding-004",
            new GeminiBatchEmbedContentsRequest { Requests = new List<GeminiEmbedContentRequest>() },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
    }

    [Fact]
    public async Task BatchEmbedContentsAsync_WhenCallerCancels_RethrowsCancellation()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Post, "*:batchEmbedContents*").Throw(new TaskCanceledException("c"));

        // Act
        var act = async () => await client.BatchEmbedContentsAsync(
            "text-embedding-004",
            new GeminiBatchEmbedContentsRequest { Requests = new List<GeminiEmbedContentRequest>() },
            cts.Token);

        // Assert
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task ListModelsAsync_ReadsEveryPage()
    {
        // Arrange — Google pages the list (50 per page by default): the second page follows nextPageToken.
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.Expect(HttpMethod.Get, "*/v1beta/models")
            .WithExactQueryString($"pageSize=1000&key={TestFactories.DefaultApiKey}")
            .Respond("application/json", """{"models":[{"name":"models/a"},{"name":"models/b"}],"nextPageToken":"t2"}""");
        handler.Expect(HttpMethod.Get, "*/v1beta/models")
            .WithExactQueryString($"pageSize=1000&key={TestFactories.DefaultApiKey}&pageToken=t2")
            .Respond("application/json", """{"models":[{"name":"models/c"}]}""");

        // Act
        var result = await client.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Select(m => m.Name).Should().Equal("models/a", "models/b", "models/c");
        handler.VerifyNoOutstandingExpectation();
    }

    [Fact(Timeout = 5000)]
    public async Task ListModelsAsync_WhenTheTokenRepeats_Fails()
    {
        // Arrange — every page points at the same next page.
        var (client, handler) = TestFactories.CreateHttpClient();
        var request = handler.When(HttpMethod.Get, "*/models*")
            .Respond("application/json", """{"models":[{"name":"models/a"}],"nextPageToken":"t1"}""");

        // Act
        var result = await client.ListModelsAsync(CancellationToken.None);

        // Assert — the token repeats on the second page: stop there, with no partial list.
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("pagination did not advance");
        handler.GetMatchCount(request).Should().Be(2);
    }

    [Fact(Timeout = 5000)]
    public async Task ListModelsAsync_StopsAfterMaxPages()
    {
        // Arrange — a list whose pages never end, each with a new token.
        var (client, handler) = TestFactories.CreateHttpClient();
        var served = 0;
        var request = handler.When(HttpMethod.Get, "*/models*")
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"models":[],"nextPageToken":"t{{Interlocked.Increment(ref served)}}"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });

        // Act
        var result = await client.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        handler.GetMatchCount(request).Should().Be(GeminiHttpClient.MaxModelPages);
    }

    [Fact]
    public async Task ListModelsAsync_WhenALaterPageFails_ReturnsItsError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.Expect(HttpMethod.Get, "*/models*")
            .Respond("application/json", """{"models":[{"name":"models/a"}],"nextPageToken":"t2"}""");
        handler.Expect(HttpMethod.Get, "*/models*")
            .Respond(HttpStatusCode.InternalServerError, "application/json", """{"error":{"code":500,"message":"backend down","status":"INTERNAL"}}""");

        // Act
        var result = await client.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("backend down");
    }

    [Fact]
    public async Task ListModelsAsync_OnException_ReturnsProviderError()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Get, "*/models*").Throw(new HttpRequestException("dead"));

        // Act
        var result = await client.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
    }

    [Fact]
    public async Task ListModelsAsync_WhenCallerCancels_RethrowsCancellation()
    {
        // Arrange
        var (client, handler) = TestFactories.CreateHttpClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Get, "*/models*").Throw(new TaskCanceledException("c"));

        // Act
        var act = async () => await client.ListModelsAsync(cts.Token);

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
        var logger = new RecordingLogger<GeminiHttpClient>();
        var client = new GeminiHttpClient(inner, Options.Create(options), logger);

        handler.When(HttpMethod.Post, "*:generateContent*")
            .Respond("application/json", """{"candidates":[]}""");

        // Act
        await client.GenerateContentAsync(
            "gemini-2.0-flash",
            new GeminiGenerateContentRequest { Contents = new List<GeminiContent>() },
            CancellationToken.None);

        // Assert
        logger.Entries.Should().Contain(e => e.Message.Contains("Gemini request:"));
        logger.Entries.Should().Contain(e => e.Message.Contains("Gemini response"));
    }

    [Fact]
    public async Task LogsEmbedRequest_WhenEnableLoggingTrue()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var inner = new HttpClient(handler);
        var options = TestFactories.DefaultOptions(o => o.EnableLogging = true);
        var logger = new RecordingLogger<GeminiHttpClient>();
        var client = new GeminiHttpClient(inner, Options.Create(options), logger);

        handler.When(HttpMethod.Post, "*:embedContent*")
            .Respond("application/json", """{"embedding":{"values":[0.1]}}""");

        // Act
        await client.EmbedContentAsync(
            "text-embedding-004",
            new GeminiEmbedContentRequest
            {
                Model = "models/text-embedding-004",
                Content = new GeminiContent { Parts = new List<GeminiPart> { new() { Text = "x" } } }
            },
            CancellationToken.None);

        // Assert
        logger.Entries.Should().Contain(e => e.Message.Contains("embedContent request"));
    }

    [Fact]
    public async Task LogsBatchEmbedRequest_WhenEnableLoggingTrue()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var inner = new HttpClient(handler);
        var options = TestFactories.DefaultOptions(o => o.EnableLogging = true);
        var logger = new RecordingLogger<GeminiHttpClient>();
        var client = new GeminiHttpClient(inner, Options.Create(options), logger);

        handler.When(HttpMethod.Post, "*:batchEmbedContents*")
            .Respond("application/json", """{"embeddings":[]}""");

        // Act
        await client.BatchEmbedContentsAsync(
            "text-embedding-004",
            new GeminiBatchEmbedContentsRequest { Requests = new List<GeminiEmbedContentRequest>() },
            CancellationToken.None);

        // Assert
        logger.Entries.Should().Contain(e => e.Message.Contains("batchEmbedContents request"));
    }

    [Fact]
    public async Task GenerateContent_WithEmptyApiKey_StillBuildsKeyQuery()
    {
        // Arrange — empty key still produces "?key=" so the URL stays valid for tests/mocks.
        var (client, handler) = TestFactories.CreateHttpClient(o => o.ApiKey = string.Empty);
        string? capturedUri = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { capturedUri = req.RequestUri!.ToString(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        // Act
        await client.GenerateContentAsync(
            "gemini-2.0-flash",
            new GeminiGenerateContentRequest { Contents = new List<GeminiContent>() },
            CancellationToken.None);

        // Assert
        capturedUri.Should().Contain("?key=");
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
