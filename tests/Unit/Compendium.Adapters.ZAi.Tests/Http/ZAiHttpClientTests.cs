// -----------------------------------------------------------------------
// <copyright file="ZAiHttpClientTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.ZAi.Http;
using Compendium.Adapters.ZAi.Http.Models;
using Compendium.Adapters.ZAi.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Compendium.Adapters.ZAi.Tests.Http;

/// <summary>
/// The HTTP client: the endpoint, the key, the failures. The error codes are Z.ai's
/// (https://docs.z.ai/api-reference/api-code.md, read 2026-10-20): the envelope <c>{"error":{"code","message"}}</c>, the
/// code a string in the examples and an integer in the schema; an exhausted balance answers 429 with 1113, not 402.
/// </summary>
public class ZAiHttpClientTests
{
    private static ZAiChatCompletionRequest Probe(bool stream = false) => new()
    {
        Model = "glm-5.3",
        Messages = [new ZAiRequestMessage { Role = "user", Content = "Hi" }],
        Stream = stream,
    };

    [Fact]
    public void TheDefaultEndpoint_IsZaisStandardApi_WithTheKeyAsBearer()
    {
        var inner = new HttpClient(new MockHttpMessageHandler());

        _ = new ZAiHttpClient(inner, Options.Create(TestFactories.DefaultOptions()), NullLogger<ZAiHttpClient>.Instance);

        inner.BaseAddress!.ToString().Should().Be("https://api.z.ai/api/paas/v4/");
        inner.DefaultRequestHeaders.Authorization!.Scheme.Should().Be("Bearer");
        inner.DefaultRequestHeaders.Authorization!.Parameter.Should().Be(TestFactories.DefaultApiKey);
    }

