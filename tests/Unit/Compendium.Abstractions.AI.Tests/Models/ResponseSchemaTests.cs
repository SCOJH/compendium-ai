// -----------------------------------------------------------------------
// <copyright file="ResponseSchemaTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Compendium.Abstractions.AI.Tests.Models;

public sealed class ResponseSchemaTests
{
    private const string Classifier =
        """{"type":"object","properties":{"output":{"enum":["billing","none"]},"confidence":{"type":"number"}},"required":["output","confidence"],"additionalProperties":false}""";

    [Fact]
    public void ResponseSchema_IsUnsetOnARequest_AndStrictByDefault()
    {
        var request = new CompletionRequest { Model = "m", Messages = [Message.User("Hi")] };
        var schema = new ResponseSchema { Name = "route", SchemaJson = Classifier };

        request.ResponseSchema.Should().BeNull();
        schema.Strict.Should().BeTrue();
    }

    [Fact]
    public void Parse_AJsonObject_ReturnsItAsItGoesOnTheWire()
    {
        var parsed = new ResponseSchema { Name = "route_v2-1", SchemaJson = Classifier }.Parse();

        parsed.IsSuccess.Should().BeTrue();
        parsed.Value.ValueKind.Should().Be(JsonValueKind.Object);
        parsed.Value.GetRawText().Should().Be(Classifier);
    }

    [Theory]
    [InlineData("{\"type\":")]
    [InlineData("not json")]
    public void Parse_InvalidJson_IsAnInvalidRequest(string schemaJson)
    {
        var parsed = new ResponseSchema { Name = "route", SchemaJson = schemaJson }.Parse();

        parsed.IsFailure.Should().BeTrue();
        parsed.Error.Code.Should().Be("AI.InvalidRequest");
        parsed.Error.Message.Should().Contain("not valid JSON");
    }

    [Theory]
    [InlineData("[]", "Array")]
    [InlineData("true", "True")]
    [InlineData("\"object\"", "String")]
    public void Parse_JsonThatIsNotAnObject_IsAnInvalidRequest(string schemaJson, string kind)
    {
        var parsed = new ResponseSchema { Name = "route", SchemaJson = schemaJson }.Parse();

        parsed.IsFailure.Should().BeTrue();
        parsed.Error.Code.Should().Be("AI.InvalidRequest");
        parsed.Error.Message.Should().Contain($"not {kind}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NoSchema_IsAnInvalidRequest(string schemaJson)
    {
        var parsed = new ResponseSchema { Name = "route", SchemaJson = schemaJson }.Parse();

        parsed.IsFailure.Should().BeTrue();
        parsed.Error.Message.Should().Contain("has no schema");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("dotted.name")]
    [InlineData("é")]
    public void Parse_ANameOutsideTheRule_IsAnInvalidRequest(string name)
    {
        var parsed = new ResponseSchema { Name = name, SchemaJson = Classifier }.Parse();

        parsed.IsFailure.Should().BeTrue();
        parsed.Error.Code.Should().Be("AI.InvalidRequest");
        parsed.Error.Message.Should().Contain("1 to 64 letters");
    }

    [Fact]
    public void Parse_TheNameLength_IsBoundedAt64()
    {
        new ResponseSchema { Name = new string('a', 64), SchemaJson = Classifier }.Parse().IsSuccess.Should().BeTrue();
        new ResponseSchema { Name = new string('a', 65), SchemaJson = Classifier }.Parse().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void SupportsJsonSchema_OnAProviderThatDoesNotSay_IsFalse()
    {
        IAIProvider provider = new SilentProvider();

        provider.SupportsJsonSchema("any-model").Should().BeFalse();
    }

    /// <summary>A provider written before <see cref="IAIProvider.SupportsJsonSchema"/> existed.</summary>
    private sealed class SilentProvider : IAIProvider
    {
        public string ProviderId => "silent";

        public Task<Result<CompletionResponse>> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<Result<CompletionChunk>> StreamCompleteAsync(
            CompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<Result<EmbeddingResponse>> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<IReadOnlyList<AIModel>>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> HealthCheckAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
