// -----------------------------------------------------------------------
// <copyright file="AnthropicHttpClientTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Anthropic.Http;
using Compendium.Adapters.Anthropic.Http.Models;
using Compendium.Adapters.Anthropic.Tests.TestSupport;

namespace Compendium.Adapters.Anthropic.Tests.Http;

public class AnthropicHttpClientTests
{
    private static AnthropicMessagesRequest BuildRequest(string model = "claude-3-7-sonnet-latest") => new()
    {
        Model = model,
        MaxTokens = 256,
        Messages = new List<AnthropicMessage>
        {
            new() { Role = "user", Content = "Hello" },
        },
    };

    [Fact]
    public void Constructor_AddsAuthAndVersionHeaders()
    {
        // Arrange
        var (sut, _) = TestFactories.CreateHttpClient(o =>
        {
            o.ApiKey = "sk-ant-mykey";
            o.AnthropicVersion = "2023-06-01";
        });

        // Act
        var headers = GetUnderlyingHttpClient(sut).DefaultRequestHeaders;

        // Assert
        headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("sk-ant-mykey");
        headers.GetValues("anthropic-version").Should().ContainSingle().Which.Should().Be("2023-06-01");
    }

    [Fact]
    public void Constructor_WhenBaseAddressAlreadySet_DoesNotOverwrite()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var options = TestFactories.DefaultOptions();
        var preset = new Uri("https://preset.example/");
        var httpClient = new HttpClient(handler) { BaseAddress = preset };

        // Act
        _ = new AnthropicHttpClient(
            httpClient,
            Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AnthropicHttpClient>.Instance);

