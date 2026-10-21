// -----------------------------------------------------------------------
// <copyright file="ZAiProviderSurfaceTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.ZAi.Glm;
using Compendium.Adapters.ZAi.Tests.TestSupport;

namespace Compendium.Adapters.ZAi.Tests.Services;

/// <summary>The rest of the <see cref="IAIProvider"/> surface: id, model list, health check, embeddings.</summary>
public class ZAiProviderSurfaceTests
{
    [Fact]
    public void ProviderId_IsZai()
    {
        var (client, _) = TestFactories.CreateHttpClient();

        TestFactories.CreateProvider(client).ProviderId.Should().Be("zai");
    }

    [Fact]
    public async Task TheModelList_IsTheDocumentedGlms_WithWhatTheFamilyKnows()
    {
        var (client, handler) = TestFactories.CreateHttpClient();

        var result = await TestFactories.CreateProvider(client).ListModelsAsync(CancellationToken.None);

        result.Value.Select(m => m.Id).Should().Equal("glm-5.3", "glm-5.3-flash", "glm-5.3-flashx", "glm-5.2", "glm-5.1", "glm-5", "glm-4.7", "glm-4.6");
        result.Value.Should().OnlyContain(m => m.Provider == "zai" && m.SupportsTools && m.SupportsStreaming && !m.SupportsEmbeddings);
        var glm53 = result.Value[0];
        glm53.ContextWindow.Should().Be(1_048_576);
        glm53.MaxOutputTokens.Should().Be(131_072);
        glm53.Metadata.Should().Contain(GlmDialect.ReasoningMetadataKey, "always");
        result.Value[1].SupportsVision.Should().BeTrue("GLM 5.3 Flash reads images");
        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task TheHealthCheck_IsAOneTokenProbe()
    {
        var (sut, _, body) = TestFactories.Replying(
            "application/json", """{"id":"h","model":"glm-5.3","choices":[{"index":0,"finish_reason":"length","message":{"role":"assistant","content":""}}]}""");

        var result = await sut.HealthCheckAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        body().Should().Be("""{"model":"glm-5.3","messages":[{"role":"user","content":"ping"}],"max_tokens":1,"stream":false}""");
    }

    [Fact]
    public async Task TheHealthCheck_ReturnsTheProvidersError()
    {
        var (sut, _, _) = TestFactories.Replying(
            "application/json", """{"error":{"code":"1001","message":"Authentication parameter not received in Header, unable to authenticate"}}""", HttpStatusCode.Unauthorized);

        var result = await sut.HealthCheckAsync(CancellationToken.None);

        result.Error.Code.Should().Be("AI.InvalidApiKey");
    }

    [Fact]
    public async Task TheHealthCheck_ReportsAnUnexpectedFailureAsUnavailable()
    {
        var (client, handler) = TestFactories.CreateHttpClient();
        handler.When(HttpMethod.Post, "*").Throw(new InvalidOperationException("boom"));

        var result = await TestFactories.CreateProvider(client).HealthCheckAsync(CancellationToken.None);

        result.Error.Code.Should().Be("AI.ProviderUnavailable");
    }

    [Fact]
    public async Task Embeddings_AreRefused()
    {
        var (client, _) = TestFactories.CreateHttpClient();

        var result = await TestFactories.CreateProvider(client).EmbedAsync(
            new EmbeddingRequest { Model = "x", Inputs = new List<string> { "a" } }, CancellationToken.None);

        result.Error.Code.Should().Be("AI.Unsupported");
    }

    [Fact]
    public async Task ANullRequest_IsRefused()
    {
        var (client, _) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(client);

        var complete = () => sut.CompleteAsync(null!, CancellationToken.None);
        var stream = async () =>
        {
            await foreach (var _ in sut.StreamCompleteAsync(null!, CancellationToken.None))
            {
            }
        };

        await complete.Should().ThrowAsync<ArgumentNullException>();
        await stream.Should().ThrowAsync<ArgumentNullException>();
    }
}
