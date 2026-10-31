// -----------------------------------------------------------------------
// <copyright file="BedrockResponseSchemaTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Compendium.Adapters.Bedrock.Tests.TestSupport;

namespace Compendium.Adapters.Bedrock.Tests.Services;

/// <summary>
/// The portable <see cref="CompletionRequest.ResponseSchema"/> on Bedrock, which this adapter does not ask to follow a
/// schema: the Converse request goes without it, without an error, and a warning says so.
/// </summary>
public class BedrockResponseSchemaTests
{
    private static readonly ResponseSchema Route = new() { Name = "route", SchemaJson = """{"type":"object"}""" };

    [Fact]
    public async Task CompleteAsync_WithResponseSchema_SendsTheRequestWithoutItAndWarns()
    {
        var client = Substitute.For<IAmazonBedrockRuntime>();
        ConverseRequest? captured = null;
        client.ConverseAsync(Arg.Do<ConverseRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(BedrockTestFactories.SuccessConverseResponse());
        var logger = new RecordingLogger<BedrockAIProvider>();
        var sut = new BedrockAIProvider(client, Microsoft.Extensions.Options.Options.Create(BedrockTestFactories.DefaultOptions()), logger);

        var result = await sut.CompleteAsync(BedrockTestFactories.SimpleCompletionRequest() with { ResponseSchema = Route with { SchemaJson = "{oops" } });

        result.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.ToolConfig.Should().BeNull();
        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain("anthropic.claude-3-haiku-20240307-v1:0").And.Contain("route").And.Contain("not sent");
    }

    [Fact]
    public void BuildConverseStreamRequest_WithResponseSchema_Warns()
    {
        var logger = new RecordingLogger<BedrockAIProvider>();
        var sut = new BedrockAIProvider(Substitute.For<IAmazonBedrockRuntime>(), Microsoft.Extensions.Options.Options.Create(BedrockTestFactories.DefaultOptions()), logger);

        sut.BuildConverseStreamRequest(BedrockTestFactories.SimpleCompletionRequest() with { ResponseSchema = Route }, "meta.llama3-1-70b-instruct-v1:0");
        sut.BuildConverseStreamRequest(BedrockTestFactories.SimpleCompletionRequest(), "meta.llama3-1-70b-instruct-v1:0");

        logger.Warnings.Should().ContainSingle().Which.Should().Contain("meta.llama3-1-70b-instruct-v1:0");
    }

    [Fact]
    public void SupportsJsonSchema_IsFalse()
    {
        IAIProvider sut = BedrockTestFactories.CreateProvider(Substitute.For<IAmazonBedrockRuntime>());

        sut.SupportsJsonSchema("anthropic.claude-sonnet-4-5-20250929-v1:0").Should().BeFalse();
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
