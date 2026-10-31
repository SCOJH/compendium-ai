// -----------------------------------------------------------------------
// <copyright file="TeiRerankerTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using Compendium.Adapters.HuggingFace.Http;
using Compendium.Adapters.HuggingFace.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Compendium.Adapters.HuggingFace.Tests.Services;

/// <summary>
/// <see cref="TeiReranker"/> against a simulated TEI <c>/rerank</c>, in the shape TEI's router writes it (openapi 1.9.4,
/// <c>router/src/http/server.rs</c>): <c>[{index, score}]</c> by descending score, index = position in <c>texts</c>.
/// </summary>
public class TeiRerankerTests
{
    private const string BaseUrl = "http://tei-reranker:8080";

    private static readonly RerankOptions All = new();

    [Fact]
    public async Task RerankAsync_SendsTheBodyTeiTakes()
    {
        var (sut, handler) = Create(o => o.TruncationDirection = TeiTruncationDirection.Left);
        var bodies = Capture(handler, """[{"index":1,"score":0.9},{"index":0,"score":0.1}]""");

        await sut.RerankAsync("what is deep learning?", ["a", "b"], All, CancellationToken.None);

        bodies.Should().ContainSingle().Which.Should().Be(
            """{"query":"what is deep learning?","texts":["a","b"],"raw_scores":false,"return_text":false,"truncate":true,"truncation_direction":"Left"}""");
    }

    [Fact]
    public async Task RerankAsync_ReturnsTheDocumentsByDescendingRelevance_WithTheirIndex()
    {
        var (sut, handler) = Create();
        Capture(handler, """[{"index":2,"score":0.2},{"index":0,"score":0.97},{"index":1,"score":0.5}]""");

        var result = await sut.RerankAsync("q", ["first", "second", "third"], All, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(d => d.Index).Should().Equal(0, 1, 2);
        result.Value.Select(d => d.RelevanceScore).Should().Equal(0.97, 0.5, 0.2);
        result.Value.Should().OnlyContain(d => d.Document == null);
    }

    [Fact]
    public async Task RerankAsync_EqualScores_KeepTheInputOrder()
    {
        var (sut, handler) = Create();
        Capture(handler, """[{"index":2,"score":0.5},{"index":0,"score":0.5},{"index":1,"score":0.5}]""");

        var result = await sut.RerankAsync("q", ["a", "b", "c"], All, CancellationToken.None);

        result.Value.Select(d => d.Index).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task RerankAsync_MoreDocumentsThanABatch_SendsSeveralAndMergesThemWithTheirGlobalIndex()
    {
        var (sut, handler) = Create(o => o.MaxBatchSize = 32);
        var bodies = new List<string>();
        handler.When(HttpMethod.Post, $"{BaseUrl}/rerank").Respond(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            lock (bodies)
            {
                bodies.Add(body);
            }

            // Each text is "doc-<n>", scored n / 100: the merged order is the reverse of the input.
            var texts = JsonDocument.Parse(body).RootElement.GetProperty("texts").EnumerateArray().Select(t => t.GetString()!).ToList();
            var ranks = texts.Select((t, i) => new { index = i, score = int.Parse(t["doc-".Length..]) / 100.0 })
                .OrderByDescending(r => r.score);
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(ranks));
        });
        var documents = Enumerable.Range(0, 70).Select(n => $"doc-{n}").ToList();

        var result = await sut.RerankAsync("q", documents, All, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("texts").GetArrayLength())
            .Should().BeEquivalentTo([32, 32, 6]);
        result.Value.Select(d => d.Index).Should().Equal(Enumerable.Range(0, 70).Reverse());
        result.Value[0].RelevanceScore.Should().Be(0.69);
    }

    [Fact]
    public async Task RerankAsync_TopNAndReturnDocuments_KeepTheBestWithTheirText()
    {
        var (sut, handler) = Create();
        Capture(handler, """[{"index":2,"score":0.9},{"index":0,"score":0.6},{"index":1,"score":0.1}]""");

        var result = await sut.RerankAsync("q", ["a", "b", "c"], new RerankOptions { TopN = 2, ReturnDocuments = true }, CancellationToken.None);

        result.Value.Should().HaveCount(2);
        result.Value[0].Should().Be(new RerankedDocument { Index = 2, RelevanceScore = 0.9, Document = "c" });
        result.Value[1].Should().Be(new RerankedDocument { Index = 0, RelevanceScore = 0.6, Document = "a" });
    }

