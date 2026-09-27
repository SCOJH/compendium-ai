// -----------------------------------------------------------------------
// <copyright file="MercuryAIProviderTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Mercury.Http;
using Compendium.Adapters.Mercury.Tests.TestSupport;

namespace Compendium.Adapters.Mercury.Tests.Services;

/// <summary>
/// Unit tests for <see cref="MercuryAIProvider"/>. Uses MockHttp to drive the underlying HTTP client.
/// </summary>
public class MercuryAIProviderTests
{
    [Fact]
    public void ProviderId_Always_ReturnsMercury()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var id = sut.ProviderId;

        // Assert
        id.Should().Be("mercury");
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
          "id": "mer-1",
          "model": "mercury",
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
            Model = "mercury",
            Messages = new List<Message>
            {
                Message.User("Hi"),
                Message.Assistant("Yes?"),
                new Message { Role = MessageRole.User, Content = "Tell me a joke", Name = "alice" },
            },
            SystemPrompt = "Be concise.",
            Temperature = 0.5f,
            MaxTokens = 256,
            TopP = 0.9f,
            FrequencyPenalty = 0.1f,
            PresencePenalty = 0.2f,
            StopSequences = new List<string> { "###" },
        };

        // Act
        var result = await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be("mer-1");
        result.Value.Model.Should().Be("mercury");
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
    }

    [Fact]
    public async Task CompleteAsync_WithNoModel_UsesDefaultFromOptions()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "mercury-coder");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "mercury-coder");
        string? capturedBody = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req =>
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"mercury-coder","created":0,"choices":[]}""");

        // Act
        var request = new CompletionRequest
        {
            Model = null!,
            Messages = new List<Message> { Message.User("hi") },
        };
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("mercury-coder");
    }

    [Fact]
    public async Task CompleteAsync_WithMaxTokensNull_AppliesDefaultMaxTokensFromOptions()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultMaxTokens = 1234);
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultMaxTokens = 1234);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
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
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
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
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = "mercury",
            SystemPrompt = "You are helpful.",
            Messages = new List<Message> { Message.User("hi") },
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        body.Should().NotBeNullOrEmpty();
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
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var roles = doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString()).ToList();
        roles.Should().NotContain("system");
    }

    [Fact]
    public async Task CompleteAsync_WithMessageName_SerialisesName()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = "mercury",
            Messages = new List<Message>
            {
                new Message { Role = MessageRole.User, Content = "ping", Name = "alice" },
            },
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        body.Should().Contain("\"name\":\"alice\"");
    }

    [Fact]
    public async Task CompleteAsync_OnHttpError_ReturnsFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(HttpStatusCode.Unauthorized, "application/json", "{\"error\":{\"message\":\"bad key\"}}");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
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
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": "" }, "finish_reason": "{{apiReason}}" }
          ]
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
        var stream = string.Join(
            "\n",
            "data: {\"id\":\"c1\",\"model\":\"mercury\",\"choices\":[{\"delta\":{\"content\":\"He\"}}]}",
            "data: {\"id\":\"c1\",\"model\":\"mercury\",\"choices\":[{\"delta\":{\"content\":\"llo\"}}]}",
            "data: {\"id\":\"c1\",\"model\":\"mercury\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}",
            "data: {\"id\":\"c1\",\"model\":\"mercury\",\"choices\":[{\"delta\":{\"content\":\"never\"}}]}",
            "data: [DONE]",
            string.Empty);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond("text/event-stream", stream);

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
    public async Task StreamCompleteAsync_WithNoModel_UsesDefaultFromOptions()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "mercury-2");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "mercury-2");
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("text/event-stream", "data: [DONE]\n");

        var request = new CompletionRequest
        {
            Model = null!,
            Messages = new List<Message> { Message.User("hi") },
        };

        // Act
        await foreach (var _ in sut.StreamCompleteAsync(request, CancellationToken.None))
        {
        }

        // Assert
        body.Should().NotBeNull();
        body!.Should().Contain("mercury-2");
        body!.Should().Contain("\"stream\":true");
    }

    [Fact]
    public async Task StreamCompleteAsync_WhenChunkReturnsFailure_YieldsFailureAndStops()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions")
            .Respond(HttpStatusCode.TooManyRequests, "application/json", "{\"error\":{\"message\":\"limit\"}}");

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
    public async Task EmbedAsync_Always_ReturnsInvalidRequestFailure()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var request = new EmbeddingRequest { Model = "mercury", Inputs = new List<string> { "a", "b" } };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
        result.Error.Message.Should().Contain("Mercury does not expose an embeddings endpoint");
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
            {
              "id": "mercury",
              "name": "Inception: Mercury",
              "context_length": 128000,
              "max_output_length": 32000,
              "input_modalities": ["text"],
              "supported_features": ["tools","json_mode","structured_outputs"],
              "pricing": { "prompt": "0.00000025", "completion": "0.00000075" }
            },
            {
              "id": "mercury-edit",
              "context_length": 128000,
              "max_output_length": 32000,
              "input_modalities": ["text"],
              "supported_features": [],
              "pricing": { "prompt": "not-a-number", "completion": null }
            },
            { "id": "mercury-vision", "input_modalities": ["text","image"] }
          ]
        }
        """;
        handler.When(HttpMethod.Get, "*/models").Respond("application/json", json);

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);

        var mercury = result.Value[0];
        mercury.Id.Should().Be("mercury");
        mercury.Name.Should().Be("Inception: Mercury");
        mercury.Provider.Should().Be("mercury");
        mercury.ContextWindow.Should().Be(128000);
        mercury.MaxOutputTokens.Should().Be(32000);
        mercury.SupportsStreaming.Should().BeTrue();
        mercury.SupportsEmbeddings.Should().BeFalse();
        mercury.SupportsVision.Should().BeFalse();
        mercury.SupportsTools.Should().BeTrue();
        mercury.PricingInputPerMillion.Should().Be(0.25m); // 0.00000025 * 1_000_000
        mercury.PricingOutputPerMillion.Should().Be(0.75m); // 0.00000075 * 1_000_000

        var edit = result.Value[1];
        edit.Id.Should().Be("mercury-edit");
        edit.Name.Should().Be("mercury-edit"); // falls back to id when name missing
        edit.SupportsTools.Should().BeFalse();
        edit.PricingInputPerMillion.Should().BeNull(); // unparseable
        edit.PricingOutputPerMillion.Should().BeNull(); // null

        var vision = result.Value[2];
        vision.SupportsVision.Should().BeTrue();
    }

    [Fact]
    public async Task ListModelsAsync_OnFailure_PropagatesError()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/models")
            .Respond(HttpStatusCode.InternalServerError, "application/json", "{}");

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
        handler.When(HttpMethod.Get, "*/models")
            .Respond("application/json", "{\"data\":[]}");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HealthCheckAsync_WhenModelsListReturnsFailure_ReturnsFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/models")
            .Respond(HttpStatusCode.Unauthorized, "application/json", "{\"error\":{\"message\":\"x\"}}");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Fact]
    public async Task HealthCheckAsync_WhenListModelsAsyncRethrowsTaskCanceled_ReturnsProviderUnavailable()
    {
        // Arrange — when caller has already cancelled, ListModelsAsync rethrows TaskCanceledException;
        // HealthCheckAsync wraps it in catch (Exception ex) → ProviderUnavailable.
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Get, "*/models")
            .Throw(new TaskCanceledException("user cancel"));
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var result = await sut.HealthCheckAsync(cts.Token);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderUnavailable");
    }
}
