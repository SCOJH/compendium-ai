// -----------------------------------------------------------------------
// <copyright file="CohereRerankerRegistrationTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using Compendium.Adapters.Cohere.DependencyInjection;
using Compendium.Adapters.Cohere.Http;
using Compendium.Adapters.Cohere.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Compendium.Adapters.Cohere.Tests.DependencyInjection;

public class CohereRerankerRegistrationTests
{
    [Fact]
    public void AddCompendiumCohereReranker_WithConfigureAction_RegistersTheReranker()
    {
        var services = Services();

        services.AddCompendiumCohereReranker(o =>
        {
            o.ApiKey = "co";
            o.TimeoutSeconds = 3;
        });
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IReranker>().Should().BeOfType<CohereReranker>();
        sp.GetRequiredService<IReranker>().Should().BeSameAs(sp.GetRequiredService<CohereReranker>());
        sp.GetRequiredService<IOptions<CohereRerankerOptions>>().Value.TimeoutSeconds.Should().Be(3);
        sp.GetRequiredService<CohereHttpClient>().Should().NotBeNull();
    }

    [Fact]
    public void AddCompendiumCohereReranker_WithConfiguration_BindsTheCohereRerankerSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CohereReranker:BaseUrl"] = "https://cohere.internal",
                ["CohereReranker:ApiKey"] = "k",
                ["CohereReranker:Model"] = "rerank-v3.5",
                ["CohereReranker:MaxTokensPerDoc"] = "2048",
                ["CohereReranker:MaxBatchSize"] = "100",
                ["CohereReranker:MaxConcurrentBatches"] = "2",
                ["CohereReranker:TimeoutSeconds"] = "5",
            })
            .Build();
        var services = Services();

        services.AddCompendiumCohereReranker(configuration);
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<CohereRerankerOptions>>().Value;
        options.BaseUrl.Should().Be("https://cohere.internal");
        options.ApiKey.Should().Be("k");
        options.Model.Should().Be("rerank-v3.5");
        options.MaxTokensPerDoc.Should().Be(2048);
        options.MaxBatchSize.Should().Be(100);
        options.MaxConcurrentBatches.Should().Be(2);
        options.TimeoutSeconds.Should().Be(5);
        sp.GetService<IReranker>().Should().NotBeNull();
    }

    [Theory]
    [InlineData("", "co", "BaseUrl must be an absolute http or https URL")]
    [InlineData("api.cohere.com", "co", "BaseUrl must be an absolute http or https URL")]
    [InlineData(CohereRerankerOptions.DefaultBaseUrl, "", "ApiKey and Model are required")]
    public void AddCompendiumCohereReranker_WithOptionsThatCannotServe_FailsAtResolution(string baseUrl, string apiKey, string message)
    {
        var services = Services();

        services.AddCompendiumCohereReranker(o =>
        {
            o.BaseUrl = baseUrl;
            o.ApiKey = apiKey;
        });
        using var sp = services.BuildServiceProvider();

        ((Action)(() => sp.GetRequiredService<IReranker>())).Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(message);
    }

    [Fact]
    public void AddCompendiumCohereReranker_WithABatchAboveCoheresTenThousandDocuments_FailsAtResolution()
    {
        var services = Services();

        services.AddCompendiumCohereReranker(o =>
        {
            o.ApiKey = "co";
            o.MaxBatchSize = 10_001;
        });
        using var sp = services.BuildServiceProvider();

        ((Action)(() => sp.GetRequiredService<IReranker>())).Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("MaxBatchSize between 1 and 10000");
    }

    [Fact]
    public void AddCompendiumCohereReranker_WithATimeoutBelowOne_FailsAtResolution()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CohereReranker:ApiKey"] = "k",
                ["CohereReranker:TimeoutSeconds"] = "0",
            })
            .Build();
        var services = Services();

        services.AddCompendiumCohereReranker(configuration);
        using var sp = services.BuildServiceProvider();

        ((Action)(() => _ = sp.GetRequiredService<IOptions<CohereRerankerOptions>>().Value)).Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddCompendiumCohereReranker_ValidatesOnStart()
    {
        var services = Services();

        services.AddCompendiumCohereReranker(o => o.ApiKey = string.Empty);
        using var sp = services.BuildServiceProvider();

        // What a host runs before it starts.
        ((Action)(() => sp.GetRequiredService<IStartupValidator>().Validate())).Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public async Task AddCompendiumCohereReranker_NeverLogsTheKey_EvenAtTrace()
    {
        const string key = "co-registered-key-do-not-log";
        var logs = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddCompendiumCohereReranker(o =>
        {
            o.ApiKey = key;
            o.EnableLogging = true;
        });
        services.AddHttpClient<CohereHttpClient>().ConfigurePrimaryHttpMessageHandler(() => new AnswerHandler());
        await using var sp = services.BuildServiceProvider();

        var result = await sp.GetRequiredService<IReranker>().RerankAsync("q", ["a"], new RerankOptions(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        logs.Entries.Should().Contain(e => e.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal), "the client's own logging ran");
        logs.Entries.Should().NotContain(e => e.Message.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void AddCompendiumCohereReranker_NullArguments_Throw()
    {
        var services = Services();

        ((Action)(() => services.AddCompendiumCohereReranker((Action<CohereRerankerOptions>)null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => services.AddCompendiumCohereReranker((IConfiguration)null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => ((IServiceCollection)null!).AddCompendiumCohereReranker(_ => { }))).Should().Throw<ArgumentNullException>();
        ((Action)(() => ((IServiceCollection)null!).AddCompendiumCohereReranker(new ConfigurationBuilder().Build()))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CohereRerankerOptions_Defaults()
    {
        var options = new CohereRerankerOptions();

        options.BaseUrl.Should().Be("https://api.cohere.com");
        options.Model.Should().Be("rerank-v5.0-pro");
        options.MaxTokensPerDoc.Should().BeNull();
        options.MaxBatchSize.Should().Be(1000);
        options.MaxConcurrentBatches.Should().Be(4);
        options.TimeoutSeconds.Should().Be(10);
        options.IsValid().Should().BeFalse("there is no key");
    }

    [Theory]
    [InlineData("https://api.cohere.com", "k", "rerank-v3.5", null, 1000, 10, true)]
    [InlineData("http://cohere.internal:8080", "k", "rerank-v3.5", 512, 1, 1, true)]
    [InlineData("not a url", "k", "rerank-v3.5", null, 1000, 10, false)]
    [InlineData("https://api.cohere.com", "", "rerank-v3.5", null, 1000, 10, false)]
    [InlineData("https://api.cohere.com", "k", " ", null, 1000, 10, false)]
    [InlineData("https://api.cohere.com", "k", "rerank-v3.5", -1, 1000, 10, false)]
    [InlineData("https://api.cohere.com", "k", "rerank-v3.5", null, 0, 10, false)]
    [InlineData("https://api.cohere.com", "k", "rerank-v3.5", null, 10_000, 10, true)]
    [InlineData("https://api.cohere.com", "k", "rerank-v3.5", null, 10_001, 10, false)]
    [InlineData("https://api.cohere.com", "k", "rerank-v3.5", null, 1000, 0, false)]
    public void CohereRerankerOptions_IsValid(string baseUrl, string apiKey, string model, int? maxTokensPerDoc, int maxBatchSize, int timeoutSeconds, bool expected)
    {
        new CohereRerankerOptions
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Model = model,
            MaxTokensPerDoc = maxTokensPerDoc,
            MaxBatchSize = maxBatchSize,
            TimeoutSeconds = timeoutSeconds,
        }.IsValid().Should().Be(expected);
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        return services;
    }

    /// <summary>Answers one document, whatever is asked.</summary>
    private sealed class AnswerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":[{"index":0,"relevance_score":0.5}]}""", Encoding.UTF8, "application/json"),
            });
    }
}
