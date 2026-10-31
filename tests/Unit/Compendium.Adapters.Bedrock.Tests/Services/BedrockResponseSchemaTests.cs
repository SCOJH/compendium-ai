// -----------------------------------------------------------------------
// <copyright file="BedrockResponseSchemaTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.BedrockRuntime.Model.Internal.MarshallTransformations;
using Compendium.Adapters.Bedrock.Tests.TestSupport;

namespace Compendium.Adapters.Bedrock.Tests.Services;

/// <summary>
/// The portable <see cref="CompletionRequest.ResponseSchema"/> on Converse's <c>outputConfig.textFormat</c>, sent to the
/// Claude models that have it; the other models get the request without it, without an error, and a warning.
/// </summary>
public class BedrockResponseSchemaTests
{
    private const string Schema =
        """{"type":"object","properties":{"output":{"enum":["billing","none"]}},"required":["output"],"additionalProperties":false}""";

    private const string Claude45 = "us.anthropic.claude-sonnet-4-5-20250929-v1:0";

    private static readonly ResponseSchema Route = new() { Name = "route", SchemaJson = Schema };

    [Fact]
    public async Task CompleteAsync_WithResponseSchemaOnClaude45_SendsItAsOutputConfigTextFormat()
    {
        var (sut, client, _) = Create();
        ConverseRequest? captured = null;
        client.ConverseAsync(Arg.Do<ConverseRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());

        var result = await sut.CompleteAsync(Request(Claude45) with { ResponseSchema = Route });

        result.IsSuccess.Should().BeTrue();
        var body = JsonDocument.Parse(Body(ConverseRequestMarshaller.Instance.Marshall(captured!))).RootElement;
        var format = body.GetProperty("outputConfig").GetProperty("textFormat");
        format.GetProperty("type").GetString().Should().Be("json_schema");
        var definition = format.GetProperty("structure").GetProperty("jsonSchema");
        definition.GetProperty("name").GetString().Should().Be("route");
        definition.GetProperty("schema").GetString().Should().Be(Schema, "Converse takes the schema as a JSON string");
    }

