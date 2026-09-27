// -----------------------------------------------------------------------
// <copyright file="BedrockAIProviderTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Compendium.Adapters.Bedrock.Tests.TestSupport;
using BedrockMessage = Amazon.BedrockRuntime.Model.Message;

namespace Compendium.Adapters.Bedrock.Tests.Services;

public class BedrockAIProviderTests
{
    [Fact]
    public void ProviderId_Always_ReturnsBedrock()
    {
        // Arrange
        var sut = BedrockTestFactories.CreateProvider(Substitute.For<IAmazonBedrockRuntime>());

        // Act
        var id = sut.ProviderId;

        // Assert
        id.Should().Be("bedrock");
    }

    [Fact]
    public void Ctor_NullClient_Throws() =>
        FluentActions.Invoking(() => new BedrockAIProvider(
                null!,
                Microsoft.Extensions.Options.Options.Create(BedrockTestFactories.DefaultOptions()),
                NullLogger<BedrockAIProvider>.Instance))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void Ctor_NullOptions_Throws() =>
        FluentActions.Invoking(() => new BedrockAIProvider(
                Substitute.For<IAmazonBedrockRuntime>(),
                null!,
                NullLogger<BedrockAIProvider>.Instance))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void Ctor_NullLogger_Throws() =>
        FluentActions.Invoking(() => new BedrockAIProvider(
                Substitute.For<IAmazonBedrockRuntime>(),
                Microsoft.Extensions.Options.Options.Create(BedrockTestFactories.DefaultOptions()),
                null!))
            .Should().Throw<ArgumentNullException>();

    // ---------- CompleteAsync ----------

