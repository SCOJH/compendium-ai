// -----------------------------------------------------------------------
// <copyright file="HuggingFaceAIProviderTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Abstractions.AI.Agents.Models;
using Compendium.Adapters.HuggingFace.Tests.TestSupport;
using Compendium.Adapters.HuggingFace.Tools;

namespace Compendium.Adapters.HuggingFace.Tests.Services;

/// <summary>
/// Unit tests for <see cref="HuggingFaceAIProvider"/>.
/// </summary>
public class HuggingFaceAIProviderTests
{
    private const string ChatUrl = TestFactories.DefaultEndpointUrl + "/v1/chat/completions";

    [Fact]
    public void ProviderId_Always_ReturnsHuggingface()
    {
        // Arrange
        var (sut, _) = TestFactories.CreateProvider();

        // Act
        var id = sut.ProviderId;

        // Assert
        id.Should().Be("huggingface");
    }

    // ---------- CompleteAsync ----------

    [Fact]
    public async Task CompleteAsync_OnSuccess_MapsApiResponseToCompletionResponse()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        var json = """
        {
          "id": "hf-1",
          "model": "meta-llama/Meta-Llama-3.1-8B-Instruct",
          "created": 1730000000,
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": "Hello world" }, "finish_reason": "stop" }
          ],
          "usage": { "prompt_tokens": 12, "completion_tokens": 3, "total_tokens": 15 }
        }
        """;
        handler.When(HttpMethod.Post, ChatUrl).Respond("application/json", json);

        var request = new CompletionRequest
        {
            Model = TestFactories.DefaultChatModel,
            Messages = new List<Message>
            {
                Message.User("Hi"),
                Message.Assistant("Yes?"),
                new Message { Role = MessageRole.User, Content = "Tell me", Name = "alice" }
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
        result.Value.Id.Should().Be("hf-1");
        result.Value.Model.Should().Be("meta-llama/Meta-Llama-3.1-8B-Instruct");
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
        var (sut, _) = TestFactories.CreateProvider();

        // Act
        var act = async () => await sut.CompleteAsync(null!, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CompleteAsync_WithEmptyChoices_ReturnsEmptyContentAndInProgressReason()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        handler.When(HttpMethod.Post, ChatUrl)
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
    public async Task CompleteAsync_WhenCreatedZero_UsesNowAsCreatedAt()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        handler.When(HttpMethod.Post, ChatUrl)
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var before = DateTime.UtcNow;

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        var after = DateTime.UtcNow;
        result.Value.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after.AddSeconds(1));
    }

    [Fact]
    public async Task CompleteAsync_WithNoModel_UsesDefaultChatModelFromOptions()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider(o => o.DefaultChatModel = "Qwen/Qwen2.5-7B-Instruct");
        string? capturedBody = null;
        handler.When(HttpMethod.Post, ChatUrl)
            .With(req =>
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = null!,
            Messages = new List<Message> { Message.User("hi") }
        };

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("Qwen/Qwen2.5-7B-Instruct");
    }

