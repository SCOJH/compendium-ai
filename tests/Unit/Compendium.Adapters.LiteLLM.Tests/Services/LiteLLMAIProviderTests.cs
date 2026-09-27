// -----------------------------------------------------------------------
// <copyright file="LiteLLMAIProviderTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.LiteLLM.StructuredOutputs;
using Compendium.Adapters.LiteLLM.Tests.TestSupport;
using Compendium.Adapters.LiteLLM.Tools;

namespace Compendium.Adapters.LiteLLM.Tests.Services;

public class LiteLLMAIProviderTests
{
    [Fact]
    public void ProviderId_Always_ReturnsLitellm()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var id = sut.ProviderId;

        // Assert
        id.Should().Be("litellm");
    }

    // ---------- CompleteAsync ----------

    [Fact]
    public async Task CompleteAsync_OnSuccess_MapsApiResponseToCompletionResponse()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "id": "chatcmpl-1",
          "model": "openai/gpt-4o-mini",
          "created": 1730000000,
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": "Hello world" }, "finish_reason": "stop" }
          ],
          "usage": { "prompt_tokens": 12, "completion_tokens": 3, "total_tokens": 15 }
        }
        """;
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", json);

        var request = new CompletionRequest
        {
            Model = "openai/gpt-4o-mini",
            Messages = new List<Message>
            {
                Message.User("Hi"),
                Message.Assistant("Yes?"),
                new() { Role = MessageRole.User, Content = "Tell me a joke", Name = "alice" }
            },
            SystemPrompt = "Be concise.",
            Temperature = 0.5f,
            MaxTokens = 256,
            TopP = 0.9f,
            FrequencyPenalty = 0.1f,
            PresencePenalty = 0.2f,
            StopSequences = new List<string> { "###" },
            UserId = "user-42"
        };

        // Act
        var result = await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be("chatcmpl-1");
        result.Value.Model.Should().Be("openai/gpt-4o-mini");
        result.Value.Content.Should().Be("Hello world");
        result.Value.FinishReason.Should().Be(FinishReason.Stop);
        result.Value.Usage.PromptTokens.Should().Be(12);
        result.Value.Usage.CompletionTokens.Should().Be(3);
        result.Value.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1730000000).UtcDateTime);
    }

    [Fact]
    public async Task CompleteAsync_WithEmptyChoices_ReturnsEmptyContentAndInProgressReason()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().BeEmpty();
        result.Value.FinishReason.Should().Be(FinishReason.InProgress);
        result.Value.Usage.PromptTokens.Should().Be(0);
        result.Value.Usage.CompletionTokens.Should().Be(0);
        result.Value.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CompleteAsync_WithEmptyModel_UsesDefaultModel()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "anthropic/claude-haiku-4.5");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "anthropic/claude-haiku-4.5");
        string? capturedBody = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req =>
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"anthropic/claude-haiku-4.5","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = string.Empty,
            Messages = new List<Message> { Message.User("hi") }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("\"model\":\"anthropic/claude-haiku-4.5\"");
    }

    [Fact]
    public async Task CompleteAsync_WithMaxTokensNull_AppliesDefaultMaxTokens()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultMaxTokens = 1234);
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultMaxTokens = 1234);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        body.Should().Contain("\"max_tokens\":1234");
    }

    [Fact]
    public async Task CompleteAsync_WithoutTemperature_SendsTheFallbackTemperature()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        body!.Should().Contain("\"temperature\":0.7", "an unset temperature keeps sending the former default");
    }

    [Fact]
    public async Task CompleteAsync_WithSystemPrompt_PrependsSystemMessage()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = "m",
            SystemPrompt = "You are helpful.",
            Messages = new List<Message> { Message.User("hi") }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        messages.Should().HaveCount(2);
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages[0].GetProperty("content").GetString().Should().Be("You are helpful.");
        messages[1].GetProperty("role").GetString().Should().Be("user");
    }

    [Fact]
    public async Task CompleteAsync_WithoutSystemPrompt_DoesNotPrependSystemMessage()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var roles = doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString()).ToList();
        roles.Should().NotContain("system");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AI.InvalidApiKey")]
    [InlineData(HttpStatusCode.TooManyRequests, "AI.RateLimitExceeded")]
    [InlineData(HttpStatusCode.PaymentRequired, "AI.InsufficientCredits")]
    [InlineData(HttpStatusCode.NotFound, "AI.ModelNotFound")]
    [InlineData(HttpStatusCode.InternalServerError, "AI.ProviderError")]
    public async Task CompleteAsync_OnHttpError_MapsStatusCodeToErrorCode(HttpStatusCode status, string expectedCode)
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(status, "application/json", """{"error":{"message":"oops","code":"some_code"}}""");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(expectedCode);
    }

    [Fact]
    public async Task CompleteAsync_OnNonJsonErrorBody_FallsBackToProviderError()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(HttpStatusCode.BadGateway, "text/plain", "Bad gateway");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("Bad gateway");
    }

    [Fact]
    public async Task CompleteAsync_OnEmptyErrorBody_FallsBackToStatusName()
    {
        // Arrange — proxy can return 502 with empty body when upstream is unreachable.
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(HttpStatusCode.BadGateway, "text/plain", string.Empty);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("BadGateway");
    }

    [Fact]
    public async Task CompleteAsync_OnInvalidSuccessBody_ReturnsProviderError()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond("application/json", "not valid json");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
    }

    [Fact]
    public async Task CompleteAsync_OnEmptySuccessBody_ReturnsProviderError()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond("application/json", "null");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
    }

    [Fact]
    public async Task CompleteAsync_OnHttpRequestException_ReturnsProviderError()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Throw(new HttpRequestException("network down"));

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("network down");
    }

    [Fact]
    public async Task CompleteAsync_OnNonCancellationTimeout_ReturnsTimeout()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Throw(new TaskCanceledException("server slow"));

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.Timeout");
    }

    [Fact]
    public async Task CompleteAsync_WhenCallerCancels_RethrowsCancellation()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Throw(new TaskCanceledException("cancelled"));

        // Act
        var act = async () => await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), cts.Token);

        // Assert
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Theory]
    [InlineData("stop", FinishReason.Stop)]
    [InlineData("STOP", FinishReason.Stop)]
    [InlineData("length", FinishReason.Length)]
    [InlineData("content_filter", FinishReason.ContentFilter)]
    [InlineData("tool_calls", FinishReason.ToolCall)]
    [InlineData("function_call", FinishReason.ToolCall)]
    [InlineData("weird_other", FinishReason.Other)]
    public async Task CompleteAsync_MapsFinishReasonCorrectly(string apiReason, FinishReason expected)
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = $$"""
        {
          "id": "x", "model": "m", "created": 0,
          "choices": [ { "index": 0, "message": { "role": "assistant", "content": "" }, "finish_reason": "{{apiReason}}" } ]
        }
        """;
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.FinishReason.Should().Be(expected);
    }

    // ---------- StreamCompleteAsync ----------

    [Fact]
    public async Task StreamCompleteAsync_OnSuccess_YieldsChunksWithIncrementingIndex_AndStopsOnFinal()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var stream = string.Join("\n",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"He\"}}]}",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"llo\"}}]}",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"never\"}}]}",
            "data: [DONE]",
            string.Empty);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("text/event-stream", stream);

        // Act
        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None))
        {
            r.IsSuccess.Should().BeTrue();
            chunks.Add(r.Value);
        }

        // Assert
        chunks.Should().HaveCount(3);
        chunks[0].ContentDelta.Should().Be("He");
        chunks[0].Index.Should().Be(0);
        chunks[0].IsFinal.Should().BeFalse();
        chunks[1].ContentDelta.Should().Be("llo");
        chunks[1].Index.Should().Be(1);
        chunks[2].IsFinal.Should().BeTrue();
        chunks[2].FinishReason.Should().Be(FinishReason.Stop);
        chunks[2].Usage!.PromptTokens.Should().Be(3);
        chunks[2].Usage!.CompletionTokens.Should().Be(2);
    }

    [Fact]
    public async Task StreamCompleteAsync_IgnoresMalformedDataLinesAndUnrelatedLines()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var stream = string.Join("\n",
            ": comment line that should be ignored",
            string.Empty,
            "data: not json",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"X\"},\"finish_reason\":\"stop\"}]}",
            "data: [DONE]",
            string.Empty);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("text/event-stream", stream);

        // Act
        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None))
        {
            r.IsSuccess.Should().BeTrue();
            chunks.Add(r.Value);
        }

        // Assert
        chunks.Should().ContainSingle();
        chunks[0].ContentDelta.Should().Be("X");
        chunks[0].IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task StreamCompleteAsync_WithEmptyModel_UsesDefaultModel_AndSendsStreamTrue()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "openai/gpt-4o-mini");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "openai/gpt-4o-mini");
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("text/event-stream", "data: [DONE]\n");

        var request = new CompletionRequest
        {
            Model = string.Empty,
            Messages = new List<Message> { Message.User("hi") }
        };

        // Act
        await foreach (var _ in sut.StreamCompleteAsync(request, CancellationToken.None))
        {
        }

        // Assert
        body.Should().NotBeNull();
        body!.Should().Contain("openai/gpt-4o-mini");
        body!.Should().Contain("\"stream\":true");
    }

    [Fact]
    public async Task StreamCompleteAsync_OnError_YieldsFailureOnceAndStops()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(HttpStatusCode.TooManyRequests, "application/json", """{"error":{"message":"limit"}}""");

        // Act
        var results = new List<Result<CompletionChunk>>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None))
        {
            results.Add(r);
        }

        // Assert
        results.Should().ContainSingle();
        results[0].IsFailure.Should().BeTrue();
        results[0].Error.Code.Should().Be("AI.RateLimitExceeded");
    }

    // ---------- EmbedAsync ----------

    [Fact]
    public async Task EmbedAsync_SingleBatch_AggregatesEmbeddings()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "model": "openai/text-embedding-3-small",
          "data": [
            { "index": 0, "embedding": [0.1, 0.2] },
            { "index": 1, "embedding": [0.3, 0.4] }
          ],
          "usage": { "prompt_tokens": 6, "total_tokens": 6 }
        }
        """;
        handler.When(HttpMethod.Post, "*/embeddings").Respond("application/json", json);

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(2), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Embeddings.Should().HaveCount(2);
        result.Value.Embeddings[0].Vector.Should().Equal(0.1f, 0.2f);
        result.Value.Embeddings[1].Vector.Should().Equal(0.3f, 0.4f);
        result.Value.Usage.PromptTokens.Should().Be(6);
        result.Value.Model.Should().Be("openai/text-embedding-3-small");
    }

    [Fact]
    public async Task EmbedAsync_LargeInputs_BatchesByMaxEmbeddingsBatchSize()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.MaxEmbeddingsBatchSize = 2);
        var sut = TestFactories.CreateProvider(httpClient, o => o.MaxEmbeddingsBatchSize = 2);

        var callCount = 0;
        handler.When(HttpMethod.Post, "*/embeddings").Respond(req =>
        {
            callCount++;
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var inputs = doc.RootElement.GetProperty("input").GetArrayLength();
            var data = string.Join(",", Enumerable.Range(0, inputs).Select(i =>
                $"{{\"index\":{i},\"embedding\":[{i * 0.1f}]}}"));
            var responseJson = $"{{\"model\":\"openai/text-embedding-3-small\",\"data\":[{data}],\"usage\":{{\"prompt_tokens\":2,\"total_tokens\":2}}}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(5), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        callCount.Should().Be(3); // ceil(5/2)
        result.Value.Embeddings.Should().HaveCount(5);
        result.Value.Embeddings.Select(e => e.Index).Should().BeEquivalentTo(new[] { 0, 1, 2, 3, 4 });
        result.Value.Usage.PromptTokens.Should().Be(6); // 3 batches × 2
    }

    [Fact]
    public async Task EmbedAsync_WithEmptyInputs_ReturnsInvalidRequest()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var request = new EmbeddingRequest { Model = "openai/text-embedding-3-small", Inputs = new List<string>() };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
    }

    [Fact]
    public async Task EmbedAsync_WithEmptyModel_UsesDefaultEmbeddingModel()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultEmbeddingModel = "openai/text-embedding-3-large");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultEmbeddingModel = "openai/text-embedding-3-large");
        string? body = null;
        handler.When(HttpMethod.Post, "*/embeddings")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"model":"openai/text-embedding-3-large","data":[],"usage":{"prompt_tokens":0,"total_tokens":0}}""");

        var request = new EmbeddingRequest
        {
            Model = string.Empty,
            Inputs = new List<string> { "x" }
        };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        body!.Should().Contain("openai/text-embedding-3-large");
        result.Value.Model.Should().Be("openai/text-embedding-3-large");
    }

    [Fact]
    public async Task EmbedAsync_OnHttpError_ReturnsFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/embeddings")
            .Respond(HttpStatusCode.Unauthorized, "application/json", """{"error":{"message":"bad key"}}""");

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(1), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Fact]
    public async Task EmbedAsync_OnTimeout_ReturnsTimeoutError()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/embeddings").Throw(new TaskCanceledException("slow"));

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(1), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.Timeout");
    }

    [Fact]
    public async Task EmbedAsync_PropagatesDimensionsAndUserId()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/embeddings")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"model":"m","data":[{"index":0,"embedding":[0.1]}],"usage":{"prompt_tokens":1,"total_tokens":1}}""");

        var request = new EmbeddingRequest
        {
            Model = "openai/text-embedding-3-small",
            Inputs = new List<string> { "hi" },
            Dimensions = 256,
            UserId = "user-7"
        };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        body!.Should().Contain("\"dimensions\":256");
        body!.Should().Contain("\"user\":\"user-7\"");
    }

    [Fact]
    public async Task EmbedAsync_NullRequest_Throws()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var act = async () => await sut.EmbedAsync(null!, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---------- ListModelsAsync ----------

    [Fact]
    public async Task ListModelsAsync_OnSuccess_MapsProviderPrefixedIds_AndCapabilities()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "data": [
            { "id": "openai/gpt-4o", "owned_by": "openai" },
            { "id": "anthropic/claude-sonnet-4", "owned_by": "anthropic" },
            { "id": "openai/text-embedding-3-small", "owned_by": "openai" },
            { "id": "bedrock/anthropic.claude-3-haiku", "owned_by": "aws" },
            { "id": "no-slash-model" }
          ]
        }
        """;
        handler.When(HttpMethod.Get, "*/models").Respond("application/json", json);

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(5);

        var gpt = result.Value[0];
        gpt.Id.Should().Be("openai/gpt-4o");
        gpt.Provider.Should().Be("openai");
        gpt.SupportsTools.Should().BeTrue();
        gpt.SupportsStreaming.Should().BeTrue();
        gpt.SupportsEmbeddings.Should().BeFalse();
        gpt.SupportsVision.Should().BeTrue();

        var claude = result.Value[1];
        claude.Provider.Should().Be("anthropic");
        claude.SupportsVision.Should().BeTrue();

        var embed = result.Value[2];
        embed.SupportsEmbeddings.Should().BeTrue();
        embed.SupportsTools.Should().BeFalse();
        embed.SupportsStreaming.Should().BeFalse();
        embed.SupportsVision.Should().BeFalse();

        var bedrock = result.Value[3];
        bedrock.Provider.Should().Be("bedrock");
        // model id contains "claude-3" → vision
        bedrock.SupportsVision.Should().BeTrue();

        var unprefixed = result.Value[4];
        // No provider prefix → falls back to "owned_by" (absent) → "litellm".
        unprefixed.Provider.Should().Be("litellm");
        unprefixed.SupportsVision.Should().BeFalse();
    }

    [Fact]
    public async Task ListModelsAsync_OnFailure_PropagatesError()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/models").Respond(HttpStatusCode.InternalServerError, "application/json", "{}");

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
    }

    // ---------- HealthCheckAsync ----------

    [Fact]
    public async Task HealthCheckAsync_WhenModelsListSucceeds_ReturnsSuccess()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/models").Respond("application/json", "{\"data\":[]}");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HealthCheckAsync_WhenModelsListFails_ReturnsFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/models")
            .Respond(HttpStatusCode.Unauthorized, "application/json", """{"error":{"message":"x"}}""");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Fact]
    public async Task HealthCheckAsync_WhenUnderlyingThrowsCancellation_ReturnsProviderUnavailable()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Get, "*/models").Throw(new TaskCanceledException("user cancel"));

        // Act
        var result = await sut.HealthCheckAsync(cts.Token);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderUnavailable");
    }

    // ---------- Tool calling ----------

    [Fact]
    public async Task CompleteAsync_WithTools_SerializesToolsArrayInRequest()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var tools = new List<AgentTool>
        {
            new("get_weather", "Get current weather for a city.",
                """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""")
        };
        var request = TestFactories.SimpleCompletionRequest().WithTools(tools, "auto");

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var toolsEl = doc.RootElement.GetProperty("tools").EnumerateArray().ToList();
        toolsEl.Should().ContainSingle();
        toolsEl[0].GetProperty("type").GetString().Should().Be("function");
        toolsEl[0].GetProperty("function").GetProperty("name").GetString().Should().Be("get_weather");
        toolsEl[0].GetProperty("function").GetProperty("description").GetString().Should().Be("Get current weather for a city.");
        toolsEl[0].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString().Should().Be("object");
        doc.RootElement.GetProperty("tool_choice").GetString().Should().Be("auto");
    }

    [Fact]
    public async Task CompleteAsync_WithSpecificToolChoice_SerializesObjectToolChoice()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = TestFactories.SimpleCompletionRequest()
            .WithTools(new List<AgentTool> { new("foo", "bar") }, "foo");

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var choice = doc.RootElement.GetProperty("tool_choice");
        choice.GetProperty("type").GetString().Should().Be("function");
        choice.GetProperty("function").GetProperty("name").GetString().Should().Be("foo");
    }

    [Fact]
    public async Task CompleteAsync_WithMalformedToolSchema_OmitsParameters()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var tools = new List<AgentTool> { new("foo", "desc", "{not json") };
        var request = TestFactories.SimpleCompletionRequest().WithTools(tools);

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("tools").EnumerateArray().First()
            .GetProperty("function").TryGetProperty("parameters", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_WhenAssistantEmitsToolCalls_SurfacesAgentToolInvocations()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "id": "chatcmpl-2",
          "model": "openai/gpt-4o-mini",
          "created": 0,
          "choices": [
            {
              "index": 0,
              "message": {
                "role": "assistant",
                "content": null,
                "tool_calls": [
                  {
                    "id": "call_1",
                    "type": "function",
                    "function": { "name": "get_weather", "arguments": "{\"city\":\"Paris\"}" }
                  }
                ]
              },
              "finish_reason": "tool_calls"
            }
          ]
        }
        """;
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.FinishReason.Should().Be(FinishReason.ToolCall);
        var calls = result.Value.GetToolCalls();
        calls.Should().ContainSingle();
        calls[0].ToolName.Should().Be("get_weather");
        calls[0].ArgumentsJson.Should().Contain("Paris");
        calls[0].IsError.Should().BeFalse();
        calls[0].ResultText.Should().BeEmpty();
        calls[0].Latency.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void GetToolCalls_WhenMetadataAbsent_ReturnsEmpty()
    {
        // Arrange
        var response = new CompletionResponse
        {
            Id = "x",
            Model = "m",
            Content = "y",
            FinishReason = FinishReason.Stop,
            Usage = new UsageStats { PromptTokens = 0, CompletionTokens = 0 }
        };

        // Act
        var calls = response.GetToolCalls();

        // Assert
        calls.Should().BeEmpty();
    }

    // ---------- Structured outputs ----------

    [Fact]
    public async Task CompleteAsync_WithStructuredOutput_AppliesJsonSchemaResponseFormat()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var schema = """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""";
        var request = TestFactories.SimpleCompletionRequest().WithStructuredOutput(schema, "MyAnswer", strict: false);

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var rf = doc.RootElement.GetProperty("response_format");
        rf.GetProperty("type").GetString().Should().Be("json_schema");
        rf.GetProperty("json_schema").GetProperty("name").GetString().Should().Be("MyAnswer");
        rf.GetProperty("json_schema").GetProperty("strict").GetBoolean().Should().BeFalse();
        rf.GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public async Task CompleteAsync_WithJsonMode_AppliesJsonObjectResponseFormat()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = TestFactories.SimpleCompletionRequest().WithJsonMode();

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("response_format").GetProperty("type").GetString().Should().Be("json_object");
    }

    [Fact]
    public async Task CompleteAsync_WithStructuredByDefaultOption_AppliesJsonObjectResponseFormat()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.UseStructuredOutputsByDefault = true);
        var sut = TestFactories.CreateProvider(httpClient, o => o.UseStructuredOutputsByDefault = true);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("response_format").GetProperty("type").GetString().Should().Be("json_object");
    }
}