    [Fact]
    public void AnotherEndpoint_IsTaken_AndAPresetOneKept()
    {
        var china = new HttpClient(new MockHttpMessageHandler());
        var preset = new HttpClient(new MockHttpMessageHandler()) { BaseAddress = new Uri("https://proxy.test/") };
        var keyless = new HttpClient(new MockHttpMessageHandler());

        _ = new ZAiHttpClient(china, Options.Create(TestFactories.DefaultOptions(o => o.BaseUrl = ZAiOptions.MainlandChinaBaseUrl + "/")), NullLogger<ZAiHttpClient>.Instance);
        _ = new ZAiHttpClient(preset, Options.Create(TestFactories.DefaultOptions()), NullLogger<ZAiHttpClient>.Instance);
        _ = new ZAiHttpClient(keyless, Options.Create(TestFactories.DefaultOptions(o => o.ApiKey = string.Empty)), NullLogger<ZAiHttpClient>.Instance);

        china.BaseAddress!.ToString().Should().Be("https://open.bigmodel.cn/api/paas/v4/");
        preset.BaseAddress!.ToString().Should().Be("https://proxy.test/");
        keyless.DefaultRequestHeaders.Authorization.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "\"1001\"", "AI.InvalidApiKey")]
    [InlineData(HttpStatusCode.Unauthorized, "\"1000\"", "AI.InvalidApiKey")]
    [InlineData(HttpStatusCode.TooManyRequests, "\"1113\"", "AI.InsufficientCredits")]
    [InlineData(HttpStatusCode.TooManyRequests, "1113", "AI.InsufficientCredits")]
    [InlineData(HttpStatusCode.TooManyRequests, "\"1309\"", "AI.InsufficientCredits")]
    [InlineData(HttpStatusCode.TooManyRequests, "\"1302\"", "AI.RateLimitExceeded")]
    [InlineData(HttpStatusCode.TooManyRequests, "\"1305\"", "AI.RateLimitExceeded")]
    [InlineData(HttpStatusCode.TooManyRequests, "\"1316\"", "AI.RateLimitExceeded")]
    [InlineData(HttpStatusCode.TooManyRequests, "\"1311\"", "AI.ProviderError")]
    [InlineData(HttpStatusCode.BadRequest, "\"1211\"", "AI.ModelNotFound")]
    [InlineData(HttpStatusCode.BadRequest, "\"1214\"", "AI.ProviderError")]
    [InlineData(HttpStatusCode.BadRequest, "\"1301\"", "AI.ContentFiltered")]
    [InlineData(HttpStatusCode.InternalServerError, "\"1234\"", "AI.ProviderError")]
    [InlineData(HttpStatusCode.PaymentRequired, "null", "AI.InsufficientCredits")]
    [InlineData(HttpStatusCode.NotFound, "null", "AI.ModelNotFound")]
    public void AnError_IsMappedByItsCode_ThenItsStatus(HttpStatusCode status, string code, string expected)
    {
        var body = "{\"error\":{\"code\":" + code + ",\"message\":\"m\"}}";

        ZAiHttpClient.MapError(status, body).Code.Should().Be(expected);
    }

    [Fact]
    public void AParameterError_KeepsZaisCodeAndMessage()
    {
        var error = ZAiHttpClient.MapError(HttpStatusCode.BadRequest, """{"error":{"code":"1214","message":"Parameter `stop` is invalid."}}""");

        error.Message.Should().Contain("1214").And.Contain("Parameter `stop` is invalid.");
    }

    [Theory]
    [InlineData("<html>bad gateway</html>", "<html>bad gateway</html>")]
    [InlineData("", "BadGateway")]
    [InlineData("{\"detail\":\"x\"}", "{\"detail\":\"x\"}")]
    public void ABodyThatIsNotZaisEnvelope_IsTheMessage(string body, string message)
    {
        var error = ZAiHttpClient.MapError(HttpStatusCode.BadGateway, body);

        error.Code.Should().Be("AI.ProviderError");
        error.Message.Should().Contain(message);
    }

    [Fact]
    public async Task ATimeout_IsATimeout_ACancellationIsRethrown_ANetworkErrorIsAProviderError()
    {
        var (timedOut, h1) = TestFactories.CreateHttpClient();
        h1.When(HttpMethod.Post, "*").Throw(new TaskCanceledException("slow"));
        var (broken, h2) = TestFactories.CreateHttpClient();
        h2.When(HttpMethod.Post, "*").Throw(new HttpRequestException("reset"));
        var (cancelled, h3) = TestFactories.CreateHttpClient();
        using var cts = new CancellationTokenSource();
        h3.When(HttpMethod.Post, "*").Respond(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });

        (await timedOut.CreateChatCompletionAsync(Probe(), CancellationToken.None)).Error.Code.Should().Be("AI.Timeout");
        (await broken.CreateChatCompletionAsync(Probe(), CancellationToken.None)).Error.Code.Should().Be("AI.ProviderError");
        var act = () => cancelled.CreateChatCompletionAsync(Probe(), cts.Token);
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task AStreamThatCannotStart_IsAFailure()
    {
        var (timedOut, h1) = TestFactories.CreateHttpClient();
        h1.When(HttpMethod.Post, "*").Throw(new TaskCanceledException("slow"));
        var (broken, h2) = TestFactories.CreateHttpClient();
        h2.When(HttpMethod.Post, "*").Throw(new HttpRequestException("reset"));

        (await FirstAsync(timedOut.StreamChatCompletionAsync(Probe(stream: true), CancellationToken.None))).Error.Code.Should().Be("AI.Timeout");
        (await FirstAsync(broken.StreamChatCompletionAsync(Probe(stream: true), CancellationToken.None))).Error.Code.Should().Be("AI.ProviderError");
    }

    [Theory]
    [InlineData("not json", "Invalid response format")]
    [InlineData("null", "Empty response from provider")]
    public async Task ASuccessThatCannotBeRead_IsAProviderError(string body, string message)
    {
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*").Respond("application/json", body);

        var result = await client.CreateChatCompletionAsync(Probe(), CancellationToken.None);

        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain(message);
    }

    [Fact]
    public async Task WithLogging_TheBodiesAreLoggedAtDebug()
    {
        var logger = new RecordingLogger();
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "*").Respond("text/event-stream", "data: [DONE]\n\n");
        var client = new ZAiHttpClient(new HttpClient(handler), Options.Create(TestFactories.DefaultOptions(o => o.EnableLogging = true)), logger);

        await client.CreateChatCompletionAsync(Probe(), CancellationToken.None);
        await foreach (var _ in client.StreamChatCompletionAsync(Probe(stream: true), CancellationToken.None))
        {
        }

        logger.Messages.Should().Contain(m => m.StartsWith("Z.ai request:", StringComparison.Ordinal))
            .And.Contain(m => m.StartsWith("Z.ai response", StringComparison.Ordinal))
            .And.Contain(m => m.StartsWith("Z.ai streaming request:", StringComparison.Ordinal));
    }

    private static async Task<Result<ZAiStreamChunk>> FirstAsync(IAsyncEnumerable<Result<ZAiStreamChunk>> stream)
    {
        await foreach (var item in stream)
        {
            return item;
        }

        throw new InvalidOperationException("empty stream");
    }

    private sealed class RecordingLogger : ILogger<ZAiHttpClient>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
