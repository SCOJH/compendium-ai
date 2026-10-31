// -----------------------------------------------------------------------
// <copyright file="ZeroEntropyRerankerTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using Compendium.Adapters.ZeroEntropy.Http;
using Compendium.Adapters.ZeroEntropy.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Compendium.Adapters.ZeroEntropy.Tests.Services;

/// <summary>
/// <see cref="ZeroEntropyReranker"/> against a simulated ZeroEntropy <c>/v1/models/rerank</c>, in the shape of its API
/// reference (docs.zeroentropy.dev/api-reference/models/rerank): <c>{results: [{index, relevance_score}], total_bytes,
/// total_tokens, actual_latency_mode, e2e_latency, inference_latency}</c>, results by descending score, index = position
/// in <c>documents</c>; errors <c>{detail}</c>, a string or (422) a list of <c>{loc, msg, type}</c>.
/// </summary>
public class ZeroEntropyRerankerTests
{
    private const string BaseUrl = "https://api.zeroentropy.test/v1";
    private const string RerankUrl = BaseUrl + "/models/rerank";
    private const string ApiKey = "ze-test-key-do-not-log";

    private static readonly RerankOptions All = new();

    [Fact]
    public async Task RerankAsync_SendsTheBodyZeroEntropyTakes()
    {
        var (sut, handler) = Create();
        var bodies = Capture(handler, Answer((1, 0.9), (0, 0.1)));

        await sut.RerankAsync("what is deep learning?", ["a", "b"], All, CancellationToken.None);

        bodies.Should().ContainSingle().Which.Should().Be(
            """{"model":"zerank-2","query":"what is deep learning?","documents":["a","b"]}""");
    }

    [Theory]
    [InlineData(ZeroEntropyLatency.Fast, "fast")]
    [InlineData(ZeroEntropyLatency.Slow, "slow")]
    public async Task RerankAsync_WithALatency_SendsIt(ZeroEntropyLatency latency, string sent)
    {
        var (sut, handler) = Create(o => o.Latency = latency);
        var bodies = Capture(handler, Answer((0, 0.9)));

        await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        bodies.Single().Should().Be($$"""{"model":"zerank-2","query":"q","documents":["a"],"latency":"{{sent}}"}""");
    }