    [Fact]
    public async Task CompleteAsync_WithMaxTokensNull_AppliesDefaultMaxTokensFromOptions()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider(o => o.DefaultMaxTokens = 1234);
        string? body = null;
        handler.When(HttpMethod.Post, ChatUrl)
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
        var (sut, handler) = TestFactories.CreateProvider();
        string? body = null;
        handler.When(HttpMethod.Post, ChatUrl)
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
        var (sut, handler) = TestFactories.CreateProvider();
        string? body = null;
        handler.When(HttpMethod.Post, ChatUrl)
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
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
        var (sut, handler) = TestFactories.CreateProvider();
        string? body = null;
        handler.When(HttpMethod.Post, ChatUrl)
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
    public async Task CompleteAsync_OnHttpError_ReturnsFailure()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        handler.When(HttpMethod.Post, ChatUrl)
            .Respond(HttpStatusCode.Unauthorized, "application/json", "{\"error\":\"bad token\"}");

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Theory]
    [InlineData("stop", FinishReason.Stop)]
    [InlineData("eos_token", FinishReason.Stop)]
    [InlineData("STOP", FinishReason.Stop)]
    [InlineData("length", FinishReason.Length)]
    [InlineData("content_filter", FinishReason.ContentFilter)]
    [InlineData("tool_calls", FinishReason.ToolCall)]
    [InlineData("function_call", FinishReason.ToolCall)]
    [InlineData("weird_other", FinishReason.Other)]
    public async Task CompleteAsync_MapsFinishReasonCorrectly(string apiReason, FinishReason expected)
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        var json = $$"""
        {
          "id": "x", "model": "m", "created": 0,
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": "" }, "finish_reason": "{{apiReason}}" }
          ]
        }
        """;
        handler.When(HttpMethod.Post, ChatUrl).Respond("application/json", json);

        // Act
        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.FinishReason.Should().Be(expected);
    }

    // ---------- Tool calling (best-effort) ----------

    [Fact]
    public async Task CompleteAsync_WithTools_SerializesToolsOnTheWire()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        string? body = null;
        handler.When(HttpMethod.Post, ChatUrl)
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var tools = new List<AgentTool>
        {
            new(
                Name: "get_weather",
                Description: "Get weather for a city.",
                InputSchemaJson: """{"type":"object","properties":{"city":{"type":"string"}}}""")
        };
        var request = new CompletionRequest
        {
            Model = TestFactories.DefaultChatModel,
            Messages = new List<Message> { Message.User("weather in Paris?") }
        }.WithTools(tools, toolChoice: "auto");

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        body.Should().Contain("\"tools\"").And.Contain("\"get_weather\"").And.Contain("\"tool_choice\":\"auto\"");
    }

    [Fact]
    public async Task CompleteAsync_WithToolChoiceByName_SendsTypedToolChoiceObject()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        string? body = null;
        handler.When(HttpMethod.Post, ChatUrl)
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var tools = new List<AgentTool>
        {
            new(Name: "lookup", Description: "Lookup", InputSchemaJson: "{\"type\":\"object\"}")
        };
        var request = new CompletionRequest
        {
            Model = TestFactories.DefaultChatModel,
            Messages = new List<Message> { Message.User("look it up") }
        }.WithTools(tools, toolChoice: "lookup");

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        body.Should().Contain("\"function\"").And.Contain("\"name\":\"lookup\"");
    }

    [Fact]
    public async Task CompleteAsync_WithInvalidToolSchema_SkipsParametersGracefully()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        string? body = null;
        handler.When(HttpMethod.Post, ChatUrl)
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var tools = new List<AgentTool>
        {
            new(Name: "broken", Description: "broken schema", InputSchemaJson: "{not json"),
            new(Name: "empty_schema", Description: "no schema", InputSchemaJson: "")
        };
        var request = new CompletionRequest
        {
            Model = TestFactories.DefaultChatModel,
            Messages = new List<Message> { Message.User("hi") }
        }.WithTools(tools);

        // Act
        var result = await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        // Tools array present but parameters field absent (null-omitting serializer).
        body.Should().Contain("\"tools\"").And.Contain("\"broken\"").And.Contain("\"empty_schema\"");
        body.Should().NotContain("\"parameters\":");
    }

    [Fact]
    public async Task CompleteAsync_WithEmptyToolList_OmitsToolsField()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        string? body = null;
        handler.When(HttpMethod.Post, ChatUrl)
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"m","created":0,"choices":[]}""");

        var request = new CompletionRequest
        {
            Model = TestFactories.DefaultChatModel,
            Messages = new List<Message> { Message.User("hi") }
        }.WithTools(Array.Empty<AgentTool>());

        // Act
        await sut.CompleteAsync(request, CancellationToken.None);

        // Assert
        body.Should().NotContain("\"tools\"");
    }

    [Fact]
    public async Task CompleteAsync_WhenResponseContainsToolCalls_SurfacesAsMetadata()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        var json = """
        {
          "id": "hf-tc-1", "model": "m", "created": 0,
          "choices": [{
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
          }]
        }
        """;
        handler.When(HttpMethod.Post, ChatUrl).Respond("application/json", json);

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
    public void GetToolCalls_OnResponseWithoutMetadata_ReturnsEmpty()
    {
        // Arrange
        var response = new CompletionResponse
        {
            Id = "x",
            Model = "m",
            Content = "ok",
            FinishReason = FinishReason.Stop,
            Usage = new UsageStats { PromptTokens = 0, CompletionTokens = 0 },
            CreatedAt = DateTime.UtcNow
        };

        // Act & Assert
        response.GetToolCalls().Should().BeEmpty();
    }

    [Fact]
    public void GetToolCalls_WithNullArgument_Throws()
    {
        var act = () => HuggingFaceToolCallingExtensions.GetToolCalls(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WithTools_WithNullRequest_Throws()
    {
        var act = () => HuggingFaceToolCallingExtensions.WithTools(null!, Array.Empty<AgentTool>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WithTools_WithNullToolList_Throws()
    {
        var request = TestFactories.SimpleCompletionRequest();
        var act = () => request.WithTools(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---------- StreamCompleteAsync ----------

    [Fact]
    public async Task StreamCompleteAsync_OnSuccess_YieldsChunksWithIncrementingIndex_AndStopsOnFinal()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        var stream = string.Join("\n",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"He\"}}]}",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"llo\"}}]}",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}",
            "data: {\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"never\"}}]}",
            "data: [DONE]",
            "");
        handler.When(HttpMethod.Post, ChatUrl)
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
        var (sut, handler) = TestFactories.CreateProvider(o => o.DefaultChatModel = "mistralai/Mistral-7B-Instruct-v0.3");
        string? body = null;
        handler.When(HttpMethod.Post, ChatUrl)
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("text/event-stream", "data: [DONE]\n");

        var request = new CompletionRequest
        {
            Model = null!,
            Messages = new List<Message> { Message.User("hi") }
        };

        // Act
        await foreach (var _ in sut.StreamCompleteAsync(request, CancellationToken.None))
        {
        }

        // Assert
        body.Should().NotBeNull();
        body!.Should().Contain("mistralai/Mistral-7B-Instruct-v0.3");
        body!.Should().Contain("\"stream\":true");
    }

    [Fact]
    public async Task StreamCompleteAsync_WhenChunkReturnsFailure_YieldsFailureAndStops()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        handler.When(HttpMethod.Post, ChatUrl)
            .Respond(HttpStatusCode.TooManyRequests, "application/json", "{\"error\":\"limit\"}");

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
    public async Task StreamCompleteAsync_NullRequest_Throws()
    {
        // Arrange
        var (sut, _) = TestFactories.CreateProvider();

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
    public async Task EmbedAsync_SingleInput_ReturnsSingleEmbedding()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        handler.When(HttpMethod.Post, TestFactories.DefaultEndpointUrl)
            .Respond("application/json", "[0.1, 0.2, 0.3]");

        var request = new EmbeddingRequest { Model = string.Empty, Inputs = new List<string> { "hello" } };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Embeddings.Should().ContainSingle();
        result.Value.Embeddings[0].Vector.Should().BeEquivalentTo(new[] { 0.1f, 0.2f, 0.3f });
        result.Value.Embeddings[0].Index.Should().Be(0);
        result.Value.Model.Should().Be(TestFactories.DefaultEmbeddingModel);
    }

    [Fact]
    public async Task EmbedAsync_BatchInputs_ReturnsBatchEmbeddings()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        handler.When(HttpMethod.Post, TestFactories.DefaultEndpointUrl)
            .Respond("application/json", "[[0.1, 0.2], [0.3, 0.4], [0.5, 0.6]]");

        var request = new EmbeddingRequest
        {
            Model = string.Empty,
            Inputs = new List<string> { "a", "b", "c" }
        };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Embeddings.Should().HaveCount(3);
        result.Value.Embeddings.Select(e => e.Index).Should().Equal(0, 1, 2);
        result.Value.Embeddings[1].Vector.Should().BeEquivalentTo(new[] { 0.3f, 0.4f });
    }

    [Fact]
    public async Task EmbedAsync_TokenMatrixResponse_MeanPoolsToSingleVectorPerInput()
    {
        // Arrange — sentence-transformers without server-side pooling returns number[][][].
        var (sut, handler) = TestFactories.CreateProvider();
        // 2 inputs, each a 3-token sequence with 2-dim hidden state.
        handler.When(HttpMethod.Post, TestFactories.DefaultEndpointUrl)
            .Respond("application/json",
                "[[[1.0, 2.0],[3.0, 4.0],[5.0, 6.0]],[[7.0, 8.0],[9.0, 10.0],[11.0, 12.0]]]");

        var request = new EmbeddingRequest
        {
            Model = string.Empty,
            Inputs = new List<string> { "a", "b" }
        };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Embeddings.Should().HaveCount(2);
        // First input: mean of [1,2],[3,4],[5,6] = [3,4]
        result.Value.Embeddings[0].Vector.Should().BeEquivalentTo(new[] { 3.0f, 4.0f });
        // Second input: mean of [7,8],[9,10],[11,12] = [9,10]
        result.Value.Embeddings[1].Vector.Should().BeEquivalentTo(new[] { 9.0f, 10.0f });
    }

    [Fact]
    public async Task EmbedAsync_ServerlessMode_PostsToModelPath()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider(o =>
        {
            o.Mode = HuggingFaceMode.ServerlessInference;
            o.EndpointUrl = null;
            o.DefaultEmbeddingModel = "BAAI/bge-small-en-v1.5";
        });
        var expected = handler.Expect(HttpMethod.Post, TestFactories.DefaultServerlessBaseUrl + "/BAAI/bge-small-en-v1.5")
            .Respond("application/json", "[[0.1, 0.2]]");

        var request = new EmbeddingRequest { Model = string.Empty, Inputs = new List<string> { "ping" } };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        handler.GetMatchCount(expected).Should().Be(1);
    }

    [Fact]
    public async Task EmbedAsync_NullRequest_Throws()
    {
        var (sut, _) = TestFactories.CreateProvider();
        var act = async () => await sut.EmbedAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task EmbedAsync_WithEmptyInputs_ReturnsInvalidRequestFailure()
    {
        // Arrange
        var (sut, _) = TestFactories.CreateProvider();
        var request = new EmbeddingRequest { Model = string.Empty, Inputs = new List<string>() };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
    }

    [Fact]
    public async Task EmbedAsync_OnHttpFailure_PropagatesError()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        handler.When(HttpMethod.Post, TestFactories.DefaultEndpointUrl)
            .Respond(HttpStatusCode.Unauthorized, "application/json", "{\"error\":\"no\"}");

        var request = new EmbeddingRequest { Model = string.Empty, Inputs = new List<string> { "x" } };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Fact]
    public async Task EmbedAsync_WhenResponseBodyIsInvalidJson_ReturnsProviderError()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        handler.When(HttpMethod.Post, TestFactories.DefaultEndpointUrl)
            .Respond("application/json", "this is not json");

        var request = new EmbeddingRequest { Model = string.Empty, Inputs = new List<string> { "x" } };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("Failed to parse embeddings JSON");
    }

    [Fact]
    public async Task EmbedAsync_WhenResponseIsNotArray_ReturnsProviderError()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        handler.When(HttpMethod.Post, TestFactories.DefaultEndpointUrl)
            .Respond("application/json", "{\"oops\":true}");

        var request = new EmbeddingRequest { Model = string.Empty, Inputs = new List<string> { "x" } };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("not a JSON array");
    }

    [Fact]
    public async Task EmbedAsync_WhenInnerRowIsScalar_ReturnsProviderError()
    {
        // Arrange — root is array, first element is array, but first inner element is string, not number/array.
        var (sut, handler) = TestFactories.CreateProvider();
        handler.When(HttpMethod.Post, TestFactories.DefaultEndpointUrl)
            .Respond("application/json", "[[\"bad\"]]");

        var request = new EmbeddingRequest { Model = string.Empty, Inputs = new List<string> { "x" } };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("Unexpected embedding row shape");
    }

    [Fact]
    public async Task EmbedAsync_WhenRootFirstElementIsScalar_ReturnsProviderError()
    {
        // Arrange — root is array, first element is a string (neither number nor array).
        var (sut, handler) = TestFactories.CreateProvider();
        handler.When(HttpMethod.Post, TestFactories.DefaultEndpointUrl)
            .Respond("application/json", "[\"bad\"]");

        var request = new EmbeddingRequest { Model = string.Empty, Inputs = new List<string> { "x" } };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("Unexpected embedding response shape");
    }

    [Fact]
    public async Task EmbedAsync_WithExplicitModel_OverridesDefault()
    {
        // Arrange — serverless mode so model goes into the URL path; verifies the override path.
        var (sut, handler) = TestFactories.CreateProvider(o =>
        {
            o.Mode = HuggingFaceMode.ServerlessInference;
            o.EndpointUrl = null;
            o.DefaultEmbeddingModel = "default-model";
        });
        handler.Expect(HttpMethod.Post, TestFactories.DefaultServerlessBaseUrl + "/explicit-model")
            .Respond("application/json", "[[0.1]]");

        var request = new EmbeddingRequest
        {
            Model = "explicit-model",
            Inputs = new List<string> { "x" }
        };

        // Act
        var result = await sut.EmbedAsync(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Model.Should().Be("explicit-model");
    }

    // ---------- ListModelsAsync ----------

    [Fact]
    public async Task ListModelsAsync_ReturnsSingleDescriptorForConfiguredModel()
    {
        // Arrange
        var (sut, _) = TestFactories.CreateProvider(o => o.DefaultChatModel = "Qwen/Qwen2.5-7B-Instruct");

        // Act
        var result = await sut.ListModelsAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].Id.Should().Be("Qwen/Qwen2.5-7B-Instruct");
        result.Value[0].Provider.Should().Be("huggingface");
        result.Value[0].SupportsStreaming.Should().BeTrue();
        result.Value[0].SupportsTools.Should().BeTrue();
    }

    // ---------- HealthCheckAsync ----------

    [Fact]
    public async Task HealthCheckAsync_WhenProbeSucceeds_ReturnsSuccess()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        handler.When(HttpMethod.Post, TestFactories.DefaultEndpointUrl)
            .Respond("application/json", "[0.0, 0.0]");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HealthCheckAsync_WhenProbeFails_ReturnsFailure()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateProvider();
        handler.When(HttpMethod.Post, TestFactories.DefaultEndpointUrl)
            .Respond(HttpStatusCode.Unauthorized, "application/json", "{\"error\":\"no\"}");

        // Act
        var result = await sut.HealthCheckAsync(CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Fact]
    public async Task HealthCheckAsync_WhenUnderlyingThrowsUnhandled_ReturnsProviderUnavailable()
    {
        // Arrange — caller cancels; the HttpClient layer rethrows TaskCanceledException matching the token,
        // which the embedding code does *not* catch, allowing the provider's HealthCheckAsync catch to
        // wrap it as ProviderUnavailable.
        var (sut, handler) = TestFactories.CreateProvider();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Post, TestFactories.DefaultEndpointUrl)
            .Throw(new TaskCanceledException("cancelled"));

        // Act
        var result = await sut.HealthCheckAsync(cts.Token);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderUnavailable");
    }
}
