// -----------------------------------------------------------------------
// <copyright file="ZeroEntropyRerankerRegistrationTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using Compendium.Adapters.ZeroEntropy.DependencyInjection;
using Compendium.Adapters.ZeroEntropy.Http;
using Compendium.Adapters.ZeroEntropy.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Compendium.Adapters.ZeroEntropy.Tests.DependencyInjection;

public class ZeroEntropyRerankerRegistrationTests
{
    [Fact]
    public void AddCompendiumZeroEntropyReranker_WithConfigureAction_RegistersTheReranker()
    {
        var services = Services();

        services.AddCompendiumZeroEntropyReranker(o =>
        {
            o.ApiKey = "ze";
            o.TimeoutSeconds = 3;
        });
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IReranker>().Should().BeOfType<ZeroEntropyReranker>();
        sp.GetRequiredService<IReranker>().Should().BeSameAs(sp.GetRequiredService<ZeroEntropyReranker>());
        sp.GetRequiredService<IOptions<ZeroEntropyRerankerOptions>>().Value.TimeoutSeconds.Should().Be(3);
        sp.GetRequiredService<ZeroEntropyHttpClient>().Should().NotBeNull();
    }

    [Fact]
    public void AddCompendiumZeroEntropyReranker_WithConfiguration_BindsTheZeroEntropyRerankerSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZeroEntropyReranker:BaseUrl"] = ZeroEntropyRerankerOptions.EuBaseUrl,
                ["ZeroEntropyReranker:ApiKey"] = "k",
                ["ZeroEntropyReranker:Model"] = "zerank-1-small",
                ["ZeroEntropyReranker:Latency"] = "Fast",
                ["ZeroEntropyReranker:MaxBatchBytes"] = "1000000",
                ["ZeroEntropyReranker:MaxBatchSize"] = "100",
                ["ZeroEntropyReranker:MaxConcurrentBatches"] = "2",
                ["ZeroEntropyReranker:TimeoutSeconds"] = "5",
            })
            .Build();
        var services = Services();

        services.AddCompendiumZeroEntropyReranker(configuration);
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<ZeroEntropyRerankerOptions>>().Value;
        options.BaseUrl.Should().Be("https://eu-api.zeroentropy.dev/v1");
        options.ApiKey.Should().Be("k");
        options.Model.Should().Be("zerank-1-small");
        options.Latency.Should().Be(ZeroEntropyLatency.Fast);
        options.MaxBatchBytes.Should().Be(1_000_000);
        options.MaxBatchSize.Should().Be(100);
        options.MaxConcurrentBatches.Should().Be(2);
        options.TimeoutSeconds.Should().Be(5);
        sp.GetService<IReranker>().Should().NotBeNull();
    }

    [Theory]
    [InlineData("", "ze", "BaseUrl must be an absolute http or https URL")]
    [InlineData("api.zeroentropy.dev/v1", "ze", "BaseUrl must be an absolute http or https URL")]
    [InlineData(ZeroEntropyRerankerOptions.DefaultBaseUrl, "", "ApiKey and Model are required")]
    public void AddCompendiumZeroEntropyReranker_WithOptionsThatCannotServe_FailsAtResolution(string baseUrl, string apiKey, string message)
    {
        var services = Services();

        services.AddCompendiumZeroEntropyReranker(o =>
        {
            o.BaseUrl = baseUrl;
            o.ApiKey = apiKey;
        });
        using var sp = services.BuildServiceProvider();

        ((Action)(() => sp.GetRequiredService<IReranker>())).Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(message);
    }

    [Fact]
    public void AddCompendiumZeroEntropyReranker_WithATimeoutBelowOne_FailsAtResolution()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZeroEntropyReranker:ApiKey"] = "k",
                ["ZeroEntropyReranker:TimeoutSeconds"] = "0",
            })
            .Build();
        var services = Services();

        services.AddCompendiumZeroEntropyReranker(configuration);
        using var sp = services.BuildServiceProvider();

        ((Action)(() => _ = sp.GetRequiredService<IOptions<ZeroEntropyRerankerOptions>>().Value)).Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddCompendiumZeroEntropyReranker_ValidatesOnStart()
    {
        var services = Services();

        services.AddCompendiumZeroEntropyReranker(o => o.ApiKey = string.Empty);
        using var sp = services.BuildServiceProvider();

        // What a host runs before it starts.
        ((Action)(() => sp.GetRequiredService<IStartupValidator>().Validate())).Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public async Task AddCompendiumZeroEntropyReranker_NeverLogsTheKey_EvenAtTrace()
    {
        const string key = "ze-registered-key-do-not-log";
        var logs = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddCompendiumZeroEntropyReranker(o =>
        {
            o.ApiKey = key;
            o.EnableLogging = true;
        });
        services.AddHttpClient<ZeroEntropyHttpClient>().ConfigurePrimaryHttpMessageHandler(() => new AnswerHandler());
        await using var sp = services.BuildServiceProvider();

        var result = await sp.GetRequiredService<IReranker>().RerankAsync("q", ["a"], new RerankOptions(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        logs.Entries.Should().Contain(e => e.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal), "the client's own logging ran");
        logs.Entries.Should().NotContain(e => e.Message.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void AddCompendiumZeroEntropyReranker_NullArguments_Throw()
    {
        var services = Services();

        ((Action)(() => services.AddCompendiumZeroEntropyReranker((Action<ZeroEntropyRerankerOptions>)null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => services.AddCompendiumZeroEntropyReranker((IConfiguration)null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => ((IServiceCollection)null!).AddCompendiumZeroEntropyReranker(_ => { }))).Should().Throw<ArgumentNullException>();
        ((Action)(() => ((IServiceCollection)null!).AddCompendiumZeroEntropyReranker(new ConfigurationBuilder().Build()))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ZeroEntropyRerankerOptions_Defaults()
    {
        var options = new ZeroEntropyRerankerOptions();

        options.BaseUrl.Should().Be("https://api.zeroentropy.dev/v1");
        options.Model.Should().Be("zerank-2");
        options.Latency.Should().BeNull();
        options.MaxBatchBytes.Should().Be(5_000_000);
        options.MaxBatchSize.Should().Be(1000);
        options.MaxConcurrentBatches.Should().Be(4);
        options.TimeoutSeconds.Should().Be(10);
        options.IsValid().Should().BeFalse("there is no key");
    }

    [Theory]
    [InlineData("https://api.zeroentropy.dev/v1", "k", "zerank-2", 5_000_000, 1000, 10, true)]
    [InlineData("http://zeroentropy.internal:8080", "k", "zerank-1", 1, 1, 1, true)]
    [InlineData("not a url", "k", "zerank-2", 5_000_000, 1000, 10, false)]
    [InlineData("https://api.zeroentropy.dev/v1", "", "zerank-2", 5_000_000, 1000, 10, false)]
    [InlineData("https://api.zeroentropy.dev/v1", "k", " ", 5_000_000, 1000, 10, false)]
    [InlineData("https://api.zeroentropy.dev/v1", "k", "zerank-2", 0, 1000, 10, false)]
    [InlineData("https://api.zeroentropy.dev/v1", "k", "zerank-2", 5_000_000, 0, 10, false)]
    [InlineData("https://api.zeroentropy.dev/v1", "k", "zerank-2", 5_000_000, 1000, 0, false)]
    public void ZeroEntropyRerankerOptions_IsValid(string baseUrl, string apiKey, string model, int maxBatchBytes, int maxBatchSize, int timeoutSeconds, bool expected)
    {
        new ZeroEntropyRerankerOptions
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Model = model,
            MaxBatchBytes = maxBatchBytes,
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
