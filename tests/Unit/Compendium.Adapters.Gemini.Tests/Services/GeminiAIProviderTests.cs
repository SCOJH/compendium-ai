// -----------------------------------------------------------------------
// <copyright file="GeminiAIProviderTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Gemini.Http.Models;
using Compendium.Adapters.Gemini.Services;
using Compendium.Adapters.Gemini.StructuredOutputs;
using Compendium.Adapters.Gemini.Tests.TestSupport;
using Compendium.Adapters.Gemini.Tools;

namespace Compendium.Adapters.Gemini.Tests.Services;

public class GeminiAIProviderTests
{
    [Fact]
    public void ProviderId_Always_ReturnsGemini()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var id = sut.ProviderId;

        // Assert
        id.Should().Be("gemini");
    }

    // ---------- CompleteAsync ----------

    [Fact]
    public async Task CompleteAsync_OnSuccess_MapsCandidateToCompletionResponse()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "candidates": [
            {
              "content": { "role": "model", "parts": [ { "text": "Hello world" } ] },
              "finishReason": "STOP",
              "index": 0
            }
          ],
          "usageMetadata": { "promptTokenCount": 12, "candidatesTokenCount": 3, "totalTokenCount": 15 },
          "modelVersion": "gemini-2.0-flash"
        }
        """;
        handler.When(HttpMethod.Post, "*:generateContent*").Respond("application/json", json);

        var request = new CompletionRequest
        {
            Model = "gemini-2.0-flash",
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
            StopSequences = new List<string> { "###" }
        };

        // Act
        var result = await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Model.Should().Be("gemini-2.0-flash");
        result.Value.Content.Should().Be("Hello world");
        result.Value.FinishReason.Should().Be(FinishReason.Stop);
        result.Value.Usage.PromptTokens.Should().Be(12);
        result.Value.Usage.CompletionTokens.Should().Be(3);
        result.Value.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
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
    public async Task CompleteAsync_WithEmptyCandidates_ReturnsEmptyContentAndInProgressReason()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*:generateContent*")
            .Respond("application/json", """{"candidates":[]}""");

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
    public async Task CompleteAsync_WithEmptyModel_UsesDefaultModel()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "gemini-1.5-pro");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "gemini-1.5-pro");
        string? capturedUri = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { capturedUri = req.RequestUri!.ToString(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        var request = new CompletionRequest
        {
            Model = string.Empty,
            Messages = new List<Message> { Message.User("hi") }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        capturedUri.Should().NotBeNull();
        capturedUri!.Should().Contain("/models/gemini-1.5-pro:generateContent");
    }

    [Fact]
    public async Task CompleteAsync_WithMaxTokensNull_AppliesDefaultMaxTokens()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultMaxTokens = 1234);
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultMaxTokens = 1234);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        body.Should().Contain("\"maxOutputTokens\":1234");
    }

    [Fact]
    public async Task CompleteAsync_WithoutTemperature_SendsTheFallbackTemperature()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        body!.Should().Contain("\"temperature\":0.7", "an unset temperature keeps sending the former default");
    }

    [Fact]
    public async Task CompleteAsync_WithSystemPrompt_UsesSystemInstructionField()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

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
        doc.RootElement.TryGetProperty("systemInstruction", out var sys).Should().BeTrue();
        sys.GetProperty("parts").EnumerateArray().First()
            .GetProperty("text").GetString().Should().Be("You are helpful.");

        // user contents do NOT include the system prompt.
        var contents = doc.RootElement.GetProperty("contents").EnumerateArray().ToList();
        contents.Should().ContainSingle();
        contents[0].GetProperty("role").GetString().Should().Be("user");
    }

    [Fact]
    public async Task CompleteAsync_WithoutSystemPrompt_OmitsSystemInstruction()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        doc.RootElement.TryGetProperty("systemInstruction", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_MapsAssistantRoleToModel()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        var request = new CompletionRequest
        {
            Model = "m",
            Messages = new List<Message> { Message.User("q"), Message.Assistant("a") }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var roles = doc.RootElement.GetProperty("contents").EnumerateArray()
            .Select(c => c.GetProperty("role").GetString()).ToList();
        roles.Should().Equal("user", "model");
    }

    [Fact]
    public async Task CompleteAsync_ToolRoleMessage_BecomesFunctionResponse()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        var request = new CompletionRequest
        {
            Model = "m",
            Messages = new List<Message>
            {
                Message.User("call the tool"),
                new() { Role = MessageRole.Tool, Name = "get_weather", Content = """{"tempC":21}""" }
            }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var contents = doc.RootElement.GetProperty("contents").EnumerateArray().ToList();
        contents.Should().HaveCount(2);
        var fr = contents[1].GetProperty("parts").EnumerateArray().First().GetProperty("functionResponse");
        fr.GetProperty("name").GetString().Should().Be("get_weather");
        fr.GetProperty("response").GetProperty("tempC").GetInt32().Should().Be(21);
    }

    [Fact]
    public async Task CompleteAsync_ToolRoleWithNonJsonContent_WrapsInResultField()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        var request = new CompletionRequest
        {
            Model = "m",
            Messages = new List<Message>
            {
                new() { Role = MessageRole.Tool, Name = "get_weather", Content = "sunny, 21°C" }
            }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var fr = doc.RootElement.GetProperty("contents").EnumerateArray().First()
            .GetProperty("parts").EnumerateArray().First().GetProperty("functionResponse");
        fr.GetProperty("response").GetProperty("result").GetString().Should().Be("sunny, 21°C");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AI.InvalidApiKey")]
    [InlineData(HttpStatusCode.Forbidden, "AI.InvalidApiKey")]
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
        handler.When(HttpMethod.Post, "*:generateContent*")
            .Respond(status, "application/json", """{"error":{"code":1,"message":"oops","status":"FAILED_PRECONDITION"}}""");

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
        handler.When(HttpMethod.Post, "*:generateContent*")
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
        handler.When(HttpMethod.Post, "*:generateContent*")
            .Respond("application/json", "not valid json");

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
        handler.When(HttpMethod.Post, "*:generateContent*")
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
        handler.When(HttpMethod.Post, "*:generateContent*")
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
        handler.When(HttpMethod.Post, "*:generateContent*")
            .Throw(new TaskCanceledException("cancelled"));

        // Act
        var act = async () => await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), cts.Token);

        // Assert
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Theory]
    [InlineData("STOP", FinishReason.Stop)]
    [InlineData("stop", FinishReason.Stop)] // case insensitive
    [InlineData("MAX_TOKENS", FinishReason.Length)]
    [InlineData("SAFETY", FinishReason.ContentFilter)]
    [InlineData("BLOCKLIST", FinishReason.ContentFilter)]
    [InlineData("PROHIBITED_CONTENT", FinishReason.ContentFilter)]
    [InlineData("SPII", FinishReason.ContentFilter)]
    [InlineData("RECITATION", FinishReason.ContentFilter)]
    [InlineData("TOOL_CALL", FinishReason.ToolCall)]
    [InlineData("FUNCTION_CALL", FinishReason.ToolCall)]
    [InlineData("UNKNOWN_OTHER", FinishReason.Other)]
    public async Task CompleteAsync_MapsFinishReasonCorrectly(string apiReason, FinishReason expected)
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = $$"""
        {
          "candidates": [
            { "content": { "role": "model", "parts": [ { "text": "" } ] }, "finishReason": "{{apiReason}}" }
          ]
        }
        """;
        handler.When(HttpMethod.Post, "*:generateContent*").Respond("application/json", json);

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
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"He\"}]}}]}",
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"llo\"}]}}]}",
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"!\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":3,\"candidatesTokenCount\":2,\"totalTokenCount\":5}}",
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"never\"}]}}]}",
            "data: [DONE]",
            string.Empty);
        handler.When(HttpMethod.Post, "*:streamGenerateContent*").Respond("text/event-stream", stream);

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
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"X\"}]},\"finishReason\":\"STOP\"}]}",
            "data: [DONE]",
            string.Empty);
        handler.When(HttpMethod.Post, "*:streamGenerateContent*").Respond("text/event-stream", stream);

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
    public async Task StreamCompleteAsync_WithEmptyModel_UsesDefaultModel_AndStreamsViaSseUri()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "gemini-2.0-flash");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "gemini-2.0-flash");
        string? capturedUri = null;
        handler.When(HttpMethod.Post, "*:streamGenerateContent*")
            .With(req => { capturedUri = req.RequestUri!.ToString(); return true; })
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
        capturedUri.Should().NotBeNull();
        capturedUri!.Should().Contain("/models/gemini-2.0-flash:streamGenerateContent");
        capturedUri.Should().Contain("alt=sse");
    }

    [Fact]
    public async Task StreamCompleteAsync_OnError_YieldsFailureOnceAndStops()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*:streamGenerateContent*")
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
    public async Task StreamCompleteAsync_ToolCallInFinalChunk_MapsToToolCallFinishReason()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var stream = string.Join("\n",
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"name\":\"get_weather\",\"args\":{\"city\":\"Paris\"}}}]},\"finishReason\":\"STOP\"}]}",
            "data: [DONE]",
            string.Empty);
        handler.When(HttpMethod.Post, "*:streamGenerateContent*").Respond("text/event-stream", stream);

        // Act
        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None))
        {
            chunks.Add(r.Value);
        }

        // Assert
        chunks.Should().ContainSingle();
        chunks[0].IsFinal.Should().BeTrue();
        chunks[0].FinishReason.Should().Be(FinishReason.ToolCall);
    }

    // ---------- EmbedAsync ----------

    [Fact]
    public async Task EmbedAsync_SingleInput_UsesEmbedContent()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "embedding": { "values": [0.1, 0.2, 0.3] }
        }
        """;
        handler.When(HttpMethod.Post, "*:embedContent*").Respond("application/json", json);

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(1), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Embeddings.Should().ContainSingle();
        result.Value.Embeddings[0].Vector.Should().Equal(0.1f, 0.2f, 0.3f);
        result.Value.Model.Should().Be("text-embedding-004");
    }

    [Fact]
    public async Task EmbedAsync_MultipleInputs_UsesBatchEmbedContents_AggregatesEmbeddings()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "embeddings": [
            { "values": [0.1, 0.2] },
            { "values": [0.3, 0.4] }
          ]
        }
        """;
        handler.When(HttpMethod.Post, "*:batchEmbedContents*").Respond("application/json", json);

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(2), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Embeddings.Should().HaveCount(2);
        result.Value.Embeddings[0].Vector.Should().Equal(0.1f, 0.2f);
        result.Value.Embeddings[1].Vector.Should().Equal(0.3f, 0.4f);
    }

    [Fact]
    public async Task EmbedAsync_LargeInputs_BatchesByMaxEmbeddingsBatchSize()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.MaxEmbeddingsBatchSize = 2);
        var sut = TestFactories.CreateProvider(httpClient, o => o.MaxEmbeddingsBatchSize = 2);

        var callCount = 0;
        handler.When(HttpMethod.Post, "*:batchEmbedContents*").Respond(req =>
        {
            callCount++;
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var inputs = doc.RootElement.GetProperty("requests").GetArrayLength();
            var embeddings = string.Join(",", Enumerable.Range(0, inputs).Select(i =>
                $"{{\"values\":[{i * 0.1f}]}}"));
            var responseJson = $"{{\"embeddings\":[{embeddings}]}}";
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
    }

    [Fact]
    public async Task EmbedAsync_WithEmptyInputs_ReturnsInvalidRequest()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var request = new EmbeddingRequest { Model = "text-embedding-004", Inputs = new List<string>() };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
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

    [Fact]
    public async Task EmbedAsync_WithEmptyModel_UsesDefaultEmbeddingModel()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultEmbeddingModel = "text-embedding-004");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultEmbeddingModel = "text-embedding-004");
        string? body = null;
        handler.When(HttpMethod.Post, "*:embedContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"embedding":{"values":[0.1]}}""");

        var request = new EmbeddingRequest
        {
            Model = string.Empty,
            Inputs = new List<string> { "x" }
        };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        body!.Should().Contain("models/text-embedding-004");
        result.Value.Model.Should().Be("text-embedding-004");
    }

    [Fact]
    public async Task EmbedAsync_SingleInput_OnHttpError_ReturnsFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*:embedContent*")
            .Respond(HttpStatusCode.Unauthorized, "application/json", """{"error":{"message":"bad key"}}""");

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(1), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Fact]
    public async Task EmbedAsync_BatchedInputs_OnHttpError_ReturnsFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*:batchEmbedContents*")
            .Respond(HttpStatusCode.Unauthorized, "application/json", """{"error":{"message":"bad"}}""");

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(3), CancellationToken.None);

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
        handler.When(HttpMethod.Post, "*:embedContent*").Throw(new TaskCanceledException("slow"));

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(1), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.Timeout");
    }

    [Fact]
    public async Task EmbedAsync_PropagatesDimensions()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:embedContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"embedding":{"values":[0.1]}}""");

        var request = new EmbeddingRequest
        {
            Model = "text-embedding-004",
            Inputs = new List<string> { "hi" },
            Dimensions = 256
        };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        body!.Should().Contain("\"outputDimensionality\":256");
    }

    [Fact]
    public async Task EmbedAsync_ModelWithModelsPrefix_PreservedAsFullyQualifiedInBody()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:embedContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"embedding":{"values":[0.1]}}""");

        var request = new EmbeddingRequest
        {
            Model = "models/text-embedding-004",
            Inputs = new List<string> { "x" }
        };

        // Act
        await sut.EmbedAsync(request, CancellationToken.None);

        // Assert — body keeps single "models/" prefix, no double-prefix bug.
        body!.Should().Contain("\"model\":\"models/text-embedding-004\"");
        body.Should().NotContain("models/models/");
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
          "models": [
            {
              "name": "models/gemini-2.0-flash",
              "displayName": "Gemini 2.0 Flash",
              "inputTokenLimit": 1000000,
              "outputTokenLimit": 8192,
              "supportedGenerationMethods": ["generateContent", "streamGenerateContent"]
            },
            {
              "name": "models/text-embedding-004",
              "displayName": "Text Embedding 004",
              "supportedGenerationMethods": ["embedContent"]
            }
          ]
        }
        """;
        handler.When(HttpMethod.Get, "*/models*").Respond("application/json", json);

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        var chat = result.Value[0];
        chat.Id.Should().Be("gemini-2.0-flash");
        chat.Provider.Should().Be("gemini");
        chat.SupportsStreaming.Should().BeTrue();
        chat.SupportsEmbeddings.Should().BeFalse();
        chat.SupportsTools.Should().BeTrue();
        chat.SupportsVision.Should().BeTrue();
        chat.ContextWindow.Should().Be(1000000);
        chat.MaxOutputTokens.Should().Be(8192);

        var embed = result.Value[1];
        embed.SupportsEmbeddings.Should().BeTrue();
        embed.SupportsTools.Should().BeFalse();
        embed.SupportsStreaming.Should().BeFalse();
    }

    [Fact]
    public async Task ListModelsAsync_OnFailure_PropagatesError()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/models*")
            .Respond(HttpStatusCode.InternalServerError, "application/json", "{}");

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
    }

    [Fact]
    public async Task ListModelsAsync_ModelWithoutModelsPrefix_IsHandled()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/models*")
            .Respond("application/json", """{"models":[{"name":"my-model"}]}""");

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value[0].Id.Should().Be("my-model");
        result.Value[0].Provider.Should().Be("gemini");
    }

    // ---------- HealthCheckAsync ----------

    [Fact]
    public async Task HealthCheckAsync_WhenModelsListSucceeds_ReturnsSuccess()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/models*").Respond("application/json", "{\"models\":[]}");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HealthCheckAsync_WhenUnderlyingRethrowsOnCancellation_ReturnsProviderUnavailable()
    {
        // Arrange — cancelled caller + the handler throws TaskCanceledException. The HttpClient
        // rethrows under cancellation, and HealthCheckAsync's catch-all maps to ProviderUnavailable.
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Get, "*/models*").Throw(new TaskCanceledException("cancelled"));

        // Act
        var result = await sut.HealthCheckAsync(cts.Token);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderUnavailable");
    }

    [Fact]
    public async Task HealthCheckAsync_WhenModelsListFails_ReturnsFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/models*")
            .Respond(HttpStatusCode.Unauthorized, "application/json", """{"error":{"message":"x"}}""");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    // ---------- Tool calling ----------

    [Fact]
    public async Task CompleteAsync_WithTools_SerializesToolsArrayWithFunctionDeclarations()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

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
        var fns = toolsEl[0].GetProperty("functionDeclarations").EnumerateArray().ToList();
        fns.Should().ContainSingle();
        fns[0].GetProperty("name").GetString().Should().Be("get_weather");
        fns[0].GetProperty("description").GetString().Should().Be("Get current weather for a city.");
        fns[0].GetProperty("parameters").GetProperty("type").GetString().Should().Be("object");

        doc.RootElement.GetProperty("toolConfig").GetProperty("functionCallingConfig")
            .GetProperty("mode").GetString().Should().Be("AUTO");
    }

    [Theory]
    [InlineData("auto", "AUTO", null)]
    [InlineData("any", "ANY", null)]
    [InlineData("required", "ANY", null)]
    [InlineData("none", "NONE", null)]
    [InlineData("my_function", "ANY", "my_function")]
    public async Task CompleteAsync_ToolChoice_MapsToFunctionCallingConfigMode(
        string toolChoice,
        string expectedMode,
        string? expectedAllowedName)
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        var request = TestFactories.SimpleCompletionRequest()
            .WithTools(new List<AgentTool> { new("my_function", "desc") }, toolChoice);

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var cfg = doc.RootElement.GetProperty("toolConfig").GetProperty("functionCallingConfig");
        cfg.GetProperty("mode").GetString().Should().Be(expectedMode);
        if (expectedAllowedName != null)
        {
            cfg.GetProperty("allowedFunctionNames").EnumerateArray()
                .First().GetString().Should().Be(expectedAllowedName);
        }
        else
        {
            cfg.TryGetProperty("allowedFunctionNames", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task CompleteAsync_WithMalformedToolSchema_OmitsParameters()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        var tools = new List<AgentTool> { new("foo", "desc", "{not json") };
        var request = TestFactories.SimpleCompletionRequest().WithTools(tools);

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert — parameters should be absent (not serialised)
        var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("tools").EnumerateArray().First()
            .GetProperty("functionDeclarations").EnumerateArray().First()
            .TryGetProperty("parameters", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_WhenModelEmitsFunctionCall_SurfacesAgentToolInvocations()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "candidates": [
            {
              "content": {
                "role": "model",
                "parts": [
                  {
                    "functionCall": {
                      "name": "get_weather",
                      "args": { "city": "Paris" }
                    }
                  }
                ]
              },
              "finishReason": "STOP"
            }
          ]
        }
        """;
        handler.When(HttpMethod.Post, "*:generateContent*").Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.FinishReason.Should().Be(FinishReason.ToolCall); // Gemini reports STOP, we promote to ToolCall when tool calls are present
        var calls = result.Value.GetToolCalls();
        calls.Should().ContainSingle();
        calls[0].ToolName.Should().Be("get_weather");
        calls[0].ArgumentsJson.Should().Contain("Paris");
        calls[0].IsError.Should().BeFalse();
        calls[0].ResultText.Should().BeEmpty();
        calls[0].Latency.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task CompleteAsync_FunctionCallWithoutArgs_ProducesEmptyArgsJson()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "candidates": [
            {
              "content": { "role": "model", "parts": [ { "functionCall": { "name": "ping" } } ] }
            }
          ]
        }
        """;
        handler.When(HttpMethod.Post, "*:generateContent*").Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        var calls = result.Value.GetToolCalls();
        calls.Should().ContainSingle();
        calls[0].ArgumentsJson.Should().Be("{}");
    }

    // ---------- Structured outputs ----------

    [Fact]
    public async Task CompleteAsync_WithStructuredOutput_AppliesResponseMimeTypeAndSchema()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        var schema = """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""";
        var request = TestFactories.SimpleCompletionRequest().WithStructuredOutput(schema);

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var gen = doc.RootElement.GetProperty("generationConfig");
        gen.GetProperty("responseMimeType").GetString().Should().Be("application/json");
        gen.GetProperty("responseSchema").GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public async Task CompleteAsync_WithJsonMode_AppliesResponseMimeTypeOnly()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        var request = TestFactories.SimpleCompletionRequest().WithJsonMode();

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var gen = doc.RootElement.GetProperty("generationConfig");
        gen.GetProperty("responseMimeType").GetString().Should().Be("application/json");
        gen.TryGetProperty("responseSchema", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_WithStructuredByDefaultOption_AppliesResponseMimeType()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.UseStructuredOutputsByDefault = true);
        var sut = TestFactories.CreateProvider(httpClient, o => o.UseStructuredOutputsByDefault = true);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString()
            .Should().Be("application/json");
    }

    // ---------- Vision ----------

    [Fact]
    public async Task CompleteAsync_WithVisionParts_NoExistingContents_AppendsNewUserContent()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        var visionParts = new List<GeminiPart>
        {
            new() { InlineData = new GeminiInlineData { MimeType = "image/png", Data = "BASE64" } }
        };

        // Empty messages list -> apiRequest.Contents starts empty -> ApplyVisionParts adds a new user content.
        var request = new CompletionRequest
        {
            Model = "m",
            Messages = new List<Message>(),
            AdditionalParameters = new Dictionary<string, object>
            {
                [GeminiAIProvider.VisionPartsKey] = (IReadOnlyList<GeminiPart>)visionParts
            }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var contents = doc.RootElement.GetProperty("contents").EnumerateArray().ToList();
        contents.Should().ContainSingle();
        contents[0].GetProperty("role").GetString().Should().Be("user");
        contents[0].GetProperty("parts").EnumerateArray().First()
            .GetProperty("inlineData").GetProperty("data").GetString().Should().Be("BASE64");
    }

    [Fact]
    public async Task CompleteAsync_WithSystemRoleMessage_RoutesAsUser()
    {
        // Arrange — System-role messages should normally come via SystemPrompt, but if a caller
        // sneaks one into the Messages list we still map it to a user content (no crash).
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        var request = new CompletionRequest
        {
            Model = "m",
            Messages = new List<Message>
            {
                Message.System("be brief"),
                Message.User("hi")
            }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var roles = doc.RootElement.GetProperty("contents").EnumerateArray()
            .Select(c => c.GetProperty("role").GetString()).ToList();
        roles.Should().Equal("user", "user");
    }

    [Fact]
    public async Task CompleteAsync_WithVisionParts_AppendsToLastUserContent()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*:generateContent*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"candidates":[]}""");

        var visionParts = new List<GeminiPart>
        {
            new() { InlineData = new GeminiInlineData { MimeType = "image/png", Data = "BASE64DATA" } },
            new() { FileData = new GeminiFileData { MimeType = "image/jpeg", FileUri = "https://files.example/img.jpg" } }
        };

        var request = TestFactories.SimpleCompletionRequest() with
        {
            AdditionalParameters = new Dictionary<string, object>
            {
                [GeminiAIProvider.VisionPartsKey] = (IReadOnlyList<GeminiPart>)visionParts
            }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var lastUserParts = doc.RootElement.GetProperty("contents")
            .EnumerateArray().Last(c => c.GetProperty("role").GetString() == "user")
            .GetProperty("parts").EnumerateArray().ToList();

        // Original text + 2 vision parts = 3 total.
        lastUserParts.Should().HaveCount(3);
        lastUserParts.Select(p => p.TryGetProperty("inlineData", out var ignored1) ? ignored1.ValueKind : JsonValueKind.Undefined)
            .Should().Contain(JsonValueKind.Object);
        lastUserParts.Select(p => p.TryGetProperty("fileData", out var ignored2) ? ignored2.ValueKind : JsonValueKind.Undefined)
            .Should().Contain(JsonValueKind.Object);
    }
}