    [Fact]
    public async Task RerankAsync_TopNAboveTheCount_ReturnsThemAll()
    {
        var (sut, handler) = Create();
        Capture(handler, """[{"index":0,"score":0.6},{"index":1,"score":0.1}]""");

        var result = await sut.RerankAsync("q", ["a", "b"], new RerankOptions { TopN = 10 }, CancellationToken.None);

        result.Value.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":"Model is unhealthy","error_type":"Unhealthy"}""")]
    [InlineData(HttpStatusCode.FailedDependency, """{"error":"score is NaN","error_type":"Backend"}""")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":"Model is overloaded","error_type":"Overloaded"}""")]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"error":"batch size 40 > maximum allowed batch size 32","error_type":"Validation"}""")]
    [InlineData(HttpStatusCode.Unauthorized, "<html>no</html>")]
    public async Task RerankAsync_AnErrorStatus_IsRerankFailed(HttpStatusCode status, string body)
    {
        var (sut, handler) = Create();
        handler.When(HttpMethod.Post, $"{BaseUrl}/rerank").Respond(_ => Json(status, body));

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.RerankFailed");
        result.Error.Message.Should().Contain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task RerankAsync_TeiErrorBody_IsInTheMessage()
    {
        var (sut, handler) = Create();
        handler.When(HttpMethod.Post, $"{BaseUrl}/rerank").Respond(_ =>
            Json(HttpStatusCode.UnprocessableEntity, """{"error":"batch size 40 > maximum allowed batch size 32","error_type":"Validation"}"""));

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.Error.Message.Should().Contain("422").And.Contain("(Validation)").And.Contain("maximum allowed batch size 32");
    }

    [Fact]
    public async Task RerankAsync_OneFailingBatch_FailsTheCall()
    {
        var (sut, handler) = Create(o => o.MaxBatchSize = 1);
        handler.When(HttpMethod.Post, $"{BaseUrl}/rerank").Respond(req =>
            req.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"b\"", StringComparison.Ordinal)
                ? Json(HttpStatusCode.ServiceUnavailable, "")
                : Json(HttpStatusCode.OK, """[{"index":0,"score":0.5}]"""));

        var result = await sut.RerankAsync("q", ["a", "b", "c"], All, CancellationToken.None);

        result.Error.Code.Should().Be("AI.RerankFailed");
    }

    [Theory]
    [InlineData("""[{"index":3,"score":0.5}]""")]
    [InlineData("""[{"index":-1,"score":0.5}]""")]
    [InlineData("""[{"index":0,"score":0.5},{"index":0,"score":0.4}]""")]
    public async Task RerankAsync_AnIndexOutsideItsBatchOrTwice_IsRerankFailed(string answer)
    {
        var (sut, handler) = Create();
        Capture(handler, answer);

        var result = await sut.RerankAsync("q", ["a", "b"], All, CancellationToken.None);

        result.Error.Code.Should().Be("AI.RerankFailed");
        result.Error.Message.Should().Contain("index");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"index":0}""")]
    public async Task RerankAsync_AnUnreadableAnswer_IsRerankFailed(string answer)
    {
        var (sut, handler) = Create();
        Capture(handler, answer);

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.Error.Code.Should().Be("AI.RerankFailed");
    }

    [Fact]
    public async Task RerankAsync_ATransportError_IsRerankFailed()
    {
        var (sut, handler) = Create();
        handler.When(HttpMethod.Post, $"{BaseUrl}/rerank").Throw(new HttpRequestException("connection refused"));

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.Error.Code.Should().Be("AI.RerankFailed");
        result.Error.Message.Should().Contain("connection refused");
    }

    [Fact]
    public async Task RerankAsync_PastTheTimeout_IsRerankFailedNotAnException()
    {
        var sut = Create(new HangingHandler(), o => o.TimeoutSeconds = 1);

        var result = await sut.RerankAsync("q", ["a", "b"], All, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.RerankFailed");
        result.Error.Message.Should().Contain("within 1 seconds");
    }

    [Fact]
    public async Task RerankAsync_AnHttpClientTimeout_IsRerankFailedNotAnException()
    {
        var sut = Create(new HangingHandler(), o => o.TimeoutSeconds = 30, client => client.Timeout = TimeSpan.FromMilliseconds(200));

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.Error.Code.Should().Be("AI.RerankFailed");
        result.Error.Message.Should().Contain("timed out");
    }

    [Fact]
    public async Task RerankAsync_CancelledByTheCaller_Throws()
    {
        var sut = Create(new HangingHandler(), o => o.TimeoutSeconds = 30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = () => sut.RerankAsync("q", ["a"], All, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RerankAsync_AnEmptyQuery_IsInvalidQueryWithoutARequest(string query)
    {
        var (sut, handler) = Create();
        var bodies = Capture(handler, "[]");

        var result = await sut.RerankAsync(query, ["a"], All, CancellationToken.None);

        result.Error.Code.Should().Be("AI.InvalidQuery");
        bodies.Should().BeEmpty();
    }

    [Fact]
    public async Task RerankAsync_NoDocuments_IsAnEmptySuccessWithoutARequest()
    {
        var (sut, handler) = Create();
        var bodies = Capture(handler, "[]");

        var result = await sut.RerankAsync("q", [], All, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        bodies.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task RerankAsync_TopNBelowOne_IsInvalidRequestWithoutARequest(int topN)
    {
        var (sut, handler) = Create();
        var bodies = Capture(handler, "[]");

        var result = await sut.RerankAsync("q", ["a"], new RerankOptions { TopN = topN }, CancellationToken.None);

        result.Error.Code.Should().Be("AI.InvalidRequest");
        bodies.Should().BeEmpty();
    }

    [Fact]
    public async Task RerankAsync_NullDocumentsOrOptions_Throws()
    {
        var (sut, _) = Create();

        await ((Func<Task>)(() => sut.RerankAsync("q", null!, All, CancellationToken.None))).Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(() => sut.RerankAsync("q", ["a"], null!, CancellationToken.None))).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RerankAsync_WithAnApiKey_SendsItAsBearer_AndNoneWithout()
    {
        string? withKey = "unset";
        string? without = "unset";
        var (keyed, keyedHandler) = Create(o => o.ApiKey = "tei-secret");
        keyedHandler.When(HttpMethod.Post, $"{BaseUrl}/rerank").Respond(req =>
        {
            withKey = req.Headers.Authorization?.ToString();
            return Json(HttpStatusCode.OK, "[]");
        });
        var (open, openHandler) = Create();
        openHandler.When(HttpMethod.Post, $"{BaseUrl}/rerank").Respond(req =>
        {
            without = req.Headers.Authorization?.ToString();
            return Json(HttpStatusCode.OK, "[]");
        });

        await keyed.RerankAsync("q", ["a"], All, CancellationToken.None);
        await open.RerankAsync("q", ["a"], All, CancellationToken.None);

        withKey.Should().Be("Bearer tei-secret");
        without.Should().BeNull();
    }

    [Fact]
    public async Task RerankAsync_ABaseUrlWithATrailingSlash_PostsToRerank()
    {
        var (sut, handler) = Create(o => o.BaseUrl = BaseUrl + "/");
        Capture(handler, """[{"index":0,"score":0.5}]""");

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task RerankAsync_ModelIsNotSent()
    {
        var (sut, handler) = Create();
        var bodies = Capture(handler, """[{"index":0,"score":0.5}]""");

        await sut.RerankAsync("q", ["a"], new RerankOptions { Model = "BAAI/bge-reranker-v2-m3" }, CancellationToken.None);

        bodies.Single().Should().NotContain("bge");
    }

    [Fact]
    public async Task RerankAsync_WithLogging_LogsTheRequest()
    {
        var logger = new TestFactories.RecordingLogger<TeiHttpClient>();
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{BaseUrl}/rerank").Respond(_ => Json(HttpStatusCode.OK, "[]"));
        var options = Options.Create(new TeiRerankerOptions { BaseUrl = BaseUrl, EnableLogging = true });
        var sut = new TeiReranker(new TeiHttpClient(new HttpClient(handler), options, logger), options, NullLogger<TeiReranker>.Instance);

        await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Message.Contains("/rerank", StringComparison.Ordinal));
    }

    private static (TeiReranker Reranker, MockHttpMessageHandler Handler) Create(Action<TeiRerankerOptions>? configure = null)
    {
        var handler = new MockHttpMessageHandler();
        return (Create(handler, configure), handler);
    }

    private static TeiReranker Create(HttpMessageHandler handler, Action<TeiRerankerOptions>? configure = null, Action<HttpClient>? client = null)
    {
        var options = new TeiRerankerOptions { BaseUrl = BaseUrl };
        configure?.Invoke(options);
        var httpClient = new HttpClient(handler);
        client?.Invoke(httpClient);
        var wrapped = Options.Create(options);
        return new TeiReranker(
            new TeiHttpClient(httpClient, wrapped, NullLogger<TeiHttpClient>.Instance),
            wrapped,
            NullLogger<TeiReranker>.Instance);
    }

    private static List<string> Capture(MockHttpMessageHandler handler, string answer)
    {
        var bodies = new List<string>();
        handler.When(HttpMethod.Post, $"{BaseUrl}/rerank").Respond(req =>
        {
            lock (bodies)
            {
                bodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            }

            return Json(HttpStatusCode.OK, answer);
        });
        return bodies;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A server that never answers, until the request is cancelled.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
