// -----------------------------------------------------------------------
// <copyright file="OllamaAIProviderTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Ollama.Tests.TestSupport;
using Compendium.Adapters.Ollama.Tools;

namespace Compendium.Adapters.Ollama.Tests.Services;

/// <summary>
/// Unit tests for <see cref="OllamaAIProvider"/>. All HTTP traffic is mocked via
/// <see cref="MockHttpMessageHandler"/>.
/// </summary>
public class OllamaAIProviderTests
{
    [Fact]
    public void ProviderId_Always_ReturnsOllama()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var id = sut.ProviderId;

        // Assert
        id.Should().Be("ollama");
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
          "model": "llama3.2",
          "created_at": "2026-05-17T10:00:00.123Z",
          "message": { "role": "assistant", "content": "Hello world" },
          "done": true,
          "done_reason": "stop",
          "total_duration": 1234567,
          "prompt_eval_count": 12,
          "eval_count": 3
        }
        """;
        handler.When(HttpMethod.Post, "*/api/chat").Respond("application/json", json);

        var request = new CompletionRequest
        {
            Model = "llama3.2",
            Messages = new List<Message>
            {
                Message.User("Hi"),
                Message.Assistant("Yes?"),
                Message.User("Tell me a joke"),
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
        result.Value.Model.Should().Be("llama3.2");
        result.Value.Content.Should().Be("Hello world");
        result.Value.FinishReason.Should().Be(FinishReason.Stop);
        result.Value.Usage.PromptTokens.Should().Be(12);
        result.Value.Usage.CompletionTokens.Should().Be(3);
        result.Value.CreatedAt.Should().BeCloseTo(
            new DateTime(2026, 5, 17, 10, 0, 0, 123, DateTimeKind.Utc),
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CompleteAsync_BuildsCorrectRequestBody()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o =>
        {
            o.KeepAlive = "30m";
            o.DefaultMaxTokens = 999;
        });
        var sut = TestFactories.CreateProvider(httpClient, o =>
        {
            o.KeepAlive = "30m";
            o.DefaultMaxTokens = 999;
        });

        string? capturedBody = null;
        handler.When(HttpMethod.Post, "*/api/chat")
            .With(req =>
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json",
                """{"model":"llama3.2","message":{"role":"assistant","content":"hi"},"done":true,"done_reason":"stop"}""");

        var request = new CompletionRequest
        {
            Model = "llama3.2",
            SystemPrompt = "be helpful",
            Messages = new List<Message> { Message.User("hello") },
            Temperature = 0.3f,
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        capturedBody.Should().NotBeNull();
        var doc = JsonDocument.Parse(capturedBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("llama3.2");
        doc.RootElement.GetProperty("stream").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("keep_alive").GetString().Should().Be("30m");

        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        messages.Should().HaveCount(2);
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages[0].GetProperty("content").GetString().Should().Be("be helpful");
        messages[1].GetProperty("role").GetString().Should().Be("user");

        var opts = doc.RootElement.GetProperty("options");
        opts.GetProperty("temperature").GetSingle().Should().Be(0.3f);
        opts.GetProperty("num_predict").GetInt32().Should().Be(999); // DefaultMaxTokens applied
    }

    [Fact]
    public async Task CompleteAsync_WithoutTemperature_SendsTheFallbackTemperature()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? capturedBody = null;
        handler.When(HttpMethod.Post, "*/api/chat")
            .With(req =>
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json",
                """{"model":"llama3.2","message":{"role":"assistant","content":"hi"},"done":true,"done_reason":"stop"}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        using var doc = JsonDocument.Parse(capturedBody!);
        doc.RootElement.GetProperty("options").GetProperty("temperature").GetSingle()
            .Should().Be(CompletionRequest.FallbackTemperature, "an unset temperature keeps sending the former default");
    }

    [Fact]
    public async Task CompleteAsync_WithEmptyModel_UsesDefaultFromOptions()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "qwen2.5");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "qwen2.5");
        string? capturedBody = null;
        handler.When(HttpMethod.Post, "*/api/chat")
            .With(req =>
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json",
                """{"model":"qwen2.5","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}""");

        var request = new CompletionRequest
        {
            Model = string.Empty,
            Messages = new List<Message> { Message.User("hi") },
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        capturedBody.Should().Contain("\"model\":\"qwen2.5\"");
    }

    [Fact]
    public async Task CompleteAsync_WithToolCallsInResponse_ProjectsThemViaMetadata()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "model": "llama3.2",
          "message": {
            "role": "assistant",
            "content": "",
            "tool_calls": [
              { "function": { "name": "get_weather", "arguments": { "city": "Paris" } } }
            ]
          },
          "done": true,
          "done_reason": "stop",
          "prompt_eval_count": 5,
          "eval_count": 1
        }
        """;
        handler.When(HttpMethod.Post, "*/api/chat").Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.FinishReason.Should().Be(FinishReason.ToolCall);
        var calls = result.Value.GetToolCalls();
        calls.Should().ContainSingle();
        calls[0].ToolName.Should().Be("get_weather");
        calls[0].ArgumentsJson.Should().Contain("Paris");
    }

    [Fact]
    public async Task CompleteAsync_WithToolsAttached_SerializesToolsAndSchema()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/api/chat")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json",
                """{"model":"llama3.2","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}""");

        var tools = new List<AgentTool>
        {
            new(
                "get_weather",
                "Returns the current weather.",
                """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}"""),
        };
        var request = TestFactories.SimpleCompletionRequest().WithTools(tools);

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        body.Should().NotBeNull();
        var doc = JsonDocument.Parse(body!);
        var toolsJson = doc.RootElement.GetProperty("tools").EnumerateArray().ToList();
        toolsJson.Should().HaveCount(1);
        toolsJson[0].GetProperty("type").GetString().Should().Be("function");
        toolsJson[0].GetProperty("function").GetProperty("name").GetString().Should().Be("get_weather");
        toolsJson[0].GetProperty("function").GetProperty("parameters")
            .GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public async Task CompleteAsync_WithInvalidToolSchema_SkipsParameters()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/api/chat")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json",
                """{"model":"llama3.2","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}""");

        var tools = new List<AgentTool>
        {
            new("broken", "broken tool", "{not valid json"),
        };
        var request = TestFactories.SimpleCompletionRequest().WithTools(tools);

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert — broken schema should be silently dropped (parameters omitted)
        var doc = JsonDocument.Parse(body!);
        var fn = doc.RootElement.GetProperty("tools")[0].GetProperty("function");
        fn.TryGetProperty("parameters", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("stop", FinishReason.Stop)]
    [InlineData("length", FinishReason.Length)]
    [InlineData("load", FinishReason.Other)]
    [InlineData("unload", FinishReason.Other)]
    [InlineData("weird", FinishReason.Other)]
    public async Task CompleteAsync_MapsDoneReasonCorrectly(string doneReason, FinishReason expected)
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = $$"""
        {
          "model": "llama3.2",
          "message": { "role": "assistant", "content": "" },
          "done": true,
          "done_reason": "{{doneReason}}"
        }
        """;
        handler.When(HttpMethod.Post, "*/api/chat").Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.FinishReason.Should().Be(expected);
    }

    [Fact]
    public async Task CompleteAsync_WithNoDoneReason_StillDone_MapsToStop()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/api/chat").Respond("application/json",
            """{"model":"llama3.2","message":{"role":"assistant","content":""},"done":true}""");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.FinishReason.Should().Be(FinishReason.Stop);
    }

    [Fact]
    public async Task CompleteAsync_OnHttpError_ReturnsFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/api/chat")
            .Respond(HttpStatusCode.InternalServerError, "application/json", "{\"error\":\"boom\"}");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("boom");
    }

    [Fact]
    public async Task CompleteAsync_WithNullRequest_Throws()
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
    public async Task CompleteAsync_WhenCanceled_PropagatesCancellation()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/api/chat")
            .Throw(new TaskCanceledException("user cancel"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var act = async () => await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), cts.Token);

        // Assert
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    // ---------- Auto-pull recovery ----------

    [Fact]
    public async Task CompleteAsync_AutoPullEnabled_OnModelNotFound_PullsAndRetries()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.AutoPullModel = true);
        var sut = TestFactories.CreateProvider(httpClient, o => o.AutoPullModel = true);

        var chatCalls = 0;
        handler.When(HttpMethod.Post, "*/api/chat")
            .Respond(_ =>
            {
                chatCalls++;
                if (chatCalls == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(
                            "{\"error\":\"model 'llama3.2' not found, try pulling it first\"}",
                            Encoding.UTF8,
                            "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"model":"llama3.2","message":{"role":"assistant","content":"hi"},"done":true,"done_reason":"stop"}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            });

        handler.When(HttpMethod.Post, "*/api/pull")
            .Respond("application/x-ndjson",
                "{\"status\":\"pulling manifest\"}\n{\"status\":\"success\"}\n");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be("hi");
        chatCalls.Should().Be(2);
    }

    [Fact]
    public async Task CompleteAsync_AutoPullEnabled_WhenPullFails_PropagatesPullError()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.AutoPullModel = true);
        var sut = TestFactories.CreateProvider(httpClient, o => o.AutoPullModel = true);

        handler.When(HttpMethod.Post, "*/api/chat")
            .Respond(HttpStatusCode.NotFound, "application/json",
                "{\"error\":\"model 'llama3.2' not found, try pulling it first\"}");
        handler.When(HttpMethod.Post, "*/api/pull")
            .Respond("application/x-ndjson",
                "{\"status\":\"pulling\"}\n{\"error\":\"manifest not found\"}\n");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ModelNotFound");
    }

    [Fact]
    public async Task CompleteAsync_AutoPullDisabled_OnModelNotFound_DoesNotPull()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.AutoPullModel = false);
        var sut = TestFactories.CreateProvider(httpClient, o => o.AutoPullModel = false);
        var pullCalled = false;

        handler.When(HttpMethod.Post, "*/api/chat")
            .Respond(HttpStatusCode.NotFound, "application/json",
                "{\"error\":\"model 'llama3.2' not found, try pulling it first\"}");
        handler.When(HttpMethod.Post, "*/api/pull")
            .Respond(_ =>
            {
                pullCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ModelNotFound");
        pullCalled.Should().BeFalse();
    }

    // ---------- StreamCompleteAsync ----------

    [Fact]
    public async Task StreamCompleteAsync_OnSuccess_YieldsChunksAndStopsOnDone()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var stream = string.Join("\n",
            "{\"model\":\"llama3.2\",\"message\":{\"role\":\"assistant\",\"content\":\"He\"},\"done\":false}",
            "{\"model\":\"llama3.2\",\"message\":{\"role\":\"assistant\",\"content\":\"llo\"},\"done\":false}",
            "{\"model\":\"llama3.2\",\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":3,\"eval_count\":2}",
            "");
        handler.When(HttpMethod.Post, "*/api/chat")
            .Respond("application/x-ndjson", stream);

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
        chunks[1].IsFinal.Should().BeFalse();
        chunks[2].IsFinal.Should().BeTrue();
        chunks[2].FinishReason.Should().Be(FinishReason.Stop);
        chunks[2].Usage!.PromptTokens.Should().Be(3);
        chunks[2].Usage!.CompletionTokens.Should().Be(2);
    }

    [Fact]
    public async Task StreamCompleteAsync_BodyContainsStreamTrue()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/api/chat")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/x-ndjson",
                "{\"model\":\"llama3.2\",\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n");

        // Act
        await foreach (var _ in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None))
        {
        }

        // Assert
        body.Should().NotBeNull();
        body!.Should().Contain("\"stream\":true");
    }

    [Fact]
    public async Task StreamCompleteAsync_WithEmptyModel_UsesDefault()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "qwen2.5");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "qwen2.5");
        string? body = null;
        handler.When(HttpMethod.Post, "*/api/chat")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/x-ndjson",
                "{\"model\":\"qwen2.5\",\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n");

        var request = new CompletionRequest
        {
            Model = string.Empty,
            Messages = new List<Message> { Message.User("hi") },
        };

        // Act
        await foreach (var _ in sut.StreamCompleteAsync(request, CancellationToken.None))
        {
        }

        // Assert
        body.Should().Contain("qwen2.5");
    }

    [Fact]
    public async Task StreamCompleteAsync_WhenInitialResponseFails_YieldsFailureAndStops()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/api/chat")
            .Respond(HttpStatusCode.InternalServerError, "application/json", "{\"error\":\"oops\"}");

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

    [Fact]
    public async Task StreamCompleteAsync_WithNullRequest_Throws()
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

    // ---------- EmbedAsync ----------

    [Fact]
    public async Task EmbedAsync_OnSuccess_MapsEmbeddingsAndUsage()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "model": "nomic-embed-text",
          "embeddings": [[0.1, 0.2, 0.3], [0.4, 0.5, 0.6]],
          "prompt_eval_count": 4
        }
        """;
        handler.When(HttpMethod.Post, "*/api/embed").Respond("application/json", json);

        var request = TestFactories.SimpleEmbeddingRequest(n: 2);

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Embeddings.Should().HaveCount(2);
        result.Value.Embeddings[0].Index.Should().Be(0);
        result.Value.Embeddings[0].Vector.Should().Equal(0.1f, 0.2f, 0.3f);
        result.Value.Embeddings[1].Index.Should().Be(1);
        result.Value.Embeddings[1].Vector.Should().Equal(0.4f, 0.5f, 0.6f);
        result.Value.Model.Should().Be("nomic-embed-text");
        result.Value.Usage.PromptTokens.Should().Be(4);
    }

    [Fact]
    public async Task EmbedAsync_WithEmptyInputs_ReturnsInvalidRequest()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var result = await sut.EmbedAsync(
            new EmbeddingRequest { Model = "x", Inputs = new List<string>() },
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
    }

    [Fact]
    public async Task EmbedAsync_WithEmptyModel_UsesDefaultEmbeddingModel()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultEmbeddingModel = "mxbai-embed-large");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultEmbeddingModel = "mxbai-embed-large");
        string? body = null;
        handler.When(HttpMethod.Post, "*/api/embed")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json",
                """{"model":"mxbai-embed-large","embeddings":[[0.0]]}""");

        var request = new EmbeddingRequest
        {
            Model = string.Empty,
            Inputs = new List<string> { "a" },
        };

        // Act
        await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        body.Should().Contain("mxbai-embed-large");
    }

    [Fact]
    public async Task EmbedAsync_OnEmbedRouteUnavailable_FallsBackToLegacyEndpoint()
    {
        // Arrange — /api/embed returns 500 → adapter falls back to legacy per-input endpoint
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        handler.When(HttpMethod.Post, "*/api/embed")
            .Respond(HttpStatusCode.InternalServerError, "application/json", "{\"error\":\"unknown route\"}");

        var legacyCalls = 0;
        handler.When(HttpMethod.Post, "*/api/embeddings")
            .Respond(_ =>
            {
                legacyCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{{\"embedding\":[{legacyCalls}.0]}}",
                        Encoding.UTF8,
                        "application/json"),
                };
            });

        var request = TestFactories.SimpleEmbeddingRequest(n: 2);

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Embeddings.Should().HaveCount(2);
        legacyCalls.Should().Be(2);
        result.Value.Embeddings[0].Vector[0].Should().Be(1.0f);
        result.Value.Embeddings[1].Vector[0].Should().Be(2.0f);
    }

    [Fact]
    public async Task EmbedAsync_OnLegacyFallbackError_PropagatesFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        handler.When(HttpMethod.Post, "*/api/embed")
            .Respond(HttpStatusCode.InternalServerError, "application/json", "{}");
        handler.When(HttpMethod.Post, "*/api/embeddings")
            .Respond(HttpStatusCode.InternalServerError, "application/json", "{\"error\":\"bad\"}");

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
    }

    [Fact]
    public async Task EmbedAsync_AutoPullEnabled_OnModelNotFound_PullsAndRetries()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.AutoPullModel = true);
        var sut = TestFactories.CreateProvider(httpClient, o => o.AutoPullModel = true);

        var embedCalls = 0;
        handler.When(HttpMethod.Post, "*/api/embed")
            .Respond(_ =>
            {
                embedCalls++;
                if (embedCalls == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(
                            "{\"error\":\"model 'nomic-embed-text' not found, try pulling it first\"}",
                            Encoding.UTF8,
                            "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"model":"nomic-embed-text","embeddings":[[1.0,2.0]]}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            });
        handler.When(HttpMethod.Post, "*/api/pull")
            .Respond("application/x-ndjson", "{\"status\":\"success\"}\n");

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Embeddings.Should().ContainSingle();
        embedCalls.Should().Be(2);
    }

    [Fact]
    public async Task EmbedAsync_AutoPullEnabled_WhenPullFails_PropagatesPullError()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.AutoPullModel = true);
        var sut = TestFactories.CreateProvider(httpClient, o => o.AutoPullModel = true);

        handler.When(HttpMethod.Post, "*/api/embed")
            .Respond(HttpStatusCode.NotFound, "application/json",
                "{\"error\":\"model 'nomic-embed-text' not found, try pulling it first\"}");
        handler.When(HttpMethod.Post, "*/api/pull")
            .Respond("application/x-ndjson", "{\"error\":\"network down\"}\n");

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ModelNotFound");
    }

    [Fact]
    public async Task EmbedAsync_WithNullRequest_Throws()
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
    public async Task ListModelsAsync_OnSuccess_MapsLocalModelsAndDetectsKinds()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = """
        {
          "models": [
            {
              "name": "llama3.2:latest",
              "size": 2147483648,
              "digest": "sha256:abc",
              "details": { "family": "llama", "parameter_size": "3B", "quantization_level": "Q4_K_M" }
            },
            {
              "name": "nomic-embed-text:latest",
              "details": { "family": "nomic-bert" }
            },
            {
              "name": "llava:latest",
              "details": { "family": "llava" }
            },
            { "name": "ancient-model" }
          ]
        }
        """;
        handler.When(HttpMethod.Get, "*/api/tags").Respond("application/json", json);

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(4);

        var llama = result.Value[0];
        llama.Id.Should().Be("llama3.2:latest");
        llama.Provider.Should().Be("ollama");
        llama.SupportsTools.Should().BeTrue();
        llama.SupportsStreaming.Should().BeTrue();
        llama.SupportsEmbeddings.Should().BeFalse();
        llama.SupportsVision.Should().BeFalse();
        llama.Metadata.Should().NotBeNull();
        llama.Metadata!["parameter_size"].Should().Be("3B");
        llama.Metadata["digest"].Should().Be("sha256:abc");

        var embed = result.Value[1];
        embed.SupportsEmbeddings.Should().BeTrue();
        embed.SupportsStreaming.Should().BeFalse();
        embed.SupportsTools.Should().BeFalse();

        var llava = result.Value[2];
        llava.SupportsVision.Should().BeTrue();

        var unknown = result.Value[3];
        unknown.SupportsTools.Should().BeFalse();
        unknown.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task ListModelsAsync_OnHttpError_ReturnsFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/api/tags")
            .Respond(HttpStatusCode.InternalServerError, "application/json", "{}");

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    // ---------- HealthCheckAsync ----------

    [Fact]
    public async Task HealthCheckAsync_WhenTagsRouteSucceeds_ReturnsSuccess()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/api/tags")
            .Respond("application/json", "{\"models\":[]}");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HealthCheckAsync_WhenTagsRouteFails_ReturnsFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/api/tags")
            .Respond(HttpStatusCode.ServiceUnavailable, "application/json", "{}");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderUnavailable");
    }

    [Fact]
    public async Task HealthCheckAsync_WhenCancellationRequestedDuringSend_ReturnsProviderUnavailable()
    {
        // Arrange — cancellation re-throws from ListModelsAsync, HealthCheckAsync catches it
        // and converts to ProviderUnavailable.
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Get, "*/api/tags")
            .Throw(new TaskCanceledException("user cancel"));

        // Act
        var result = await sut.HealthCheckAsync(cts.Token);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderUnavailable");
    }
}