    [Fact]
    public async Task CompleteAsync_OnSuccess_MapsResponse()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        client.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse(text: "hi", promptTokens: 7, completionTokens: 2));
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        var result = await sut.CompleteAsync(BedrockTestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be("hi");
        result.Value.FinishReason.Should().Be(FinishReason.Stop);
        result.Value.Usage.PromptTokens.Should().Be(7);
        result.Value.Usage.CompletionTokens.Should().Be(2);
        result.Value.Id.Should().Be("req-abc");
        result.Value.Model.Should().Be("anthropic.claude-3-haiku-20240307-v1:0");
    }

    [Fact]
    public async Task CompleteAsync_NullRequest_Throws()
    {
        var sut = BedrockTestFactories.CreateProvider(Substitute.For<IAmazonBedrockRuntime>());
        await FluentActions.Invoking(() => sut.CompleteAsync(null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CompleteAsync_UsesRequestModelWhenProvided()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        ConverseRequest? captured = null;
        client.ConverseAsync(Arg.Do<ConverseRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        await sut.CompleteAsync(new CompletionRequest
        {
            Model = "meta.llama3-1-70b-instruct-v1:0",
            Messages = [Message.User("x")],
        });

        // Assert
        captured.Should().NotBeNull();
        captured!.ModelId.Should().Be("meta.llama3-1-70b-instruct-v1:0");
    }

    [Fact]
    public async Task CompleteAsync_FallsBackToDefaultModelWhenUnspecified()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        ConverseRequest? captured = null;
        client.ConverseAsync(Arg.Do<ConverseRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        await sut.CompleteAsync(new CompletionRequest { Model = null!, Messages = [Message.User("x")] });

        // Assert
        captured!.ModelId.Should().Be("anthropic.claude-3-haiku-20240307-v1:0");
    }

    [Fact]
    public async Task CompleteAsync_MapsSystemPromptIntoSystemBlock()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        ConverseRequest? captured = null;
        client.ConverseAsync(Arg.Do<ConverseRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        await sut.CompleteAsync(new CompletionRequest
        {
            Model = null!,
            SystemPrompt = "Be brief.",
            Messages = [Message.User("Hi")],
        });

        // Assert
        captured!.System.Should().ContainSingle(b => b.Text == "Be brief.");
        captured.Messages.Should().HaveCount(1);
        captured.Messages[0].Role.Should().Be(ConversationRole.User);
        captured.Messages[0].Content[0].Text.Should().Be("Hi");
    }

    [Fact]
    public async Task CompleteAsync_CoalescesAdjacentSameRoleMessages()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        ConverseRequest? captured = null;
        client.ConverseAsync(Arg.Do<ConverseRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        await sut.CompleteAsync(new CompletionRequest
        {
            Model = null!,
            Messages =
            [
                Message.User("first"),
                Message.User("second"),
                Message.Assistant("ack"),
            ],
        });

        // Assert : two user turns coalesce, then one assistant turn.
        captured!.Messages.Should().HaveCount(2);
        captured.Messages[0].Role.Should().Be(ConversationRole.User);
        captured.Messages[0].Content.Should().HaveCount(2);
        captured.Messages[0].Content[0].Text.Should().Be("first");
        captured.Messages[0].Content[1].Text.Should().Be("second");
        captured.Messages[1].Role.Should().Be(ConversationRole.Assistant);
    }

    [Fact]
    public async Task CompleteAsync_RoutesSystemMessagesToSystemBlocks()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        ConverseRequest? captured = null;
        client.ConverseAsync(Arg.Do<ConverseRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        await sut.CompleteAsync(new CompletionRequest
        {
            Model = null!,
            SystemPrompt = "primary",
            Messages =
            [
                new Message { Role = MessageRole.System, Content = "extra" },
                Message.User("hi"),
            ],
        });

        // Assert
        captured!.System.Should().HaveCount(2);
        captured.System[0].Text.Should().Be("primary");
        captured.System[1].Text.Should().Be("extra");
    }

    [Fact]
    public async Task CompleteAsync_TreatsToolRoleAsUser()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        ConverseRequest? captured = null;
        client.ConverseAsync(Arg.Do<ConverseRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        await sut.CompleteAsync(new CompletionRequest
        {
            Model = null!,
            Messages =
            [
                new Message { Role = MessageRole.Tool, Content = "tool-result" },
            ],
        });

        // Assert : the tool message is folded into the user role (Bedrock's Converse text-only model).
        captured!.Messages.Should().ContainSingle()
            .Which.Role.Should().Be(ConversationRole.User);
        captured.Messages[0].Content[0].Text.Should().Be("tool-result");
    }

    [Fact]
    public async Task CompleteAsync_WithNoMessages_SynthesizesUserPromptFromTrailingSystemText()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        ConverseRequest? captured = null;
        client.ConverseAsync(Arg.Do<ConverseRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        await sut.CompleteAsync(new CompletionRequest { Model = null!, Messages = Array.Empty<Message>(), SystemPrompt = "only system" });

        // Assert : Bedrock requires at least one message; we inject one carrying the system text.
        captured!.Messages.Should().ContainSingle();
        captured.Messages[0].Content[0].Text.Should().Be("only system");
    }

    [Fact]
    public async Task CompleteAsync_PropagatesInferenceConfig()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        ConverseRequest? captured = null;
        client.ConverseAsync(Arg.Do<ConverseRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        await sut.CompleteAsync(new CompletionRequest
        {
            Model = null!,
            Messages = [Message.User("x")],
            MaxTokens = 99,
            Temperature = 0.42f,
            TopP = 0.85f,
            StopSequences = new List<string> { "###" },
        });

        // Assert
        captured!.InferenceConfig.MaxTokens.Should().Be(99);
        captured.InferenceConfig.Temperature.Should().BeApproximately(0.42f, 0.001f);
        captured.InferenceConfig.TopP.Should().BeApproximately(0.85f, 0.001f);
        captured.InferenceConfig.StopSequences.Should().BeEquivalentTo(new[] { "###" });
    }

    [Fact]
    public async Task CompleteAsync_OmitsZeroTemperature()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        ConverseRequest? captured = null;
        client.ConverseAsync(Arg.Do<ConverseRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        await sut.CompleteAsync(new CompletionRequest
        {
            Model = null!,
            Messages = [Message.User("x")],
            Temperature = 0f, // explicit "unspecified" sentinel — leave the field null so the API uses its default.
        });

        // Assert
        captured!.InferenceConfig.Temperature.Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_WithoutTemperature_SendsTheFallbackTemperature()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        ConverseRequest? captured = null;
        client.ConverseAsync(Arg.Do<ConverseRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        await sut.CompleteAsync(new CompletionRequest
        {
            Model = null!,
            Messages = [Message.User("x")],
        });

        // Assert
        captured!.InferenceConfig.Temperature.Should().BeApproximately(CompletionRequest.FallbackTemperature, 0.001f,
            "an unset temperature keeps sending the former default");
        captured.InferenceConfig.TopP.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AI.InvalidApiKey")]
    [InlineData(HttpStatusCode.Forbidden, "AI.InvalidApiKey")]
    [InlineData(HttpStatusCode.PaymentRequired, "AI.InsufficientCredits")]
    [InlineData(HttpStatusCode.NotFound, "AI.ModelNotFound")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "AI.TokenLimitExceeded")]
    [InlineData(HttpStatusCode.TooManyRequests, "AI.RateLimitExceeded")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "AI.ProviderUnavailable")]
    [InlineData(HttpStatusCode.InternalServerError, "AI.ProviderError")]
    public async Task CompleteAsync_MapsBedrockExceptionsToTypedErrors(HttpStatusCode status, string expectedCode)
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        client.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(BedrockTestFactories.BedrockException(status));
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        var result = await sut.CompleteAsync(BedrockTestFactories.SimpleCompletionRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(expectedCode);
    }

    [Fact]
    public async Task CompleteAsync_PropagatesCancellation()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        client.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        var act = () => sut.CompleteAsync(BedrockTestFactories.SimpleCompletionRequest(), new CancellationToken(canceled: true));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CompleteAsync_ConcatenatesMultipleResponseContentBlocks()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        var response = new ConverseResponse
        {
            Output = new ConverseOutput
            {
                Message = new Amazon.BedrockRuntime.Model.Message
                {
                    Role = ConversationRole.Assistant,
                    Content =
                    [
                        new ContentBlock { Text = "A" },
                        new ContentBlock { Text = "B" },
                        new ContentBlock(), // empty content block, ignored
                        new ContentBlock { Text = "C" },
                    ],
                },
            },
            StopReason = StopReason.End_turn,
            Usage = new TokenUsage { InputTokens = 1, OutputTokens = 3 },
            ResponseMetadata = new Amazon.Runtime.ResponseMetadata { RequestId = "rid" },
        };
        client.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>()).Returns(response);
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        var result = await sut.CompleteAsync(BedrockTestFactories.SimpleCompletionRequest());

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be("ABC");
    }

    [Fact]
    public async Task CompleteAsync_WithNoOutput_ReturnsEmptyContentAndInProgressFinishReasonWhenStopReasonAbsent()
    {
        // Arrange
        var client = Substitute.For<IAmazonBedrockRuntime>();
        client.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ConverseResponse());
        var sut = BedrockTestFactories.CreateProvider(client);

        // Act
        var result = await sut.CompleteAsync(BedrockTestFactories.SimpleCompletionRequest());

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().BeEmpty();
        result.Value.FinishReason.Should().Be(FinishReason.InProgress);
        result.Value.Usage.PromptTokens.Should().Be(0);
        result.Value.Usage.CompletionTokens.Should().Be(0);
    }

    // ---------- MapStopReason ----------

    public static IEnumerable<object[]> StopReasonRows()
    {
        yield return new object[] { Amazon.BedrockRuntime.StopReason.End_turn, FinishReason.Stop };
        yield return new object[] { Amazon.BedrockRuntime.StopReason.Stop_sequence, FinishReason.Stop };
        yield return new object[] { Amazon.BedrockRuntime.StopReason.Max_tokens, FinishReason.Length };
        yield return new object[] { Amazon.BedrockRuntime.StopReason.Model_context_window_exceeded, FinishReason.Length };
        yield return new object[] { Amazon.BedrockRuntime.StopReason.Tool_use, FinishReason.ToolCall };
        yield return new object[] { Amazon.BedrockRuntime.StopReason.Content_filtered, FinishReason.ContentFilter };
        yield return new object[] { Amazon.BedrockRuntime.StopReason.Guardrail_intervened, FinishReason.ContentFilter };
        yield return new object[] { Amazon.BedrockRuntime.StopReason.Malformed_model_output, FinishReason.Other };
        yield return new object[] { Amazon.BedrockRuntime.StopReason.Malformed_tool_use, FinishReason.Other };
    }

    [Theory]
    [MemberData(nameof(StopReasonRows))]
    public void MapStopReason_MapsEveryKnownValue(Amazon.BedrockRuntime.StopReason input, FinishReason expected)
    {
        BedrockAIProvider.MapStopReason(input).Should().Be(expected);
    }

    [Fact]
    public void MapStopReason_NullInput_ReturnsInProgress() =>
        BedrockAIProvider.MapStopReason(null).Should().Be(FinishReason.InProgress);

    // ---------- ListModelsAsync + HealthCheck ----------

    [Fact]
    public async Task ListModelsAsync_ReturnsCatalog()
    {
        var sut = BedrockTestFactories.CreateProvider(Substitute.For<IAmazonBedrockRuntime>());
        var result = await sut.ListModelsAsync();
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();
        result.Value.Should().Contain(m => m.Id == BedrockOptions.DefaultClaude35SonnetV2ModelId);
        result.Value.Should().Contain(m => m.Id == BedrockOptions.DefaultTitanEmbedTextV2ModelId);
    }

    [Fact]
    public async Task HealthCheckAsync_OnSuccess_ReturnsSuccess()
    {
        var client = Substitute.For<IAmazonBedrockRuntime>();
        client.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());
        var sut = BedrockTestFactories.CreateProvider(client);

        var result = await sut.HealthCheckAsync();
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HealthCheckAsync_OnBedrockException_MapsError()
    {
        var client = Substitute.For<IAmazonBedrockRuntime>();
        client.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(BedrockTestFactories.BedrockException(HttpStatusCode.Unauthorized));
        var sut = BedrockTestFactories.CreateProvider(client);

        var result = await sut.HealthCheckAsync();
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Fact]
    public async Task HealthCheckAsync_OnUnexpectedException_ReportsProviderUnavailable()
    {
        var client = Substitute.For<IAmazonBedrockRuntime>();
        client.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("oops"));
        var sut = BedrockTestFactories.CreateProvider(client);

        var result = await sut.HealthCheckAsync();
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderUnavailable");
    }

    [Fact]
    public async Task HealthCheckAsync_OnCancellation_Propagates()
    {
        var client = Substitute.For<IAmazonBedrockRuntime>();
        client.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        var sut = BedrockTestFactories.CreateProvider(client);

        await FluentActions.Invoking(() => sut.HealthCheckAsync(new CancellationToken(canceled: true)))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