        // Assert
        httpClient.BaseAddress.Should().Be(preset);
    }

    [Fact]
    public void Constructor_WhenBaseAddressIsNull_AssignsFromOptions()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var options = TestFactories.DefaultOptions(o => o.BaseUrl = "https://custom.example");
        var httpClient = new HttpClient(handler);

        // Act
        _ = new AnthropicHttpClient(
            httpClient,
            Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AnthropicHttpClient>.Instance);

        // Assert
        httpClient.BaseAddress.Should().Be(new Uri("https://custom.example"));
    }

    [Fact]
    public void Constructor_WhenHeadersAlreadyPresent_DoesNotDuplicate()
    {
        // Arrange — simulate DI factory having already populated headers.
        var handler = new MockHttpMessageHandler();
        var options = TestFactories.DefaultOptions();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl) };
        httpClient.DefaultRequestHeaders.Add("x-api-key", "pre-existing");
        httpClient.DefaultRequestHeaders.Add("anthropic-version", "pre-existing");

        // Act
        _ = new AnthropicHttpClient(
            httpClient,
            Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AnthropicHttpClient>.Instance);

        // Assert
        httpClient.DefaultRequestHeaders.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("pre-existing");
        httpClient.DefaultRequestHeaders.GetValues("anthropic-version").Should().ContainSingle().Which.Should().Be("pre-existing");
    }

    [Fact]
    public void Constructor_WithWorkspaceId_AddsTheWorkspaceHeader()
    {
        // Arrange
        var (sut, _) = TestFactories.CreateHttpClient(o => o.WorkspaceId = "  " + TestFactories.WorkspaceId + " ");

        // Act
        var headers = GetUnderlyingHttpClient(sut).DefaultRequestHeaders;

        // Assert
        headers.GetValues("anthropic-workspace-id").Should().ContainSingle().Which.Should().Be(TestFactories.WorkspaceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Constructor_WithoutWorkspaceId_SendsNoWorkspaceHeader(string? workspaceId)
    {
        // Arrange
        var (sut, _) = TestFactories.CreateHttpClient(o => o.WorkspaceId = workspaceId);

        // Act
        var headers = GetUnderlyingHttpClient(sut).DefaultRequestHeaders;

        // Assert
        headers.Contains("anthropic-workspace-id").Should().BeFalse();
    }

    [Fact]
    public void Constructor_WhenWorkspaceHeaderAlreadyPresent_DoesNotDuplicate()
    {
        // Arrange — the DI factory has already put the header on the client.
        var handler = new MockHttpMessageHandler();
        var options = TestFactories.DefaultOptions(o => o.WorkspaceId = TestFactories.WorkspaceId);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl) };
        httpClient.DefaultRequestHeaders.Add("anthropic-workspace-id", TestFactories.WorkspaceId);

        // Act
        _ = new AnthropicHttpClient(
            httpClient,
            Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AnthropicHttpClient>.Instance);

        // Assert
        httpClient.DefaultRequestHeaders.GetValues("anthropic-workspace-id")
            .Should().ContainSingle().Which.Should().Be(TestFactories.WorkspaceId);
    }

    [Fact]
    public async Task CreateMessageAsync_OnSuccess_ReturnsDeserializedResponse()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        var json = """
        {
          "id": "msg_01ABC",
          "type": "message",
          "role": "assistant",
          "model": "claude-3-7-sonnet-latest",
          "content": [ { "type": "text", "text": "Hi there" } ],
          "stop_reason": "end_turn",
          "usage": { "input_tokens": 12, "output_tokens": 3 }
        }
        """;
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("application/json", json);

        // Act
        var result = await sut.CreateMessageAsync(BuildRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be("msg_01ABC");
        result.Value.Model.Should().Be("claude-3-7-sonnet-latest");
        result.Value.Content.Should().ContainSingle();
        result.Value.Content[0].Text.Should().Be("Hi there");
        result.Value.Usage!.InputTokens.Should().Be(12);
        result.Value.Usage!.OutputTokens.Should().Be(3);
    }

    [Fact]
    public async Task CreateMessageAsync_WhenLoggingEnabled_LogsRequestAndResponse()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Respond("application/json", """{"id":"x","model":"m","content":[]}""");
        var options = TestFactories.DefaultOptions(o => o.EnableLogging = true);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl) };
        var logger = new TestFactories.RecordingLogger<AnthropicHttpClient>();
        var sut = new AnthropicHttpClient(httpClient, Options.Create(options), logger);

        // Act
        var result = await sut.CreateMessageAsync(BuildRequest(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        logger.Entries.Should().Contain(e => e.Message.Contains("Anthropic request"));
        logger.Entries.Should().Contain(e => e.Message.Contains("Anthropic response"));
    }

    [Fact]
    public async Task CreateMessageAsync_WhenResponseIsNullJson_ReturnsProviderError()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("application/json", "null");

        // Act
        var result = await sut.CreateMessageAsync(BuildRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("Empty response");
    }

    [Fact]
    public async Task CreateMessageAsync_WhenResponseIsInvalidJson_ReturnsProviderError()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("application/json", "this is not json");

        // Act
        var result = await sut.CreateMessageAsync(BuildRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("Invalid response format");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AI.InvalidApiKey")]
    [InlineData(HttpStatusCode.PaymentRequired, "AI.InsufficientCredits")]
    [InlineData(HttpStatusCode.TooManyRequests, "AI.RateLimitExceeded")]
    [InlineData(HttpStatusCode.NotFound, "AI.ModelNotFound")]
    [InlineData(HttpStatusCode.InternalServerError, "AI.ProviderError")]
    [InlineData(HttpStatusCode.BadGateway, "AI.ProviderError")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "AI.ProviderError")]
    public async Task CreateMessageAsync_OnErrorStatus_MapsToTypedError(HttpStatusCode status, string expectedCode)
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Respond(status, "application/json", """{"type":"error","error":{"type":"invalid_request_error","message":"boom"}}""");

        // Act
        var result = await sut.CreateMessageAsync(BuildRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(expectedCode);
    }

    [Fact]
    public async Task CreateMessageAsync_OnErrorStatus_WithUnparseableBody_ReturnsProviderErrorWithRawBody()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Respond(HttpStatusCode.BadGateway, "text/plain", "raw upstream message");

        // Act
        var result = await sut.CreateMessageAsync(BuildRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("raw upstream message");
    }

    [Fact]
    public async Task CreateMessageAsync_OnErrorStatus_WithEmptyErrorObject_UsesRawContentAsMessage()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Respond(HttpStatusCode.InternalServerError, "application/json", "{\"foo\":\"bar\"}");

        // Act
        var result = await sut.CreateMessageAsync(BuildRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("foo");
    }

    [Fact]
    public async Task CreateMessageAsync_OnHttpRequestException_ReturnsProviderError()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Throw(new HttpRequestException("connection refused"));

        // Act
        var result = await sut.CreateMessageAsync(BuildRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.ProviderError");
        result.Error.Message.Should().Contain("connection refused");
    }

    [Fact]
    public async Task CreateMessageAsync_OnTimeoutCanceledByClient_ReturnsTimeoutError()
    {
        // Arrange — TaskCanceledException with non-cancelled token simulates HttpClient timeout.
        var (sut, handler) = TestFactories.CreateHttpClient(o => o.TimeoutSeconds = 7);
        handler.When(HttpMethod.Post, "*/v1/messages").Throw(new TaskCanceledException("timeout"));

        // Act
        var result = await sut.CreateMessageAsync(BuildRequest(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.Timeout");
        result.Error.Message.Should().Contain("7");
    }

    [Fact]
    public async Task CreateMessageAsync_WhenCallerCancels_PropagatesTaskCanceledException()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Throw(new TaskCanceledException("cancelled"));

        // Act
        var act = async () => await sut.CreateMessageAsync(BuildRequest(), cts.Token);

        // Assert
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    // ---------- streaming ----------

    [Fact]
    public async Task CreateMessageStreamAsync_OnSuccess_YieldsAllParsedEvents()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        var stream = string.Join("\n",
            "event: message_start",
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-3-7-sonnet-latest\",\"usage\":{\"input_tokens\":5,\"output_tokens\":0}}}",
            "",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"He\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"llo\"}}",
            "event: message_delta",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"input_tokens\":0,\"output_tokens\":2}}",
            "event: message_stop",
            "data: {\"type\":\"message_stop\"}",
            "");
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("text/event-stream", stream);

        // Act
        var events = new List<AnthropicStreamEvent>();
        await foreach (var r in sut.CreateMessageStreamAsync(BuildRequest(), CancellationToken.None))
        {
            r.IsSuccess.Should().BeTrue();
            events.Add(r.Value);
        }

        // Assert
        events.Select(e => e.Type).Should().Equal(
            "message_start",
            "content_block_delta",
            "content_block_delta",
            "message_delta",
            "message_stop");
    }

    [Fact]
    public async Task CreateMessageStreamAsync_OnNon2xx_YieldsSingleFailureAndStops()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Respond(HttpStatusCode.TooManyRequests, "application/json", """{"type":"error","error":{"type":"rate_limit_error","message":"slow"}}""");

        // Act
        var results = new List<Result<AnthropicStreamEvent>>();
        await foreach (var r in sut.CreateMessageStreamAsync(BuildRequest(), CancellationToken.None))
        {
            results.Add(r);
        }

        // Assert
        results.Should().ContainSingle();
        results[0].IsFailure.Should().BeTrue();
        results[0].Error.Code.Should().Be("AI.RateLimitExceeded");
    }

    [Fact]
    public async Task CreateMessageStreamAsync_StopsAfterMessageStopEvent()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        var stream = string.Join("\n",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"a\"}}",
            "data: {\"type\":\"message_stop\"}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"NEVER\"}}",
            "");
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("text/event-stream", stream);

        // Act
        var events = new List<AnthropicStreamEvent>();
        await foreach (var r in sut.CreateMessageStreamAsync(BuildRequest(), CancellationToken.None))
        {
            events.Add(r.Value);
        }

        // Assert
        events.Should().HaveCount(2);
        events[0].Type.Should().Be("content_block_delta");
        events[1].Type.Should().Be("message_stop");
    }

    [Fact]
    public async Task CreateMessageStreamAsync_SkipsBlankLines_NonDataLines_AndUnparseableData()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        var stream = string.Join("\n",
            "",
            ": comment",
            "event: ping",
            "data: not-json",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}",
            "data: {\"type\":\"message_stop\"}",
            "");
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("text/event-stream", stream);

        // Act
        var events = new List<AnthropicStreamEvent>();
        await foreach (var r in sut.CreateMessageStreamAsync(BuildRequest(), CancellationToken.None))
        {
            events.Add(r.Value);
        }

        // Assert
        events.Should().HaveCount(2);
        events[0].Type.Should().Be("content_block_delta");
        events[0].Delta!.Text.Should().Be("ok");
    }

    [Fact]
    public async Task CreateMessageStreamAsync_NullEventAfterDeserialization_IsSkipped()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        var stream = string.Join("\n",
            "data: null",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"x\"}}",
            "data: {\"type\":\"message_stop\"}",
            "");
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("text/event-stream", stream);

        // Act
        var events = new List<AnthropicStreamEvent>();
        await foreach (var r in sut.CreateMessageStreamAsync(BuildRequest(), CancellationToken.None))
        {
            events.Add(r.Value);
        }

        // Assert
        events.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateMessageStreamAsync_StopsWhenCancellationRequested()
    {
        // Arrange
        var (sut, handler) = TestFactories.CreateHttpClient();
        var stream = string.Join("\n",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"a\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"b\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"c\"}}",
            "data: {\"type\":\"message_stop\"}",
            "");
        handler.When(HttpMethod.Post, "*/v1/messages").Respond("text/event-stream", stream);

        using var cts = new CancellationTokenSource();
        var events = new List<AnthropicStreamEvent>();

        // Act
        await foreach (var r in sut.CreateMessageStreamAsync(BuildRequest(), cts.Token))
        {
            if (r.IsSuccess)
            {
                events.Add(r.Value);
            }

            cts.Cancel();
        }

        // Assert
        events.Should().NotBeEmpty();
        events.Should().HaveCountLessThan(4);
    }

    [Fact]
    public async Task CreateMessageStreamAsync_WhenLoggingEnabled_LogsRequest()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "*/v1/messages")
            .Respond("text/event-stream", "data: {\"type\":\"message_stop\"}\n");
        var options = TestFactories.DefaultOptions(o => o.EnableLogging = true);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl) };
        var logger = new TestFactories.RecordingLogger<AnthropicHttpClient>();
        var sut = new AnthropicHttpClient(httpClient, Options.Create(options), logger);

        // Act
        await foreach (var _ in sut.CreateMessageStreamAsync(BuildRequest(), CancellationToken.None))
        {
        }

        // Assert
        logger.Entries.Should().Contain(e => e.Message.Contains("Anthropic stream request"));
    }

    private static HttpClient GetUnderlyingHttpClient(AnthropicHttpClient sut)
    {
        var field = typeof(AnthropicHttpClient)
            .GetField("_httpClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field.Should().NotBeNull();
        return (HttpClient)field!.GetValue(sut)!;
    }
}
