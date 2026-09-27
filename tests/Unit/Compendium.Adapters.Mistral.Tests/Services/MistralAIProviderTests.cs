// -----------------------------------------------------------------------
// <copyright file="MistralAIProviderTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Mistral.StructuredOutputs;
using Compendium.Adapters.Mistral.Tests.TestSupport;
using Compendium.Adapters.Mistral.Tools;
using Compendium.Adapters.Mistral.Vision;

namespace Compendium.Adapters.Mistral.Tests.Services;

public class MistralAIProviderTests
{
    [Fact]
    public void ProviderId_Always_ReturnsMistral()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var id = sut.ProviderId;

        // Assert
        id.Should().Be("mistral");
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
          "id": "cmpl-1",
          "model": "mistral-large-latest",
          "created": 1730000000,
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": "Bonjour" }, "finish_reason": "stop" }
          ],
          "usage": { "prompt_tokens": 10, "completion_tokens": 2, "total_tokens": 12 }
        }
        """;
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", json);

        var request = new CompletionRequest
        {
            Model = "mistral-large-latest",
            Messages = new List<Message>
            {
                Message.User("Hi"),
                Message.Assistant("Yes?"),
                new() { Role = MessageRole.User, Content = "Bonjour", Name = "alice" }
            },
            SystemPrompt = "Soyez concis.",
            Temperature = 0.5f,
            MaxTokens = 256,
            TopP = 0.9f,
            StopSequences = new List<string> { "###" },
            UserId = "user-42"
        };

        // Act
        var result = await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be("cmpl-1");
        result.Value.Model.Should().Be("mistral-large-latest");
        result.Value.Content.Should().Be("Bonjour");
        result.Value.FinishReason.Should().Be(FinishReason.Stop);
        result.Value.Usage.PromptTokens.Should().Be(10);
        result.Value.Usage.CompletionTokens.Should().Be(2);
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
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "mistral-medium-latest");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "mistral-medium-latest");
        string? capturedBody = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req =>
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"mistral-medium-latest","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = string.Empty,
            Messages = new List<Message> { Message.User("hi") }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("\"model\":\"mistral-medium-latest\"");
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
            .Respond(status, "application/json", """{"message":"oops","code":"some_code"}""");

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
    [InlineData("model_length", FinishReason.Length)]
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

    [Fact]
    public async Task CompleteAsync_WithContentPartsArray_ConcatenatesTextParts()
    {
        // Arrange — Mistral occasionally returns content as a parts array even on text-only replies.
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "id": "x", "model": "m", "created": 0,
          "choices": [ { "index": 0, "message": { "role": "assistant", "content": [
            { "type": "text", "text": "Hello " },
            { "type": "text", "text": "world" }
          ] }, "finish_reason": "stop" } ]
        }
        """;
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be("Hello world");
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
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "mistral-large-latest");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "mistral-large-latest");
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
        body!.Should().Contain("mistral-large-latest");
        body!.Should().Contain("\"stream\":true");
    }

    [Fact]
    public async Task StreamCompleteAsync_OnError_YieldsFailureOnceAndStops()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(HttpStatusCode.TooManyRequests, "application/json", """{"message":"limit"}""");

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
          "model": "mistral-embed",
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
        result.Value.Model.Should().Be("mistral-embed");
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
            var responseJson = $"{{\"model\":\"mistral-embed\",\"data\":[{data}],\"usage\":{{\"prompt_tokens\":2,\"total_tokens\":2}}}}";
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
    public async Task EmbedAsync_WithBatchSizeZero_StillProcesses()
    {
        // Arrange — defensive: even if a misconfiguration lands a 0 batch size, the provider must
        // not divide by zero or spin forever. We clamp to at least 1.
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.MaxEmbeddingsBatchSize = 0);
        var sut = TestFactories.CreateProvider(httpClient, o => o.MaxEmbeddingsBatchSize = 0);

        var callCount = 0;
        handler.When(HttpMethod.Post, "*/embeddings").Respond(req =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"model":"mistral-embed","data":[{"index":0,"embedding":[0.5]}],"usage":{"prompt_tokens":1,"total_tokens":1}}""",
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        });

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(2), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        callCount.Should().Be(2); // batch size clamped to 1
    }

    [Fact]
    public async Task EmbedAsync_WithEmptyInputs_ReturnsInvalidRequest()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var request = new EmbeddingRequest { Model = "mistral-embed", Inputs = new List<string>() };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
    }

    [Fact]
    public async Task EmbedAsync_WithNullInputs_ReturnsInvalidRequest()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var request = new EmbeddingRequest { Model = "mistral-embed", Inputs = null! };

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
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultEmbeddingModel = "mistral-embed");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultEmbeddingModel = "mistral-embed");
        string? body = null;
        handler.When(HttpMethod.Post, "*/embeddings")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"model":"mistral-embed","data":[],"usage":{"prompt_tokens":0,"total_tokens":0}}""");

        var request = new EmbeddingRequest
        {
            Model = string.Empty,
            Inputs = new List<string> { "x" }
        };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        body!.Should().Contain("mistral-embed");
        result.Value.Model.Should().Be("mistral-embed");
    }

    [Fact]
    public async Task EmbedAsync_OnHttpError_ReturnsFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/embeddings")
            .Respond(HttpStatusCode.Unauthorized, "application/json", """{"message":"bad key"}""");

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(1), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Fact]
    public async Task EmbedAsync_OnNullRequest_Throws()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var act = async () => await sut.EmbedAsync(null!, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
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

    // ---------- ListModelsAsync ----------

    [Fact]
    public async Task ListModelsAsync_OnSuccess_MapsAllFields()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "data": [
            { "id": "mistral-large-latest", "owned_by": "mistralai" },
            { "id": "mistral-embed", "owned_by": "mistralai" },
            { "id": "pixtral-12b-latest", "owned_by": "mistralai" },
            { "id": "codestral-latest", "owned_by": "mistralai" }
          ]
        }
        """;
        handler.When(HttpMethod.Get, "*/models").Respond("application/json", json);

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(4);

        var large = result.Value[0];
        large.Id.Should().Be("mistral-large-latest");
        large.Provider.Should().Be("mistralai");
        large.SupportsTools.Should().BeTrue();
        large.SupportsStreaming.Should().BeTrue();
        large.SupportsEmbeddings.Should().BeFalse();
        large.SupportsVision.Should().BeFalse();

        var embed = result.Value[1];
        embed.SupportsEmbeddings.Should().BeTrue();
        embed.SupportsTools.Should().BeFalse();
        embed.SupportsStreaming.Should().BeFalse();

        var pixtral = result.Value[2];
        pixtral.SupportsVision.Should().BeTrue();
        pixtral.SupportsTools.Should().BeTrue();

        var codestral = result.Value[3];
        codestral.Metadata.Should().NotBeNull();
        codestral.Metadata!["specialty"].Should().Be("code");
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

    [Fact]
    public async Task ListModelsAsync_DefaultsProviderWhenOwnerOmitted()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/models").Respond("application/json", """{"data":[{"id":"my-model"}]}""");

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value[0].Provider.Should().Be("mistral");
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
            .Respond(HttpStatusCode.Unauthorized, "application/json", """{"message":"x"}""");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
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
    public async Task CompleteAsync_WithAnyToolChoice_SerializesVerbatim()
    {
        // Arrange — Mistral uses "any" (not "required") to force a tool call.
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = TestFactories.SimpleCompletionRequest()
            .WithTools(new List<AgentTool> { new("foo", "bar") }, "any");

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("tool_choice").GetString().Should().Be("any");
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

        // Assert — parameters should be absent (not serialised)
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
          "id": "cmpl-2",
          "model": "mistral-large-latest",
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

    // ---------- Vision (Pixtral) ----------

    [Fact]
    public async Task CompleteAsync_WithImages_PromotesLastUserMessageToContentPartsArray()
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
            Model = "pixtral-12b-latest",
            Messages = new List<Message>
            {
                Message.Assistant("Sure, let me look."),
                Message.User("What's in this picture?")
            }
        }.WithImages("https://example.com/cat.jpg");

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        messages.Should().HaveCount(2);
        // Last user message should now hold a content array, not a string.
        var userContent = messages[1].GetProperty("content");
        userContent.ValueKind.Should().Be(JsonValueKind.Array);
        var parts = userContent.EnumerateArray().ToList();
        parts.Should().HaveCount(2);
        parts[0].GetProperty("type").GetString().Should().Be("text");
        parts[0].GetProperty("text").GetString().Should().Be("What's in this picture?");
        parts[1].GetProperty("type").GetString().Should().Be("image_url");
        parts[1].GetProperty("image_url").GetProperty("url").GetString().Should().Be("https://example.com/cat.jpg");
    }

    [Fact]
    public async Task CompleteAsync_WithImages_AppendsUserMessage_WhenNoneExist()
    {
        // Arrange — assistant-only history; the adapter must append a fresh user message to attach images to.
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = "pixtral-12b-latest",
            Messages = new List<Message> { Message.Assistant("Hi.") }
        }.WithImages("https://example.com/a.jpg");

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        messages.Should().HaveCount(2);
        messages[1].GetProperty("role").GetString().Should().Be("user");
        messages[1].GetProperty("content").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task CompleteAsync_WithImages_AndEmptyTextContent_OmitsTextPart()
    {
        // Arrange — guarantees we don't emit empty text fragments which Mistral would reject.
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = "pixtral-12b-latest",
            Messages = new List<Message> { new() { Role = MessageRole.User, Content = string.Empty } }
        }.WithImages("https://example.com/a.jpg");

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var parts = doc.RootElement.GetProperty("messages")
            .EnumerateArray().Last().GetProperty("content").EnumerateArray().ToList();
        parts.Should().ContainSingle();
        parts[0].GetProperty("type").GetString().Should().Be("image_url");
    }

    [Fact]
    public async Task CompleteAsync_WithoutImages_KeepsContentAsString()
    {
        // Arrange — ensures we don't accidentally rewrite text-only messages.
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
        var content = doc.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content");
        content.ValueKind.Should().Be(JsonValueKind.String);
    }
}