    [Fact]
    public async Task RerankAsync_TheCallsModel_WinsOverTheOptions_AndABlankOneDoesNot()
    {
        var (sut, handler) = Create(o => o.Model = "zerank-1");
        var bodies = Capture(handler, Answer((0, 0.9)));

        await sut.RerankAsync("q", ["a"], new RerankOptions { Model = "zerank-1-small" }, CancellationToken.None);
        await sut.RerankAsync("q", ["a"], new RerankOptions { Model = " " }, CancellationToken.None);

        bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("model").GetString())
            .Should().Equal("zerank-1-small", "zerank-1");
    }

    [Fact]
    public async Task RerankAsync_TopNAndReturnDocuments_AreNotSent()
    {
        var (sut, handler) = Create();
        var bodies = Capture(handler, Answer((0, 0.9), (1, 0.1)));

        await sut.RerankAsync("q", ["a", "b"], new RerankOptions { TopN = 1, ReturnDocuments = true }, CancellationToken.None);

        bodies.Single().Should().NotContain("top_n").And.NotContain("return_documents");
    }

    [Fact]
    public async Task RerankAsync_SendsTheKeyAsBearer()
    {
        string? authorization = null;
        var (sut, handler) = Create(o => o.ApiKey = ApiKey + "\n");
        handler.When(HttpMethod.Post, RerankUrl).Respond(req =>
        {
            authorization = req.Headers.Authorization?.ToString();
            return Json(HttpStatusCode.OK, Answer((0, 0.5)));
        });

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        authorization.Should().Be($"Bearer {ApiKey}", "a key read from a file with its newline is trimmed");
    }

    [Fact]
    public async Task RerankAsync_ReturnsTheDocumentsByDescendingRelevance_WithTheirIndex()
    {
        var (sut, handler) = Create();
        Capture(handler, Answer((2, 0.2), (0, 0.97), (1, 0.5)));

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
        Capture(handler, Answer((2, 0.5), (0, 0.5), (1, 0.5)));

        var result = await sut.RerankAsync("q", ["a", "b", "c"], All, CancellationToken.None);

        result.Value.Select(d => d.Index).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task RerankAsync_MoreDocumentsThanABatch_SendsSeveralAndMergesThemWithTheirGlobalIndex()
    {
        var (sut, handler) = Create(o => o.MaxBatchSize = 32);
        var bodies = new List<string>();
        handler.When(HttpMethod.Post, RerankUrl).Respond(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            lock (bodies)
            {
                bodies.Add(body);
            }

            // Each document is "doc-<n>", scored n / 100: the merged order is the reverse of the input.
            var texts = JsonDocument.Parse(body).RootElement.GetProperty("documents").EnumerateArray().Select(t => t.GetString()!).ToList();
            var results = texts.Select((t, i) => (i, int.Parse(t["doc-".Length..]) / 100.0)).OrderByDescending(r => r.Item2);
            return Json(HttpStatusCode.OK, Answer(results.ToArray()));
        });
        var documents = Enumerable.Range(0, 70).Select(n => $"doc-{n}").ToList();

        var result = await sut.RerankAsync("q", documents, All, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("documents").GetArrayLength())
            .Should().BeEquivalentTo([32, 32, 6]);
        result.Value.Select(d => d.Index).Should().Equal(Enumerable.Range(0, 70).Reverse());
        result.Value[0].RelevanceScore.Should().Be(0.69);
    }

    [Fact]
    public async Task RerankAsync_ByDefault_SendsAThousandDocumentsInOneRequest()
    {
        var handler = new CountingHandler();
        var sut = Create(handler);

        var result = await sut.RerankAsync("q", Enumerable.Range(0, 1001).Select(n => $"d{n}").ToList(), All, CancellationToken.None);

        result.Value.Should().HaveCount(1001);
        handler.Sizes.Should().BeEquivalentTo([1000, 1]);
    }

    [Theory]
    [InlineData("q", "ee", 306, new[] { 2 })]
    [InlineData("q", "ee", 305, new[] { 1, 1 })]
    [InlineData("q", "éé", 310, new[] { 2 })]
    [InlineData("q", "éé", 309, new[] { 1, 1 })]
    [InlineData("ü", "ee", 308, new[] { 2 })]
    [InlineData("ü", "ee", 307, new[] { 1, 1 })]
    public async Task RerankAsync_ABatchStopsAtMaxBatchBytes_CountedAsZeroEntropyCountsThem(string query, string document, int maxBatchBytes, int[] sizes)
    {
        // 150 per document, plus the query and the document in UTF-8, the query counted once per document.
        var (sut, handler) = Create(o => o.MaxBatchBytes = maxBatchBytes);
        var sent = Scored(handler, _ => 0.5);

        var result = await sut.RerankAsync(query, [document, document], All, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        sent.Should().BeEquivalentTo(sizes);
    }

    [Fact]
    public async Task RerankAsync_BatchesOfUnequalSizes_MergeWithTheirGlobalIndex()
    {
        // With the query "q": 251 bytes for the long one, 152 for each letter; 310 at most a batch: 1, then 2, then 1.
        var (sut, handler) = Create(o => o.MaxBatchBytes = 310);
        var scores = new Dictionary<string, double> { [new string('x', 100)] = 0.1, ["a"] = 0.4, ["b"] = 0.9, ["c"] = 0.7 };
        var sent = Scored(handler, d => scores[d]);

        var result = await sut.RerankAsync("q", [new string('x', 100), "a", "b", "c"], new RerankOptions { ReturnDocuments = true }, CancellationToken.None);

        sent.Should().BeEquivalentTo([1, 2, 1]);
        result.Value.Select(d => d.Index).Should().Equal(2, 3, 1, 0);
        result.Value.Select(d => d.Document).Should().Equal("b", "c", "a", new string('x', 100));
    }

    [Fact]
    public async Task RerankAsync_ADocumentPastMaxBatchBytesAlone_IsRerankFailedWithoutARequest()
    {
        var (sut, handler) = Create(o => o.MaxBatchBytes = 160);
        var sent = Scored(handler, _ => 0.5);

        var result = await sut.RerankAsync("q", ["fits", new string('y', 10), "fits"], All, CancellationToken.None);

        result.Error.Code.Should().Be("AI.RerankFailed");
        result.Error.Message.Should().Contain("document 1 is 161 bytes").And.Contain("MaxBatchBytes (160)");
        sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData(4_999_849, true)]
    [InlineData(4_999_850, false)]
    public async Task RerankAsync_ByDefault_TakesUpToZeroEntropysFiveMillionBytes(int length, bool sent)
    {
        var (sut, handler) = Create();
        var sizes = Scored(handler, _ => 0.5);

        var result = await sut.RerankAsync("q", [new string('z', length)], All, CancellationToken.None);

        result.IsSuccess.Should().Be(sent);
        sizes.Should().HaveCount(sent ? 1 : 0);
    }

    [Fact]
    public async Task RerankAsync_TopNAndReturnDocuments_KeepTheBestWithTheirText()
    {
        var (sut, handler) = Create();
        Capture(handler, Answer((2, 0.9), (0, 0.6), (1, 0.1)));

        var result = await sut.RerankAsync("q", ["a", "b", "c"], new RerankOptions { TopN = 2, ReturnDocuments = true }, CancellationToken.None);

        result.Value.Should().HaveCount(2);
        result.Value[0].Should().Be(new RerankedDocument { Index = 2, RelevanceScore = 0.9, Document = "c" });
        result.Value[1].Should().Be(new RerankedDocument { Index = 0, RelevanceScore = 0.6, Document = "a" });
    }

    [Fact]
    public async Task RerankAsync_TopNAboveTheCount_ReturnsThemAll()
    {
        var (sut, handler) = Create();
        Capture(handler, Answer((0, 0.6), (1, 0.1)));

        var result = await sut.RerankAsync("q", ["a", "b"], new RerankOptions { TopN = 10 }, CancellationToken.None);

        result.Value.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"detail":"Invalid API key"}""")]
    [InlineData(HttpStatusCode.NotFound, """{"detail":"Description of Error"}""")]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"detail":[{"loc":["body","model"],"msg":"Input should be 'zerank-2', 'zerank-1' or 'zerank-1-small'","type":"enum"}]}""")]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"detail":[]}""")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, """{"detail":{"bytes":5000001}}""")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"detail":"Rate limit exceeded"}""")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.BadGateway, "[1, 2]")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "<html>no</html>")]
    [InlineData(HttpStatusCode.GatewayTimeout, "null")]
    public async Task RerankAsync_AnErrorStatus_IsRerankFailed(HttpStatusCode status, string body)
    {
        var (sut, handler) = Create();
        handler.When(HttpMethod.Post, RerankUrl).Respond(_ => Json(status, body));

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.RerankFailed");
        result.Error.Message.Should().Contain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task RerankAsync_ZeroEntropyErrorDetail_IsInTheMessage()
    {
        var (sut, handler) = Create();
        handler.When(HttpMethod.Post, RerankUrl).Respond(_ =>
            Json(HttpStatusCode.TooManyRequests, """{"detail":"Rate limit exceeded: the fast mode is not available"}"""));

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.Error.Message.Should().Contain("429").And.Contain("Rate limit exceeded: the fast mode is not available");
    }

    [Fact]
    public async Task RerankAsync_AValidationError_ListsWhereAndWhat()
    {
        var (sut, handler) = Create();
        handler.When(HttpMethod.Post, RerankUrl).Respond(_ => Json(
            HttpStatusCode.UnprocessableEntity,
            """{"detail":[{"loc":["body","documents",0],"msg":"Input should be a valid string","type":"string_type"},{"loc":["body","model"],"msg":"Field required","type":"missing"},{"msg":"no location"},"bare"]}"""));

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.Error.Message.Should().Contain("422")
            .And.Contain("body.documents.0: Input should be a valid string; body.model: Field required; no location; bare");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RerankAsync_ManyBatches_KeepsAtMostMaxConcurrentBatchesInFlight(int maxConcurrent)
    {
        var handler = new CountingHandler();
        var sut = Create(handler, o =>
        {
            o.MaxBatchSize = 2;
            o.MaxConcurrentBatches = maxConcurrent;
        });

        var result = await sut.RerankAsync("q", Enumerable.Range(0, 20).Select(n => $"d{n}").ToList(), All, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(20);
        handler.Requests.Should().Be(10);
        handler.MostInFlight.Should().Be(maxConcurrent);
    }

    [Fact]
    public async Task RerankAsync_AFailingBatch_StopsTheBatchesInFlightAndThoseNotStarted()
    {
        var handler = new FailFirstHandler();
        var sut = Create(handler, o =>
        {
            o.MaxBatchSize = 1;
            o.MaxConcurrentBatches = 2;
            o.TimeoutSeconds = 30;
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await sut.RerankAsync("q", ["fail", "hang", "c", "d", "e"], All, CancellationToken.None);

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the hanging batch is cancelled, not waited for");
        result.Error.Code.Should().Be("AI.RerankFailed");
        result.Error.Message.Should().Contain("503");
        handler.HangingCancelled.Should().BeTrue();
        handler.SentAfterTheStop.Should().Be(0, "no batch is sent after the failure");
    }

    [Fact]
    public async Task RerankAsync_AFailingBatch_NeverSendsABatchAfterTheStop()
    {
        // Repeated, for the interleavings of the failure, the hanging batch's cancellation and the waiting batches. A batch
        // that reaches the handler with its token already cancelled is not sent (a real handler refuses it unsent).
        for (var run = 0; run < 30; run++)
        {
            var handler = new FailFirstHandler();
            var sut = Create(handler, o =>
            {
                o.MaxBatchSize = 1;
                o.MaxConcurrentBatches = 2;
                o.TimeoutSeconds = 30;
            });

            var result = await sut.RerankAsync("q", ["fail", "hang", "c", "d", "e", "f"], All, CancellationToken.None);

            result.Error.Message.Should().Contain("503");
            handler.SentAfterTheStop.Should().Be(0, $"run {run}: no batch is sent once a batch failed");
        }
    }

    [Fact]
    public async Task RerankAsync_OneFailingBatch_FailsTheCall()
    {
        var (sut, handler) = Create(o => o.MaxBatchSize = 1);
        handler.When(HttpMethod.Post, RerankUrl).Respond(req =>
            req.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"b\"", StringComparison.Ordinal)
                ? Json(HttpStatusCode.ServiceUnavailable, "")
                : Json(HttpStatusCode.OK, Answer((0, 0.5))));

        var result = await sut.RerankAsync("q", ["a", "b", "c"], All, CancellationToken.None);

        result.Error.Code.Should().Be("AI.RerankFailed");
    }

    [Theory]
    [InlineData("", "ze", "m", 5_000_000, 1000, 4, 10, "BaseUrl")]
    [InlineData("/models/rerank", "ze", "m", 5_000_000, 1000, 4, 10, "BaseUrl")]
    [InlineData("ftp://api.zeroentropy.dev", "ze", "m", 5_000_000, 1000, 4, 10, "BaseUrl")]
    [InlineData(BaseUrl, "", "m", 5_000_000, 1000, 4, 10, "ApiKey")]
    [InlineData(BaseUrl, "  ", "m", 5_000_000, 1000, 4, 10, "ApiKey")]
    [InlineData(BaseUrl, "ze", "", 5_000_000, 1000, 4, 10, "Model")]
    [InlineData(BaseUrl, "ze", "m", 0, 1000, 4, 10, "MaxBatchBytes")]
    [InlineData(BaseUrl, "ze", "m", 5_000_001, 1000, 4, 10, "MaxBatchBytes must be between 1 and 5000000")]
    [InlineData(BaseUrl, "ze", "m", 5_000_000, 0, 4, 10, "MaxBatchSize")]
    [InlineData(BaseUrl, "ze", "m", 5_000_000, 1000, 0, 10, "MaxConcurrentBatches")]
    [InlineData(BaseUrl, "ze", "m", 5_000_000, 1000, 4, 0, "TimeoutSeconds")]
    [InlineData(BaseUrl, "ze", "m", 5_000_000, 1000, 4, -5, "TimeoutSeconds")]
    public async Task RerankAsync_OptionsThatCannotServe_AreRerankFailedWithoutARequest(
        string baseUrl, string apiKey, string model, int maxBatchBytes, int maxBatchSize, int maxConcurrent, int timeoutSeconds, string named)
    {
        var (sut, handler) = Create(o =>
        {
            o.BaseUrl = baseUrl;
            o.ApiKey = apiKey;
            o.Model = model;
            o.MaxBatchBytes = maxBatchBytes;
            o.MaxBatchSize = maxBatchSize;
            o.MaxConcurrentBatches = maxConcurrent;
            o.TimeoutSeconds = timeoutSeconds;
        });
        var bodies = Capture(handler, Answer());

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.Error.Code.Should().Be("AI.RerankFailed");
        result.Error.Message.Should().Contain("misconfigured").And.Contain(named);
        bodies.Should().BeEmpty();
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    public async Task RerankAsync_AnIndexOutsideItsBatchOrTwice_IsRerankFailed(int first, int second)
    {
        var (sut, handler) = Create();
        Capture(handler, Answer((first, 0.5), (second, 0.4)));

        var result = await sut.RerankAsync("q", ["a", "b"], All, CancellationToken.None);

        result.Error.Code.Should().Be("AI.RerankFailed");
        result.Error.Message.Should().Contain("index");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RerankAsync_AnAnswerThatDoesNotRankTheWholeBatch_IsRerankFailed(int count)
    {
        var (sut, handler) = Create();
        Capture(handler, Answer(Enumerable.Range(0, count).Select(i => (i, 0.5 - (i / 10.0))).ToArray()));

        var result = await sut.RerankAsync("q", ["a", "b"], All, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.RerankFailed");
        result.Error.Message.Should().Contain("of a batch of 2");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"results":[{"index":0,"relevance_score":"high"}]}""")]
    [InlineData("""[{"index":0,"relevance_score":0.5}]""")]
    [InlineData("""{"results":[{"index":"zero","relevance_score":0.5}]}""")]
    public async Task RerankAsync_AnUnreadableAnswer_IsRerankFailed(string answer)
    {
        var (sut, handler) = Create();
        Capture(handler, answer);

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.Error.Code.Should().Be("AI.RerankFailed");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"results":null,"total_bytes":0}""")]
    public async Task RerankAsync_AnAnswerWithoutResults_IsRerankFailed_NotAnEmptyRanking(string answer)
    {
        var (sut, handler) = Create();
        Capture(handler, answer);

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.Error.Code.Should().Be("AI.RerankFailed");
        result.Error.Message.Should().Contain("without results");
    }

    [Fact]
    public async Task RerankAsync_ATransportError_IsRerankFailed()
    {
        var (sut, handler) = Create();
        handler.When(HttpMethod.Post, RerankUrl).Throw(new HttpRequestException("connection refused"));

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
        var bodies = Capture(handler, Answer());

        var result = await sut.RerankAsync(query, ["a"], All, CancellationToken.None);

        result.Error.Code.Should().Be("AI.InvalidQuery");
        bodies.Should().BeEmpty();
    }

    [Fact]
    public async Task RerankAsync_NoDocuments_IsAnEmptySuccessWithoutARequest()
    {
        var (sut, handler) = Create();
        var bodies = Capture(handler, Answer());

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
        var bodies = Capture(handler, Answer());

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
    public async Task RerankAsync_ABaseUrlWithATrailingSlash_PostsToModelsRerank()
    {
        var (sut, handler) = Create(o => o.BaseUrl = BaseUrl + "/");
        Capture(handler, Answer((0, 0.5)));

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task RerankAsync_WithLogging_LogsTheRequestButNotTheKey()
    {
        var logs = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, RerankUrl).Respond(_ => Json(HttpStatusCode.Unauthorized, """{"message":"invalid api token"}"""));
        var options = Options.Create(new ZeroEntropyRerankerOptions { BaseUrl = BaseUrl, ApiKey = ApiKey, EnableLogging = true });
        var sut = new ZeroEntropyReranker(
            new ZeroEntropyHttpClient(new HttpClient(handler), options, factory.CreateLogger<ZeroEntropyHttpClient>()),
            options,
            factory.CreateLogger<ZeroEntropyReranker>());

        var result = await sut.RerankAsync("q", ["a"], All, CancellationToken.None);

        logs.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Message.Contains("/models/rerank", StringComparison.Ordinal));
        logs.Entries.Should().NotContain(e => e.Message.Contains(ApiKey, StringComparison.Ordinal));
        result.Error.Message.Should().NotContain(ApiKey);
    }

    private static (ZeroEntropyReranker Reranker, MockHttpMessageHandler Handler) Create(Action<ZeroEntropyRerankerOptions>? configure = null)
    {
        var handler = new MockHttpMessageHandler();
        return (Create(handler, configure), handler);
    }

    private static ZeroEntropyReranker Create(HttpMessageHandler handler, Action<ZeroEntropyRerankerOptions>? configure = null, Action<HttpClient>? client = null)
    {
        var options = new ZeroEntropyRerankerOptions { BaseUrl = BaseUrl, ApiKey = ApiKey };
        configure?.Invoke(options);
        var httpClient = new HttpClient(handler);
        client?.Invoke(httpClient);
        var wrapped = Options.Create(options);
        return new ZeroEntropyReranker(
            new ZeroEntropyHttpClient(httpClient, wrapped, NullLogger<ZeroEntropyHttpClient>.Instance),
            wrapped,
            NullLogger<ZeroEntropyReranker>.Instance);
    }

    /// <summary>ZeroEntropy's answer for these results, with the counts and latencies it sends beside them.</summary>
    private static string Answer(params (int Index, double Score)[] results) => JsonSerializer.Serialize(new
    {
        results = results.Select(r => new { index = r.Index, relevance_score = r.Score }),
        total_bytes = 1024,
        total_tokens = 256,
        actual_latency_mode = "fast",
        e2e_latency = 0.12,
        inference_latency = 0.08,
    });

    private static List<string> Capture(MockHttpMessageHandler handler, string answer)
    {
        var bodies = new List<string>();
        handler.When(HttpMethod.Post, RerankUrl).Respond(req =>
        {
            lock (bodies)
            {
                bodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            }

            return Json(HttpStatusCode.OK, answer);
        });
        return bodies;
    }

    /// <summary>Answers each batch with <paramref name="score"/> of each document; returns the batch sizes, in order sent.</summary>
    private static List<int> Scored(MockHttpMessageHandler handler, Func<string, double> score)
    {
        var sizes = new List<int>();
        handler.When(HttpMethod.Post, RerankUrl).Respond(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var texts = JsonDocument.Parse(body).RootElement.GetProperty("documents").EnumerateArray().Select(t => t.GetString()!).ToList();
            lock (sizes)
            {
                sizes.Add(texts.Count);
            }

            return Json(HttpStatusCode.OK, Answer(texts.Select((t, i) => (i, score(t))).ToArray()));
        });
        return sizes;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Answers every document after a short wait, and counts the requests in flight at once.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly List<int> _sizes = new();
        private int _inFlight;
        private int _mostInFlight;
        private int _requests;

        public int MostInFlight => _mostInFlight;

        public int Requests => _requests;

        public IReadOnlyList<int> Sizes
        {
            get
            {
                lock (_sizes)
                {
                    return _sizes.ToList();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = _mostInFlight) < now && Interlocked.CompareExchange(ref _mostInFlight, now, seen) != seen)
            {
            }

            try
            {
                await Task.Delay(50, cancellationToken);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var count = JsonDocument.Parse(body).RootElement.GetProperty("documents").GetArrayLength();
                lock (_sizes)
                {
                    _sizes.Add(count);
                }

                return Json(HttpStatusCode.OK, Answer(Enumerable.Range(0, count).Select(i => (i, 0.5)).ToArray()));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>Fails the batch "fail" at once, hangs on "hang" until cancelled, answers the others.</summary>
    private sealed class FailFirstHandler : HttpMessageHandler
    {
        private int _sentAfterTheStop;
        private volatile bool _failed;

        /// <summary>The requests sent, their token live, once "fail" was answered.</summary>
        public int SentAfterTheStop => _sentAfterTheStop;

        public bool HangingCancelled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_failed && !cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _sentAfterTheStop);
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (body.Contains("\"hang\"", StringComparison.Ordinal))
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    HangingCancelled = true;
                    throw;
                }
            }

            if (body.Contains("\"fail\"", StringComparison.Ordinal))
            {
                await Task.Delay(100, cancellationToken);
                _failed = true;
                return Json(HttpStatusCode.ServiceUnavailable, """{"detail":"Service unavailable"}""");
            }

            return Json(HttpStatusCode.OK, Answer((0, 0.5)));
        }
    }

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