    [Fact]
    public void BuildConverseStreamRequest_WithResponseSchemaOnClaude45_SendsItAsOutputConfigTextFormat()
    {
        var (sut, _, logger) = Create();

        var request = sut.BuildConverseStreamRequest(Request(Claude45) with { ResponseSchema = Route }, Claude45);

        var body = JsonDocument.Parse(Body(ConverseStreamRequestMarshaller.Instance.Marshall(request))).RootElement;
        body.GetProperty("outputConfig").GetProperty("textFormat").GetProperty("structure").GetProperty("jsonSchema")
            .GetProperty("schema").GetString().Should().Be(Schema);
        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void BuildConverseRequest_WithoutResponseSchema_SendsNoOutputConfig()
    {
        var (sut, _, _) = Create();

        var request = sut.BuildConverseRequest(Request(Claude45), Claude45);

        request.OutputConfig.Should().BeNull();
        JsonDocument.Parse(Body(ConverseRequestMarshaller.Instance.Marshall(request))).RootElement
            .TryGetProperty("outputConfig", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_WithResponseSchemaOnAnOlderModel_SendsTheRequestWithoutItAndWarns()
    {
        var (sut, client, logger) = Create();
        ConverseRequest? captured = null;
        client.ConverseAsync(Arg.Do<ConverseRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());

        // The default model, Claude 3 Haiku; an invalid schema is not even checked where it is not sent.
        var result = await sut.CompleteAsync(Request(null!) with { ResponseSchema = Route with { SchemaJson = "{oops" } });

        result.IsSuccess.Should().BeTrue();
        captured!.OutputConfig.Should().BeNull();
        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain("anthropic.claude-3-haiku-20240307-v1:0").And.Contain("route").And.Contain("not sent");
    }

    [Fact]
    public void BuildConverseStreamRequest_WithResponseSchemaOnAModelWithoutIt_Warns()
    {
        var (sut, _, logger) = Create();

        sut.BuildConverseStreamRequest(Request("x") with { ResponseSchema = Route }, "meta.llama3-1-70b-instruct-v1:0")
            .OutputConfig.Should().BeNull();
        sut.BuildConverseStreamRequest(Request("x"), "meta.llama3-1-70b-instruct-v1:0");

        logger.Warnings.Should().ContainSingle().Which.Should().Contain("meta.llama3-1-70b-instruct-v1:0");
    }

    [Fact]
    public async Task CompleteAsync_WithAnInvalidResponseSchemaOnClaude45_FailsBeforeAnyRequest()
    {
        var (sut, client, _) = Create();

        var result = await sut.CompleteAsync(Request(Claude45) with { ResponseSchema = Route with { SchemaJson = "[]" } });

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AI.InvalidRequest");
        await client.DidNotReceiveWithAnyArgs().ConverseAsync(default!, default);
    }

    [Fact]
    public async Task StreamCompleteAsync_WithAnInvalidResponseSchemaOnClaude45_EndsWithOneFailureBeforeAnyRequest()
    {
        var (sut, client, _) = Create();

        var chunks = new List<Result<CompletionChunk>>();
        await foreach (var chunk in sut.StreamCompleteAsync(Request(Claude45) with { ResponseSchema = Route with { Name = "a b" } }))
        {
            chunks.Add(chunk);
        }

        chunks.Should().ContainSingle().Which.Error.Code.Should().Be("AI.InvalidRequest");
        await client.DidNotReceiveWithAnyArgs().ConverseStreamAsync(default!, default);
    }

    [Theory]
    [InlineData("anthropic.claude-sonnet-4-5-20250929-v1:0", true)]
    [InlineData("global.anthropic.claude-haiku-4-5-20251001-v1:0", true)]
    [InlineData("us.anthropic.claude-opus-4-7", true)]
    [InlineData("anthropic.claude-opus-5-5", true)]
    [InlineData("eu.anthropic.claude-haiku-4-5-20251001-v1:0", true)]
    [InlineData("in.anthropic.claude-sonnet-4-5-20250929-v1:0", true)]
    [InlineData("in.anthropic.claude-haiku-4-5-20251001-v1:0", false)]
    [InlineData("arn:aws:bedrock:ap-south-1:123456789012:inference-profile/in.anthropic.claude-haiku-4-5-20251001-v1:0", false)]
    [InlineData("anthropic.claude-sonnet-4-20250514-v1:0", false)]
    [InlineData("anthropic.claude-opus-4-1-20250805-v1:0", false)]
    [InlineData("anthropic.claude-3-5-sonnet-20241022-v2:0", false)]
    [InlineData("meta.llama3-1-70b-instruct-v1:0", false)]
    [InlineData("", false)]
    public void SupportsJsonSchema_ReadsTheModelId(string model, bool expected)
    {
        IAIProvider sut = BedrockTestFactories.CreateProvider(Substitute.For<IAmazonBedrockRuntime>());

        // An empty id is the default model, Claude 3 Haiku here.
        sut.SupportsJsonSchema(model).Should().Be(expected);
    }

    private static CompletionRequest Request(string model) =>
        BedrockTestFactories.SimpleCompletionRequest() with { Model = model };

    private static string Body(Amazon.Runtime.Internal.IRequest marshalled) =>
        marshalled.Content is { } content
            ? Encoding.UTF8.GetString(content)
            : new StreamReader(marshalled.ContentStream).ReadToEnd();

    private static (BedrockAIProvider Provider, IAmazonBedrockRuntime Client, RecordingLogger<BedrockAIProvider> Logger) Create()
    {
        var client = Substitute.For<IAmazonBedrockRuntime>();
        var logger = new RecordingLogger<BedrockAIProvider>();
        var provider = new BedrockAIProvider(client, Microsoft.Extensions.Options.Options.Create(BedrockTestFactories.DefaultOptions()), logger);
        return (provider, client, logger);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IEnumerable<string> Warnings => Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
