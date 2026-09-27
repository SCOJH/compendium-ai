// -----------------------------------------------------------------------
// <copyright file="DeepSeekAIProviderTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.DeepSeek.Reasoning;
using Compendium.Adapters.DeepSeek.Tests.TestSupport;
using Compendium.Adapters.DeepSeek.Tools;

namespace Compendium.Adapters.DeepSeek.Tests.Services;

public class DeepSeekAIProviderTests
{
    [Fact]
    public void ProviderId_Always_ReturnsDeepseek()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var id = sut.ProviderId;

        // Assert
        id.Should().Be("deepseek");
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
          "model": "deepseek-chat",
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
            Model = "deepseek-chat",
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
        result.Value.Model.Should().Be("deepseek-chat");
        result.Value.Content.Should().Be("Hello world");
        result.Value.FinishReason.Should().Be(FinishReason.Stop);
        result.Value.Usage.PromptTokens.Should().Be(12);
        result.Value.Usage.CompletionTokens.Should().Be(3);
        result.Value.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1730000000).UtcDateTime);
        result.Value.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_NullRequest_Throws()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var act = async () => await sut.CompleteAsync(null!, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CompleteAsync_WithEmptyChoices_ReturnsEmptyContentAndInProgressReason()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond("application/json", """{"id":"x","model":"deepseek-chat","created":0,"choices":[]}""");

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
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "deepseek-chat");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "deepseek-chat");
        string? capturedBody = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req =>
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"deepseek-chat","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = string.Empty,
            Messages = new List<Message> { Message.User("hi") }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("\"model\":\"deepseek-chat\"");
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
    [InlineData(HttpStatusCode.BadGateway, "AI.ProviderError")]
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
    public async Task StreamCompleteAsync_NullRequest_Throws()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var act = async () =>
        {
            await foreach (var _ in sut.StreamCompleteAsync(null!, CancellationToken.None))
            {
            }
        };

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
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
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "deepseek-chat");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "deepseek-chat");
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
        body!.Should().Contain("deepseek-chat");
        body!.Should().Contain("\"stream\":true");
    }

    [Fact]
    public async Task StreamCompleteAsync_OnRateLimit_YieldsFailureOnceAndStops()
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

    [Fact]
    public async Task StreamCompleteAsync_OnServerError_YieldsProviderErrorOnce()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(HttpStatusCode.InternalServerError, "application/json", """{"error":{"message":"boom"}}""");

        // Act
        var results = new List<Result<CompletionChunk>>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None))
        {
            results.Add(r);
        }

        // Assert
        results.Should().ContainSingle();
        results[0].IsFailure.Should().BeTrue();
        results[0].Error.Code.Should().Be("AI.ProviderError");
    }

    // ---------- EmbedAsync (unsupported) ----------

    [Fact]
    public async Task EmbedAsync_AlwaysReturnsUnsupportedFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        // Tripwire — if EmbedAsync ever hits the network, this test should fail noisily.
        handler.When(HttpMethod.Post, "*/embeddings").Respond("application/json", "{}");

        var request = new EmbeddingRequest
        {
            Model = "anything",
            Inputs = new List<string> { "hello" }
        };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.Unsupported");
        result.Error.Message.Should().Contain("DeepSeek does not");
        handler.GetMatchCount(handler.Expect(HttpMethod.Post, "*/embeddings")).Should().Be(0);
    }

    // ---------- ListModelsAsync ----------

    [Fact]
    public async Task ListModelsAsync_OnSuccess_MapsAllFields_AndMarksReasonerNoTools()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "data": [
            { "id": "deepseek-chat", "owned_by": "deepseek" },
            { "id": "deepseek-reasoner", "owned_by": "deepseek" }
          ]
        }
        """;
        handler.When(HttpMethod.Get, "*/models").Respond("application/json", json);

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);

        var chat = result.Value[0];
        chat.Id.Should().Be("deepseek-chat");
        chat.Provider.Should().Be("deepseek");
        chat.SupportsTools.Should().BeTrue();
        chat.SupportsStreaming.Should().BeTrue();
        chat.SupportsEmbeddings.Should().BeFalse();
        chat.SupportsVision.Should().BeFalse();

        var reasoner = result.Value[1];
        reasoner.SupportsTools.Should().BeFalse();
        reasoner.SupportsStreaming.Should().BeTrue();
        reasoner.SupportsEmbeddings.Should().BeFalse();
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
        result.Value[0].Provider.Should().Be("deepseek");
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

        // Assert — parameters should be absent (not serialised)
        var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("tools").EnumerateArray().First()
            .GetProperty("function").TryGetProperty("parameters", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_WithEmptyToolList_OmitsToolsField()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = TestFactories.SimpleCompletionRequest().WithTools(new List<AgentTool>());

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        doc.RootElement.TryGetProperty("tools", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_WithEmptyToolChoiceString_OmitsToolChoice()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        // Inject directly via AdditionalParameters so we can exercise the empty-string path.
        var request = TestFactories.SimpleCompletionRequest() with
        {
            AdditionalParameters = new Dictionary<string, object>
            {
                [DeepSeekToolCallingExtensions.ToolsKey] = new List<AgentTool> { new("foo", "bar") },
                [DeepSeekToolCallingExtensions.ToolChoiceKey] = string.Empty
            }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        doc.RootElement.TryGetProperty("tool_choice", out _).Should().BeFalse();
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
          "model": "deepseek-chat",
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

    // ---------- Reasoning model handling ----------

    [Fact]
    public async Task CompleteAsync_WithReasonerResponse_SurfacesReasoningContentInMetadata()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "id": "chatcmpl-r1",
          "model": "deepseek-reasoner",
          "created": 0,
          "choices": [
            {
              "index": 0,
              "message": {
                "role": "assistant",
                "content": "The answer is 42.",
                "reasoning_content": "Let me think step by step. First, the meaning of life..."
              },
              "finish_reason": "stop"
            }
          ]
        }
        """;
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(
            TestFactories.SimpleCompletionRequest(DeepSeekOptions.ReasonerModel),
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be("The answer is 42.");
        result.Value.GetReasoningContent().Should().Be("Let me think step by step. First, the meaning of life...");
    }

    [Fact]
    public async Task CompleteAsync_WithReasonerResponse_AndInlineOption_PrependsThinkTagsToContent()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.InlineReasoningInContent = true);
        var sut = TestFactories.CreateProvider(httpClient, o => o.InlineReasoningInContent = true);
        var json = """
        {
          "id": "chatcmpl-r1",
          "model": "deepseek-reasoner",
          "created": 0,
          "choices": [
            {
              "index": 0,
              "message": {
                "role": "assistant",
                "content": "Answer: 42.",
                "reasoning_content": "Long form trace."
              },
              "finish_reason": "stop"
            }
          ]
        }
        """;
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(
            TestFactories.SimpleCompletionRequest(DeepSeekOptions.ReasonerModel),
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be("<think>Long form trace.</think>Answer: 42.");
        result.Value.GetReasoningContent().Should().Be("Long form trace.");
    }

    [Fact]
    public async Task CompleteAsync_WithReasonerResponse_AndNoReasoningField_OmitsMetadata()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "id": "chatcmpl-rc",
          "model": "deepseek-chat",
          "created": 0,
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": "hi" }, "finish_reason": "stop" }
          ]
        }
        """;
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Metadata.Should().BeNull();
        result.Value.GetReasoningContent().Should().BeNull();
    }

    [Fact]
    public async Task StreamCompleteAsync_WithReasonerStream_AndDefaultOptions_DropsPureReasoningDeltas()
    {
        // Arrange — content-only chunks pass through; pure reasoning_content chunks are dropped.
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var stream = string.Join("\n",
            "data: {\"id\":\"r1\",\"model\":\"deepseek-reasoner\",\"choices\":[{\"delta\":{\"reasoning_content\":\"Hmm.\"}}]}",
            "data: {\"id\":\"r1\",\"model\":\"deepseek-reasoner\",\"choices\":[{\"delta\":{\"reasoning_content\":\" Let me try...\"}}]}",
            "data: {\"id\":\"r1\",\"model\":\"deepseek-reasoner\",\"choices\":[{\"delta\":{\"content\":\"42\"}}]}",
            "data: {\"id\":\"r1\",\"model\":\"deepseek-reasoner\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}",
            "data: [DONE]",
            string.Empty);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("text/event-stream", stream);

        // Act
        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(
            TestFactories.SimpleCompletionRequest(DeepSeekOptions.ReasonerModel),
            CancellationToken.None))
        {
            r.IsSuccess.Should().BeTrue();
            chunks.Add(r.Value);
        }

        // Assert
        chunks.Should().HaveCount(2);
        chunks[0].ContentDelta.Should().Be("42");
        chunks[0].IsFinal.Should().BeFalse();
        chunks[0].Index.Should().Be(0);
        chunks[1].IsFinal.Should().BeTrue();
        chunks[1].Index.Should().Be(1);
    }

    [Fact]
    public async Task StreamCompleteAsync_WithReasonerStream_AndInlineOption_TagsReasoningDeltas()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.InlineReasoningInContent = true);
        var sut = TestFactories.CreateProvider(httpClient, o => o.InlineReasoningInContent = true);
        var stream = string.Join("\n",
            "data: {\"id\":\"r1\",\"model\":\"deepseek-reasoner\",\"choices\":[{\"delta\":{\"reasoning_content\":\"think\"}}]}",
            "data: {\"id\":\"r1\",\"model\":\"deepseek-reasoner\",\"choices\":[{\"delta\":{\"content\":\"42\"}}]}",
            "data: {\"id\":\"r1\",\"model\":\"deepseek-reasoner\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}",
            "data: [DONE]",
            string.Empty);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("text/event-stream", stream);

        // Act
        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(
            TestFactories.SimpleCompletionRequest(DeepSeekOptions.ReasonerModel),
            CancellationToken.None))
        {
            r.IsSuccess.Should().BeTrue();
            chunks.Add(r.Value);
        }

        // Assert
        chunks.Should().HaveCount(3);
        chunks[0].ContentDelta.Should().Be("<think>think</think>");
        chunks[0].IsFinal.Should().BeFalse();
        chunks[1].ContentDelta.Should().Be("42");
        chunks[2].IsFinal.Should().BeTrue();
    }
}
