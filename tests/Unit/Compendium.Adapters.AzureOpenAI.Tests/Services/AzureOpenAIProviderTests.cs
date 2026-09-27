// -----------------------------------------------------------------------
// <copyright file="AzureOpenAIProviderTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Abstractions.AI.Agents.Models;
using Compendium.Adapters.AzureOpenAI.StructuredOutputs;
using Compendium.Adapters.AzureOpenAI.Tests.TestSupport;
using Compendium.Adapters.AzureOpenAI.Tools;
using Compendium.Adapters.AzureOpenAI.Vision;
using Microsoft.Extensions.Logging.Abstractions;

namespace Compendium.Adapters.AzureOpenAI.Tests.Services;

public class AzureOpenAIProviderTests
{
    [Fact]
    public void ProviderId_Always_ReturnsAzureOpenAI()
    {
        // Arrange
        var (httpClient, _, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var id = sut.ProviderId;

        // Assert
        id.Should().Be("azure-openai");
    }

    // ---------- Constructor guards ----------

    [Fact]
    public void Ctor_NullHttpClient_Throws()
    {
        // Arrange
        var options = Options.Create(TestFactories.DefaultOptions());

        // Act
        var act = () => new AzureOpenAIProvider(null!, options, NullLogger<AzureOpenAIProvider>.Instance);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        // Arrange
        var (httpClient, _, _) = TestFactories.CreateHttpClient();

        // Act
        var act = () => new AzureOpenAIProvider(httpClient, null!, NullLogger<AzureOpenAIProvider>.Instance);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        // Arrange
        var (httpClient, _, _) = TestFactories.CreateHttpClient();
        var options = Options.Create(TestFactories.DefaultOptions());

        // Act
        var act = () => new AzureOpenAIProvider(httpClient, options, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    // ---------- CompleteAsync ----------

    [Fact]
    public async Task CompleteAsync_OnSuccess_MapsApiResponseToCompletionResponse()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        var json = """
        {
          "id": "chatcmpl-1",
          "model": "gpt-4o-mini",
          "created": 1730000000,
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": "Hello world" }, "finish_reason": "stop" }
          ],
          "usage": { "prompt_tokens": 12, "completion_tokens": 3, "total_tokens": 15 }
        }
        """;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Respond("application/json", json);

        var request = new CompletionRequest
        {
            Model = "gpt-4o-mini",
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
        result.Value.Model.Should().Be("gpt-4o-mini");
        result.Value.Content.Should().Be("Hello world");
        result.Value.FinishReason.Should().Be(FinishReason.Stop);
        result.Value.Usage.PromptTokens.Should().Be(12);
        result.Value.Usage.CompletionTokens.Should().Be(3);
        result.Value.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1730000000).UtcDateTime);
    }

    [Fact]
    public async Task CompleteAsync_NullRequest_Throws()
    {
        // Arrange
        var (sut, _) = TestFactories.CreateProviderWithMock();

        // Act
        var act = async () => await sut.CompleteAsync(null!, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CompleteAsync_RoutesToMappedDeployment()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock(o =>
        {
            o.DeploymentMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-4o"] = "prod-gpt-4o-deployment"
            };
        });
        string? capturedUrl = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .With(req => { capturedUrl = req.RequestUri!.ToString(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest("gpt-4o"), CancellationToken.None);

        // Assert
        capturedUrl.Should().NotBeNull();
        capturedUrl!.Should().Contain("/openai/deployments/prod-gpt-4o-deployment/chat/completions");
    }

    [Fact]
    public async Task CompleteAsync_WithoutMapping_UsesModelNameAsDeployment()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock(o => o.DeploymentMapping.Clear());
        string? capturedUrl = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .With(req => { capturedUrl = req.RequestUri!.ToString(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest("my-model"), CancellationToken.None);

        // Assert
        capturedUrl!.Should().Contain("/openai/deployments/my-model/chat/completions");
    }

    [Fact]
    public async Task CompleteAsync_WithEmptyModel_UsesDefaultModelMapping()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock(o =>
        {
            o.DefaultModel = "gpt-4o";
            o.DeploymentMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-4o"] = "prod-gpt-4o-deployment"
            };
        });
        string? capturedUrl = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .With(req => { capturedUrl = req.RequestUri!.ToString(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = string.Empty,
            Messages = new List<Message> { Message.User("hi") }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        capturedUrl!.Should().Contain("/openai/deployments/prod-gpt-4o-deployment/chat/completions");
    }

    [Fact]
    public async Task CompleteAsync_DoesNotSerializeModelInBody()
    {
        // Arrange — Azure routes via URL; body must not carry a redundant "model" property.
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        body.Should().NotBeNull();
        var doc = JsonDocument.Parse(body!);
        doc.RootElement.TryGetProperty("model", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_WithMaxTokensNull_AppliesDefaultMaxTokens()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock(o => o.DefaultMaxTokens = 1234);
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        body!.Should().Contain("\"max_tokens\":1234");
    }

    [Fact]
    public async Task CompleteAsync_WithoutTemperature_SendsTheFallbackTemperature()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = "gpt-4o-mini",
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
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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

    [Fact]
    public async Task CompleteAsync_WithEmptyChoices_ReturnsEmptyContentAndInProgressReason()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Respond("application/json", """{"id":"x","model":"gpt-4o-mini","created":0,"choices":[]}""");

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
    public async Task CompleteAsync_WithEmptyResponseModel_FallsBackToRequestedModel()
    {
        // Arrange — Azure occasionally returns an empty "model" string; we keep the caller's logical model name.
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Respond("application/json", """{"id":"x","model":"","created":0,"choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest("gpt-4o-mini"), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Model.Should().Be("gpt-4o-mini");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AI.InvalidApiKey")]
    [InlineData(HttpStatusCode.Forbidden, "AI.InvalidApiKey")]
    [InlineData(HttpStatusCode.TooManyRequests, "AI.RateLimitExceeded")]
    [InlineData(HttpStatusCode.PaymentRequired, "AI.InsufficientCredits")]
    [InlineData(HttpStatusCode.NotFound, "AI.ModelNotFound")]
    [InlineData(HttpStatusCode.InternalServerError, "AI.ProviderError")]
    public async Task CompleteAsync_OnHttpError_MapsStatusCodeToErrorCode(HttpStatusCode status, string expectedCode)
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Respond(status, "application/json", """{"error":{"message":"oops","code":"some_code"}}""");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(expectedCode);
    }

    [Fact]
    public async Task CompleteAsync_OnContentFilter400_ReturnsContentFilteredError()
    {
        // Arrange — Azure returns 400 with code="content_filter" when a prompt is blocked at request time.
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        var errorBody = """
        {
          "error": {
            "code": "content_filter",
            "message": "The response was filtered due to the prompt triggering Azure OpenAI's content management policy.",
            "innererror": {
              "code": "ResponsibleAIPolicyViolation",
              "content_filter_result": {
                "hate":   { "filtered": false, "severity": "safe" },
                "violence":{ "filtered": true,  "severity": "high" }
              }
            }
          }
        }
        """;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Respond(HttpStatusCode.BadRequest, "application/json", errorBody);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ContentFiltered");
        result.Error.Message.Should().Contain("violence");
    }

    [Fact]
    public async Task CompleteAsync_OnContentFilter400_WithOnlyCodeNoCategories_StillReturnsContentFilteredError()
    {
        // Arrange — older response shape: code=content_filter, no inner content_filter_result block.
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        var errorBody = """
        { "error": { "code": "content_filter", "message": "blocked" } }
        """;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Respond(HttpStatusCode.BadRequest, "application/json", errorBody);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ContentFiltered");
    }

    [Fact]
    public async Task CompleteAsync_On200WithContentFilteredCompletion_ReturnsContentFilteredError()
    {
        // Arrange — Azure occasionally returns 200 with finish_reason="content_filter" when the
        // completion is blocked mid-generation. Surface that as a Result.Failure to match prompt-time blocks.
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        var json = """
        {
          "id": "x", "model": "m", "created": 0,
          "choices": [
            {
              "index": 0,
              "message": { "role": "assistant", "content": "" },
              "finish_reason": "content_filter",
              "content_filter_results": { "hate": { "filtered": true, "severity": "high" } }
            }
          ]
        }
        """;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ContentFiltered");
        result.Error.Message.Should().Contain("hate");
    }

    [Fact]
    public async Task CompleteAsync_OnNonJsonErrorBody_FallsBackToProviderError()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
    [InlineData("tool_calls", FinishReason.ToolCall)]
    [InlineData("function_call", FinishReason.ToolCall)]
    [InlineData("weird_other", FinishReason.Other)]
    public async Task CompleteAsync_MapsFinishReasonCorrectly(string apiReason, FinishReason expected)
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        var json = $$"""
        {
          "id": "x", "model": "m", "created": 0,
          "choices": [ { "index": 0, "message": { "role": "assistant", "content": "" }, "finish_reason": "{{apiReason}}" } ]
        }
        """;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.FinishReason.Should().Be(expected);
    }

    // ---------- StreamCompleteAsync ----------

    [Fact]
    public async Task StreamCompleteAsync_NullRequest_Throws()
    {
        // Arrange
        var (sut, _) = TestFactories.CreateProviderWithMock();

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
    public async Task StreamCompleteAsync_OnSuccess_YieldsChunksWithIncrementingIndex_AndStopsOnFinal()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        var stream = string.Join("\n",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"He\"}}]}",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"llo\"}}]}",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"never\"}}]}",
            "data: [DONE]",
            string.Empty);
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
    public async Task StreamCompleteAsync_IgnoresMalformedDataLinesAndUnrelatedLines()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        var stream = string.Join("\n",
            ": comment line that should be ignored",
            string.Empty,
            "data: not json",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"X\"},\"finish_reason\":\"stop\"}]}",
            "data: [DONE]",
            string.Empty);
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Respond("text/event-stream", stream);

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
    public async Task StreamCompleteAsync_WithEmptyModel_UsesDefaultModelAndSendsStreamTrue()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock(o => o.DefaultModel = "gpt-4o-mini");
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
        body!.Should().Contain("\"stream\":true");
        body!.Should().Contain("\"include_usage\":true");
    }

    [Fact]
    public async Task StreamCompleteAsync_OnError_YieldsFailureOnceAndStops()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
    public async Task StreamCompleteAsync_OnContentFilterMidStream_YieldsContentFilteredFailure()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        var stream = string.Join("\n",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"Bad\"}}]}",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{},\"finish_reason\":\"content_filter\",\"content_filter_results\":{\"sexual\":{\"filtered\":true,\"severity\":\"high\"}}}]}",
            "data: [DONE]",
            string.Empty);
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Respond("text/event-stream", stream);

        // Act
        var results = new List<Result<CompletionChunk>>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None))
        {
            results.Add(r);
        }

        // Assert — first chunk yielded, then the filter stops the stream with a failure.
        results.Should().HaveCount(2);
        results[0].IsSuccess.Should().BeTrue();
        results[1].IsFailure.Should().BeTrue();
        results[1].Error.Code.Should().Be("AI.ContentFiltered");
        results[1].Error.Message.Should().Contain("sexual");
    }

    // ---------- EmbedAsync ----------

    [Fact]
    public async Task EmbedAsync_NullRequest_Throws()
    {
        // Arrange
        var (sut, _) = TestFactories.CreateProviderWithMock();

        // Act
        var act = async () => await sut.EmbedAsync(null!, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task EmbedAsync_SingleBatch_AggregatesEmbeddings()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        var json = """
        {
          "model": "text-embedding-3-small",
          "data": [
            { "index": 0, "embedding": [0.1, 0.2] },
            { "index": 1, "embedding": [0.3, 0.4] }
          ],
          "usage": { "prompt_tokens": 6, "total_tokens": 6 }
        }
        """;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/embeddings*").Respond("application/json", json);

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(2), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Embeddings.Should().HaveCount(2);
        result.Value.Embeddings[0].Vector.Should().Equal(0.1f, 0.2f);
        result.Value.Embeddings[1].Vector.Should().Equal(0.3f, 0.4f);
        result.Value.Usage.PromptTokens.Should().Be(6);
        result.Value.Model.Should().Be("text-embedding-3-small");
    }

    [Fact]
    public async Task EmbedAsync_RoutesToEmbeddingDeployment()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock(o =>
        {
            o.DeploymentMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["text-embedding-3-small"] = "prod-embeddings"
            };
        });
        string? capturedUrl = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/embeddings*")
            .With(req => { capturedUrl = req.RequestUri!.ToString(); return true; })
            .Respond("application/json", """{"model":"text-embedding-3-small","data":[{"index":0,"embedding":[0.1]}],"usage":{"prompt_tokens":1,"total_tokens":1}}""");

        // Act
        await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(1), CancellationToken.None);

        // Assert
        capturedUrl!.Should().Contain("/openai/deployments/prod-embeddings/embeddings");
    }

    [Fact]
    public async Task EmbedAsync_LargeInputs_BatchesByMaxEmbeddingsBatchSize()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock(o => o.MaxEmbeddingsBatchSize = 2);

        var callCount = 0;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/embeddings*").Respond(req =>
        {
            callCount++;
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var inputs = doc.RootElement.GetProperty("input").GetArrayLength();
            var data = string.Join(",", Enumerable.Range(0, inputs).Select(i =>
                $"{{\"index\":{i},\"embedding\":[{i * 0.1f}]}}"));
            var responseJson = $"{{\"model\":\"text-embedding-3-small\",\"data\":[{data}],\"usage\":{{\"prompt_tokens\":2,\"total_tokens\":2}}}}";
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
        var (sut, _) = TestFactories.CreateProviderWithMock();
        var request = new EmbeddingRequest { Model = "text-embedding-3-small", Inputs = new List<string>() };

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
        var (sut, handler) = TestFactories.CreateProviderWithMock(o => o.DefaultEmbeddingModel = "text-embedding-3-large");
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/embeddings*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"model":"text-embedding-3-large","data":[],"usage":{"prompt_tokens":0,"total_tokens":0}}""");

        var request = new EmbeddingRequest
        {
            Model = string.Empty,
            Inputs = new List<string> { "x" }
        };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        body!.Should().Contain("input");
        result.Value.Model.Should().Be("text-embedding-3-large");
    }

    [Fact]
    public async Task EmbedAsync_OnHttpError_ReturnsFailure()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/embeddings*")
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
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/embeddings*").Throw(new TaskCanceledException("slow"));

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
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/embeddings*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"model":"m","data":[{"index":0,"embedding":[0.1]}],"usage":{"prompt_tokens":1,"total_tokens":1}}""");

        var request = new EmbeddingRequest
        {
            Model = "text-embedding-3-small",
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
    public async Task EmbedAsync_BatchSizeAtMostOne_StillEmitsAtLeastOneRequest()
    {
        // Arrange — guard against `Math.Max(1, MaxEmbeddingsBatchSize)` silently dropping requests.
        var (sut, handler) = TestFactories.CreateProviderWithMock(o => o.MaxEmbeddingsBatchSize = 0);
        var calls = 0;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/embeddings*").Respond(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"model":"m","data":[{"index":0,"embedding":[0.1]}],"usage":{"prompt_tokens":1,"total_tokens":1}}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        // Act
        var result = await sut.EmbedAsync(TestFactories.SimpleEmbeddingRequest(3), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        calls.Should().Be(3);
    }

    // ---------- ListModelsAsync ----------

    [Fact]
    public async Task ListModelsAsync_FromConfiguredDeploymentMapping_ReturnsRowPerLogicalModel()
    {
        // Arrange
        var (httpClient, _, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient, o =>
        {
            o.DeploymentMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-4o"] = "prod-gpt-4o",
                ["text-embedding-3-small"] = "prod-embeddings"
            };
            o.DefaultModel = "gpt-4o";
            o.DefaultEmbeddingModel = "text-embedding-3-small";
        });

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        var gpt = result.Value.Single(m => m.Id == "gpt-4o");
        gpt.Provider.Should().Be("azure-openai");
        gpt.SupportsTools.Should().BeTrue();
        gpt.SupportsStreaming.Should().BeTrue();
        gpt.SupportsVision.Should().BeTrue();
        gpt.SupportsEmbeddings.Should().BeFalse();
        var embed = result.Value.Single(m => m.Id == "text-embedding-3-small");
        embed.SupportsEmbeddings.Should().BeTrue();
        embed.SupportsTools.Should().BeFalse();
    }

    [Fact]
    public async Task ListModelsAsync_OnCancellation_Throws()
    {
        // Arrange
        var (httpClient, _, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var act = async () => await sut.ListModelsAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ListModelsAsync_DeduplicatesAcrossMappingAndDefaults()
    {
        // Arrange
        var (httpClient, _, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient, o =>
        {
            o.DefaultModel = "gpt-4o-mini";
            o.DefaultEmbeddingModel = "text-embedding-3-small";
            o.DeploymentMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-4o-mini"] = "deploy-1"
            };
        });

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Select(m => m.Id).Should().BeEquivalentTo(new[] { "gpt-4o-mini", "text-embedding-3-small" });
    }

    // ---------- HealthCheckAsync ----------

    [Fact]
    public async Task HealthCheckAsync_OnPingSuccess_ReturnsSuccess()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[{"index":0,"message":{"role":"assistant","content":"pong"},"finish_reason":"stop"}]}""");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HealthCheckAsync_OnPingFailure_ReturnsFailure()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Respond(HttpStatusCode.Unauthorized, "application/json", """{"error":{"message":"x"}}""");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Fact]
    public async Task HealthCheckAsync_WhenUnderlyingThrowsCancellation_RethrowsCancellation()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Throw(new TaskCanceledException("user cancel"));

        // Act
        var act = async () => await sut.HealthCheckAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task HealthCheckAsync_WhenDefaultModelEmpty_ReturnsProviderUnavailable()
    {
        // Arrange
        var (sut, _) = TestFactories.CreateProviderWithMock(o =>
        {
            o.DefaultModel = string.Empty;
            o.DeploymentMapping.Clear();
        });

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderUnavailable");
    }

    // ---------- Tool calling ----------

    [Fact]
    public async Task CompleteAsync_WithTools_SerializesToolsArrayInRequest()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        var json = """
        {
          "id": "chatcmpl-2",
          "model": "gpt-4o-mini",
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
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .Respond("application/json", json);

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

    [Fact]
    public void WithTools_NullRequest_Throws()
    {
        // Arrange
        CompletionRequest? request = null;

        // Act
        var act = () => request!.WithTools(new List<AgentTool>());

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WithTools_NullTools_Throws()
    {
        // Arrange
        var request = TestFactories.SimpleCompletionRequest();

        // Act
        var act = () => request.WithTools(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    // ---------- Structured outputs ----------

    [Fact]
    public async Task CompleteAsync_WithStructuredOutput_AppliesJsonSchemaResponseFormat()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
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
        var (sut, handler) = TestFactories.CreateProviderWithMock(o => o.UseStructuredOutputsByDefault = true);
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("response_format").GetProperty("type").GetString().Should().Be("json_object");
    }

    // ---------- Vision ----------

    [Fact]
    public async Task CompleteAsync_WithImages_RewritesLastUserMessageAsMultimodal()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = TestFactories.SimpleCompletionRequest()
            .WithImages(new List<AzureOpenAIImageInput>
            {
                new("https://example.com/cat.png", "high")
            });

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var lastMessage = doc.RootElement.GetProperty("messages").EnumerateArray().Last();
        lastMessage.GetProperty("role").GetString().Should().Be("user");
        var contentArr = lastMessage.GetProperty("content").EnumerateArray().ToList();
        contentArr.Should().HaveCount(2);
        contentArr[0].GetProperty("type").GetString().Should().Be("text");
        contentArr[0].GetProperty("text").GetString().Should().Be("Hello");
        contentArr[1].GetProperty("type").GetString().Should().Be("image_url");
        contentArr[1].GetProperty("image_url").GetProperty("url").GetString().Should().Be("https://example.com/cat.png");
        contentArr[1].GetProperty("image_url").GetProperty("detail").GetString().Should().Be("high");
    }

    [Fact]
    public async Task CompleteAsync_WithImagesButNoUserMessage_DoesNotMutate()
    {
        // Arrange — system-only conversations cannot carry images; the adapter must not crash.
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = "gpt-4o-mini",
            SystemPrompt = "Only system",
            Messages = new List<Message> { Message.Assistant("Prior reply") }
        }.WithImages(new List<AzureOpenAIImageInput> { new("https://example.com/x.png") });

        // Act
        var result = await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var doc = JsonDocument.Parse(body!);
        var roles = doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString()).ToList();
        roles.Should().NotContain("user");
    }

    [Fact]
    public async Task CompleteAsync_WithImagesAndEmptyUserMessage_OmitsTextPart()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProviderWithMock();
        string? body = null;
        handler.When(HttpMethod.Post, "*/openai/deployments/*/chat/completions*")
            .With(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return true; })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = "gpt-4o-mini",
            Messages = new List<Message> { new() { Role = MessageRole.User, Content = string.Empty } }
        }.WithImages(new List<AzureOpenAIImageInput> { new("https://example.com/x.png") });

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var lastMessage = doc.RootElement.GetProperty("messages").EnumerateArray().Last();
        var contentArr = lastMessage.GetProperty("content").EnumerateArray().ToList();
        contentArr.Should().ContainSingle();
        contentArr[0].GetProperty("type").GetString().Should().Be("image_url");
    }

    [Fact]
    public void WithImages_NullRequest_Throws()
    {
        // Arrange
        CompletionRequest? request = null;

        // Act
        var act = () => request!.WithImages(new List<AzureOpenAIImageInput>());

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WithImages_NullImages_Throws()
    {
        // Arrange
        var request = TestFactories.SimpleCompletionRequest();

        // Act
        var act = () => request.WithImages(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WithStructuredOutput_NullRequest_Throws()
    {
        // Arrange
        CompletionRequest? request = null;

        // Act
        var act = () => request!.WithStructuredOutput("""{"type":"object"}""");

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WithJsonMode_NullRequest_Throws()
    {
        // Arrange
        CompletionRequest? request = null;

        // Act
        var act = () => request!.WithJsonMode();

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WithStructuredOutput_EmptySchema_Throws()
    {
        // Arrange
        var request = TestFactories.SimpleCompletionRequest();

        // Act
        var act = () => request.WithStructuredOutput("   ");

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
