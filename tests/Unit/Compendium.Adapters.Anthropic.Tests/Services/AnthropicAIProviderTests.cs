// -----------------------------------------------------------------------
// <copyright file="AnthropicAIProviderTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Anthropic.Http;
using Compendium.Adapters.Anthropic.Tests.TestSupport;

namespace Compendium.Adapters.Anthropic.Tests.Services;

public class AnthropicAIProviderTests
{
    [Fact]
    public void ProviderId_Always_ReturnsAnthropic()
    {
        // Arrange
        var (httpClient, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);

        // Act
        var id = sut.ProviderId;

        // Assert
        id.Should().Be("anthropic");
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
          "id": "msg_01",
          "model": "claude-3-7-sonnet-latest",
          "content": [ { "type": "text", "text": "Hello world" } ],
          "stop_reason": "end_turn",
          "usage": { "input_tokens": 12, "output_tokens": 3 }
        }
        """;
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("application/json", json);

        var request = new CompletionRequest
        {
            Model = "claude-3-7-sonnet-latest",
            Messages = new List<Message>
            {
                Message.User("Hi"),
                Message.Assistant("Yes?"),
                new Message { Role = MessageRole.User, Content = "Tell me a joke" },
            },
            SystemPrompt = "Be concise.",
            Temperature = 0.5f,
            MaxTokens = 256,
            TopP = 0.9f,
            StopSequences = new List<string> { "###" },
            UserId = "user-42",
        };

        // Act
        var result = await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be("msg_01");
        result.Value.Model.Should().Be("claude-3-7-sonnet-latest");
        result.Value.Content.Should().Be("Hello world");
        result.Value.FinishReason.Should().Be(FinishReason.Stop);
        result.Value.Usage.PromptTokens.Should().Be(12);
        result.Value.Usage.CompletionTokens.Should().Be(3);
    }

    [Fact]
    public async Task CompleteAsync_WithEmptyContent_ReturnsEmptyContentAndInProgressReasonWhenStopReasonAbsent()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Respond("application/json", """{"id":"x","model":"m","content":[]}""");

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
    public async Task CompleteAsync_WithMultipleTextBlocks_ConcatenatesTextAndIgnoresNonText()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("application/json", """
        {
          "id":"x","model":"m",
          "content":[
            {"type":"text","text":"Part1"},
            {"type":"tool_use","text":null},
            {"type":"text","text":" Part2"}
          ]
        }
        """);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be("Part1 Part2");
    }

    [Fact]
    public async Task CompleteAsync_WithNoModel_UsesDefaultFromOptions()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "claude-opus-4-5");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "claude-opus-4-5");
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"claude-opus-4-5","content":[]}""");

        // Act
        var request = new CompletionRequest
        {
            Model = null!,
            Messages = new List<Message> { Message.User("hi") },
        };
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        body.Should().Contain("claude-opus-4-5");
    }

    [Fact]
    public async Task CompleteAsync_WithMaxTokensNull_AppliesDefaultMaxTokensFromOptions()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultMaxTokens = 1234);
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultMaxTokens = 1234);
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","content":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        body.Should().Contain("\"max_tokens\":1234");
    }

    // Claude Opus 4.7 and later, Sonnet 5, Fable and Mythos answer any temperature or top_p with a 400: a request
    // that sets neither must carry neither, on both paths.
    [Fact]
    public async Task CompleteAsync_WithoutTemperatureOrTopP_SendsNeither()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"claude-opus-5","content":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest("claude-opus-5"), CancellationToken.None);

        // Assert
        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.TryGetProperty("temperature", out _).Should().BeFalse("an unset temperature is left out, not defaulted");
        sent.RootElement.TryGetProperty("top_p", out _).Should().BeFalse();
    }

    [Fact]
    public async Task StreamCompleteAsync_WithoutTemperatureOrTopP_SendsNeither()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("text/event-stream", "data: {\"type\":\"message_stop\"}\n");

        // Act
        await foreach (var _ in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest("claude-sonnet-5"), CancellationToken.None))
        {
        }

        // Assert
        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
        sent.RootElement.TryGetProperty("temperature", out _).Should().BeFalse("an unset temperature is left out, not defaulted");
        sent.RootElement.TryGetProperty("top_p", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_WithATemperature_SendsIt()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"claude-sonnet-4-6","content":[]}""");

        // Act
        await sut.CompleteAsync(
            TestFactories.SimpleCompletionRequest("claude-sonnet-4-6") with { Temperature = 0f },
            CancellationToken.None);

        // Assert
        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.GetProperty("temperature").GetSingle().Should().Be(0f, "a temperature of 0 is a value, not an absence");
        sent.RootElement.TryGetProperty("top_p", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_WithSystemPrompt_EmitsTopLevelSystemBlock()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","content":[]}""");

        var request = new CompletionRequest
        {
            Model = "m",
            SystemPrompt = "You are helpful.",
            Messages = new List<Message> { Message.User("hi") },
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        body.Should().NotBeNullOrEmpty();
        var doc = JsonDocument.Parse(body!);
        var system = doc.RootElement.GetProperty("system");
        system.ValueKind.Should().Be(JsonValueKind.Array);
        system.EnumerateArray().First().GetProperty("text").GetString().Should().Be("You are helpful.");
        doc.RootElement.GetProperty("messages").EnumerateArray().Should().OnlyContain(m =>
            m.GetProperty("role").GetString()!.Equals("user"));
    }

    [Fact]
    public async Task CompleteAsync_WithSystemRoleMessages_PromotesThemToSystemBlock()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","content":[]}""");

        var request = new CompletionRequest
        {
            Model = "m",
            SystemPrompt = "Primary.",
            Messages = new List<Message>
            {
                Message.System("Sys-A"),
                Message.System("Sys-B"),
                Message.User("hi"),
            },
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var systemText = doc.RootElement.GetProperty("system").EnumerateArray().First().GetProperty("text").GetString();
        systemText.Should().Be("Primary.\nSys-A\nSys-B");
        doc.RootElement.GetProperty("messages").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task CompleteAsync_WithoutSystemPrompt_OmitsSystemField()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","content":[]}""");

        // Act
        await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        doc.RootElement.TryGetProperty("system", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_WhenPromptCachingEnabled_EmitsCacheControlOnSystemBlock()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.EnablePromptCaching = true);
        var sut = TestFactories.CreateProvider(httpClient, o => o.EnablePromptCaching = true);
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","content":[]}""");

        var request = new CompletionRequest
        {
            Model = "m",
            SystemPrompt = "Long stable system prompt.",
            Messages = new List<Message> { Message.User("hi") },
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        body.Should().Contain("\"cache_control\":{\"type\":\"ephemeral\"}");
    }

    [Fact]
    public async Task CompleteAsync_WhenPromptCachingDisabled_DoesNotEmitCacheControl()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","content":[]}""");

        var request = new CompletionRequest
        {
            Model = "m",
            SystemPrompt = "Long stable system prompt.",
            Messages = new List<Message> { Message.User("hi") },
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        body.Should().NotContain("cache_control");
    }

    [Fact]
    public async Task CompleteAsync_WithToolRole_MapsAsUser()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","content":[]}""");

        var request = new CompletionRequest
        {
            Model = "m",
            Messages = new List<Message>
            {
                Message.User("Need data"),
                Message.Assistant("Calling tool"),
                new Message { Role = MessageRole.Tool, Content = "result: 42" },
            },
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(body!);
        var roles = doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString()).ToList();
        roles.Should().Equal("user", "assistant", "user");
    }

    [Fact]
    public async Task CompleteAsync_WithUserId_EmitsMetadataBlock()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","content":[]}""");

        var request = new CompletionRequest
        {
            Model = "m",
            Messages = new List<Message> { Message.User("hi") },
            UserId = "user-007",
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        body.Should().Contain("\"metadata\":{\"user_id\":\"user-007\"}");
    }

    [Fact]
    public async Task CompleteAsync_OnHttpError_ReturnsFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Respond(HttpStatusCode.Unauthorized, "application/json", """{"type":"error","error":{"type":"authentication_error","message":"bad key"}}""");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Theory]
    [InlineData("end_turn", FinishReason.Stop)]
    [InlineData("stop_sequence", FinishReason.Stop)]
    [InlineData("max_tokens", FinishReason.Length)]
    [InlineData("tool_use", FinishReason.ToolCall)]
    [InlineData("weird_other", FinishReason.Other)]
    public async Task CompleteAsync_MapsStopReasonsCorrectly(string stopReason, FinishReason expected)
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var json = $$"""
        { "id":"x","model":"m","content":[{"type":"text","text":""}],"stop_reason":"{{stopReason}}" }
        """;
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.FinishReason.Should().Be(expected);
    }

    // ---------- StreamCompleteAsync ----------

    [Fact]
    public async Task StreamCompleteAsync_OnSuccess_YieldsContentDeltasAndFinalChunk()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var stream = string.Join("\n",
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-3-7-sonnet-latest\",\"usage\":{\"input_tokens\":7,\"output_tokens\":0}}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"He\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"llo\"}}",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"input_tokens\":0,\"output_tokens\":2}}",
            "data: {\"type\":\"message_stop\"}",
            "");
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("text/event-stream", stream);

        // Act
        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None))
        {
            r.IsSuccess.Should().BeTrue();
            chunks.Add(r.Value);
        }

        // Assert
        chunks.Should().HaveCount(3);
        chunks[0].Id.Should().Be("msg_1");
        chunks[0].ContentDelta.Should().Be("He");
        chunks[0].IsFinal.Should().BeFalse();
        chunks[0].Index.Should().Be(0);
        chunks[1].ContentDelta.Should().Be("llo");
        chunks[1].Index.Should().Be(1);
        chunks[2].IsFinal.Should().BeTrue();
        chunks[2].FinishReason.Should().Be(FinishReason.Stop);
        chunks[2].Usage!.PromptTokens.Should().Be(7);
        chunks[2].Usage!.CompletionTokens.Should().Be(2);
    }

    [Fact]
    public async Task StreamCompleteAsync_WhenMessageStopArrivesWithoutPriorMessageDelta_EmitsFallbackFinalChunk()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var stream = string.Join("\n",
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_2\",\"model\":\"m\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}",
            "data: {\"type\":\"message_stop\"}",
            "");
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("text/event-stream", stream);

        // Act
        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None))
        {
            chunks.Add(r.Value);
        }

        // Assert
        chunks.Should().HaveCount(2);
        chunks[^1].IsFinal.Should().BeTrue();
        chunks[^1].FinishReason.Should().Be(FinishReason.Stop);
    }

    [Fact]
    public async Task StreamCompleteAsync_WithNoModel_UsesDefaultFromOptionsAndSetsStreamTrue()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.DefaultModel = "claude-3-5-haiku-latest");
        var sut = TestFactories.CreateProvider(httpClient, o => o.DefaultModel = "claude-3-5-haiku-latest");
        string? body = null;
        handler.When(HttpMethod.Post, "*/v1/messages")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("text/event-stream", "data: {\"type\":\"message_stop\"}\n");

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
        body!.Should().Contain("claude-3-5-haiku-latest");
        body!.Should().Contain("\"stream\":true");
    }

    [Fact]
    public async Task StreamCompleteAsync_WhenUpstreamReturns429_YieldsFailureAndStops()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Respond(HttpStatusCode.TooManyRequests, "application/json", """{"type":"error","error":{"type":"rate_limit_error","message":"slow"}}""");

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
        var request = new EmbeddingRequest { Model = "any", Inputs = new List<string> { "a" } };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
        result.Error.Message.Should().Contain("Embeddings are not supported");
    }

    // ---------- ListModelsAsync ----------

    private const string ObservedWorkspaceMessage =
        "This API key is not scoped to a workspace, so this request must include the anthropic-workspace-id header with the ID of the workspace to use";

    private static string ModelsPage(string data, bool hasMore, string? lastId) =>
        $$"""
        { "data": [ {{data}} ], "has_more": {{(hasMore ? "true" : "false")}}, "first_id": null, "last_id": {{(lastId is null ? "null" : "\"" + lastId + "\"")}} }
        """;

    private static string Model(string id) =>
        $$"""{ "type": "model", "id": "{{id}}", "display_name": "{{id}}", "created_at": "2026-01-01T00:00:00Z" }""";

    private static string ErrorBody(string type, string message) =>
        JsonSerializer.Serialize(new { type = "error", error = new { type, message } });

    [Fact]
    public async Task ListModelsAsync_ReadsEveryPage()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.Expect(HttpMethod.Get, "*/v1/models")
            .WithExactQueryString("limit=1000")
            .Respond("application/json", ModelsPage(Model("m1") + "," + Model("m2"), hasMore: true, lastId: "m2"));
        handler.Expect(HttpMethod.Get, "*/v1/models")
            .WithExactQueryString("limit=1000&after_id=m2")
            .Respond("application/json", ModelsPage(Model("m3"), hasMore: false, lastId: "m3"));

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Select(m => m.Id).Should().Equal("m1", "m2", "m3");
        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task ListModelsAsync_ListsAnIdSeenOnAnEarlierPageOnce()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.Expect(HttpMethod.Get, "*/v1/models")
            .WithExactQueryString("limit=1000")
            .Respond("application/json", ModelsPage(Model("m1") + "," + Model("m2"), hasMore: true, lastId: "m2"));
        handler.Expect(HttpMethod.Get, "*/v1/models")
            .WithExactQueryString("limit=1000&after_id=m2")
            .Respond("application/json", ModelsPage(Model("m2") + "," + Model("m3"), hasMore: false, lastId: "m3"));

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Select(m => m.Id).Should().Equal("m1", "m2", "m3");
    }

    [Fact]
    public async Task ListModelsAsync_MapsDisplayNameDateAndLimits()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/v1/models*").Respond("application/json", """
        {
          "data": [
            {
              "type": "model",
              "id": "claude-sonnet-4-5-20250929",
              "display_name": "Claude Sonnet 4.5",
              "created_at": "2025-09-29T02:00:00+02:00",
              "max_input_tokens": 200000,
              "max_tokens": 64000,
              "line": "sonnet",
              "capabilities": { "image_input": { "supported": true }, "thinking": { "supported": true } }
            }
          ],
          "has_more": false,
          "first_id": "claude-sonnet-4-5-20250929",
          "last_id": "claude-sonnet-4-5-20250929"
        }
        """);

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var model = result.Value.Should().ContainSingle().Subject;
        model.Id.Should().Be("claude-sonnet-4-5-20250929");
        model.Name.Should().Be("Claude Sonnet 4.5");
        model.Provider.Should().Be("anthropic");
        model.ContextWindow.Should().Be(200_000);
        model.MaxOutputTokens.Should().Be(64_000);
        model.SupportsVision.Should().BeTrue();
        model.SupportsStreaming.Should().BeTrue();
        model.SupportsTools.Should().BeTrue();
        model.SupportsEmbeddings.Should().BeFalse();
        model.PricingInputPerMillion.Should().BeNull();
        model.PricingOutputPerMillion.Should().BeNull();
        var createdAt = model.Metadata!["created_at"].Should().BeOfType<DateTimeOffset>().Subject;
        createdAt.Should().Be(new DateTimeOffset(2025, 9, 29, 0, 0, 0, TimeSpan.Zero));
        createdAt.Offset.Should().Be(TimeSpan.Zero);
        model.Metadata["line"].Should().Be("sonnet");
    }

    [Fact]
    public async Task ListModelsAsync_TreatsAnEpochReleaseDateAsUnknown()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/v1/models*").Respond("application/json", """
        { "data": [ { "type": "model", "id": "claude-x", "display_name": "Claude X", "created_at": "1970-01-01T00:00:00Z", "line": "opus" } ], "has_more": false }
        """);

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var model = result.Value.Should().ContainSingle().Subject;
        model.Metadata.Should().NotBeNull();
        model.Metadata!.ContainsKey("created_at").Should().BeFalse();
        model.Metadata["line"].Should().Be("opus");
    }

    [Fact]
    public async Task ListModelsAsync_WithoutLimits_LeavesThemUnknown()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/v1/models*").Respond("application/json", """
        { "data": [ { "type": "model", "id": "claude-y", "max_input_tokens": null, "max_tokens": null, "capabilities": null } ], "has_more": false }
        """);

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var model = result.Value.Should().ContainSingle().Subject;
        model.Name.Should().Be("claude-y");
        model.ContextWindow.Should().BeNull();
        model.MaxOutputTokens.Should().BeNull();
        model.SupportsVision.Should().BeFalse();
        model.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task ListModelsAsync_OnWorkspaceError_ReturnsTheProviderErrorAsIs()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/v1/models*")
            .Respond(HttpStatusCode.BadRequest, "application/json", ErrorBody("invalid_request_error", ObservedWorkspaceMessage));

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("anthropic-workspace-id");
        result.Error.Message.Should().Contain(ObservedWorkspaceMessage);
    }

    [Fact]
    public async Task ListModelsAsync_On404_IsAProviderError_NotAModelNotFound()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.WorkspaceId = "wrkspc_x");
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/v1/models*")
            .Respond(HttpStatusCode.NotFound, "application/json", ErrorBody("not_found_error", "Workspace `wrkspc_x` not found."));

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("[not_found_error]");
        result.Error.Message.Should().Contain("Workspace `wrkspc_x` not found.");
    }

    [Fact]
    public async Task ListModelsAsync_On401_IsInvalidApiKey()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/v1/models*")
            .Respond(HttpStatusCode.Unauthorized, "application/json", ErrorBody("authentication_error", "invalid x-api-key"));

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Fact(Timeout = 5000)]
    public async Task ListModelsAsync_WhenTheCursorDoesNotMove_FailsInsteadOfLooping()
    {
        // Arrange — every page claims more after the same id.
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var request = handler.When(HttpMethod.Get, "*/v1/models*")
            .Respond("application/json", ModelsPage(Model("m1"), hasMore: true, lastId: "m1"));

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert — the second page does not move the cursor: stop there, with no partial list.
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("pagination did not advance");
        handler.GetMatchCount(request).Should().Be(2);
    }

    [Fact]
    public async Task ListModelsAsync_WhenAPageHasMoreWithoutALastId_Fails()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var request = handler.When(HttpMethod.Get, "*/v1/models*")
            .Respond("application/json", ModelsPage(Model("m1"), hasMore: true, lastId: null));

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("pagination did not advance");
        handler.GetMatchCount(request).Should().Be(1);
    }

    [Fact(Timeout = 5000)]
    public async Task ListModelsAsync_StopsAfterMaxPages()
    {
        // Arrange — a provider whose pages never end, each moving the cursor.
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var served = 0;
        var request = handler.When(HttpMethod.Get, "*/v1/models*")
            .Respond(_ =>
            {
                var id = $"m{Interlocked.Increment(ref served)}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ModelsPage(Model(id), hasMore: true, lastId: id), Encoding.UTF8, "application/json"),
                };
            });

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        handler.GetMatchCount(request).Should().Be(AnthropicHttpClient.MaxModelPages);
    }

    [Fact]
    public async Task ListModelsAsync_WhenCallerCancels_Rethrows()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Get, "*/v1/models*").Throw(new TaskCanceledException("cancelled"));

        // Act
        var act = async () => await sut.ListModelsAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ListModelsAsync_OnClientTimeout_IsATimeout()
    {
        // Arrange — a TaskCanceledException the caller did not ask for is the HttpClient timeout.
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.TimeoutSeconds = 9);
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/v1/models*").Throw(new TaskCanceledException("timeout"));

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.Timeout");
    }

    [Fact]
    public async Task ListModelsAsync_OnHttpRequestException_IsAProviderError()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Get, "*/v1/models*").Throw(new HttpRequestException("connection refused"));

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("connection refused");
    }

    [Fact]
    public async Task ListModelsAsync_WhenLoggingEnabled_LogsThePageWithoutHeaders()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "*/v1/models*")
            .Respond("application/json", ModelsPage(Model("m1"), hasMore: false, lastId: "m1"));
        var options = TestFactories.DefaultOptions(o =>
        {
            o.EnableLogging = true;
            o.WorkspaceId = TestFactories.WorkspaceId;
        });
        var logger = new TestFactories.RecordingLogger<AnthropicHttpClient>();
        var client = new AnthropicHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl) },
            Options.Create(options),
            logger);
        var sut = TestFactories.CreateProvider(client);

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        logger.Entries.Should().Contain(e => e.Message.Contains("Anthropic models page 1"));
        logger.Entries.Should().NotContain(e => e.Message.Contains(TestFactories.DefaultApiKey));
    }

    [Fact]
    public async Task EveryRequest_CarriesTheWorkspaceHeader()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient(o => o.WorkspaceId = TestFactories.WorkspaceId);
        var sut = TestFactories.CreateProvider(httpClient);
        handler.Expect(HttpMethod.Post, "*/v1/messages")
            .WithHeaders("anthropic-workspace-id", TestFactories.WorkspaceId)
            .Respond("application/json", """
            { "id":"x","model":"m","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1} }
            """);
        handler.Expect(HttpMethod.Get, "*/v1/models*")
            .WithHeaders("anthropic-workspace-id", TestFactories.WorkspaceId)
            .Respond("application/json", ModelsPage(Model("m1"), hasMore: false, lastId: "m1"));

        // Act
        var completion = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);
        var models = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        completion.IsSuccess.Should().BeTrue();
        models.IsSuccess.Should().BeTrue();
        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task CompleteAsync_On404_StillMapsToModelNotFound()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Respond(HttpStatusCode.NotFound, "application/json", ErrorBody("not_found_error", "model: claude-nope"));

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest("claude-nope"), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ModelNotFound");
    }

    // ---------- HealthCheckAsync ----------

    [Fact]
    public async Task HealthCheckAsync_WhenProbeSucceeds_ReturnsSuccess()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("application/json", """
        { "id":"x","model":"m","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1} }
        """);

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HealthCheckAsync_WhenProbeFails_ReturnsMappedFailure()
    {
        // Arrange
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Respond(HttpStatusCode.Unauthorized, "application/json", """{"type":"error","error":{"type":"authentication_error","message":"bad key"}}""");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Fact]
    public async Task HealthCheckAsync_OnUnexpectedException_ReturnsProviderUnavailable()
    {
        // Arrange — InvalidOperationException isn't caught by the typed-client,
        // so it propagates into HealthCheckAsync's defensive catch.
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Throw(new InvalidOperationException("network is unplugged"));

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderUnavailable");
    }
}
