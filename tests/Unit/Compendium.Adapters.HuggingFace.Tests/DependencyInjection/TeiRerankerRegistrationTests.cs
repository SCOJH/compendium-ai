// -----------------------------------------------------------------------
// <copyright file="TeiRerankerRegistrationTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.HuggingFace.DependencyInjection;
using Compendium.Adapters.HuggingFace.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Compendium.Adapters.HuggingFace.Tests.DependencyInjection;

public class TeiRerankerRegistrationTests
{
    [Fact]
    public void AddCompendiumTeiReranker_WithConfigureAction_RegistersTheReranker()
    {
        var services = Services();

        services.AddCompendiumTeiReranker(o =>
        {
            o.BaseUrl = "http://tei:8080";
            o.TimeoutSeconds = 3;
        });
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IReranker>().Should().BeOfType<TeiReranker>();
        sp.GetRequiredService<IReranker>().Should().BeSameAs(sp.GetRequiredService<TeiReranker>());
        sp.GetRequiredService<IOptions<TeiRerankerOptions>>().Value.TimeoutSeconds.Should().Be(3);
        sp.GetRequiredService<TeiHttpClient>().Should().NotBeNull();
    }

    [Fact]
    public void AddCompendiumTeiReranker_WithConfiguration_BindsTheTeiRerankerSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TeiReranker:BaseUrl"] = "http://tei:8080",
                ["TeiReranker:ApiKey"] = "k",
                ["TeiReranker:Truncate"] = "false",
                ["TeiReranker:TruncationDirection"] = "Left",
                ["TeiReranker:MaxBatchSize"] = "100",
                ["TeiReranker:TimeoutSeconds"] = "5",
            })
            .Build();
        var services = Services();

        services.AddCompendiumTeiReranker(configuration);
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<TeiRerankerOptions>>().Value;
        options.BaseUrl.Should().Be("http://tei:8080");
        options.ApiKey.Should().Be("k");
        options.Truncate.Should().BeFalse();
        options.TruncationDirection.Should().Be(TeiTruncationDirection.Left);
        options.MaxBatchSize.Should().Be(100);
        options.TimeoutSeconds.Should().Be(5);
        sp.GetService<IReranker>().Should().NotBeNull();
    }

    [Fact]
    public void AddCompendiumTeiReranker_NullArguments_Throw()
    {
        var services = Services();

        ((Action)(() => services.AddCompendiumTeiReranker((Action<TeiRerankerOptions>)null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => services.AddCompendiumTeiReranker((IConfiguration)null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => ((IServiceCollection)null!).AddCompendiumTeiReranker(_ => { }))).Should().Throw<ArgumentNullException>();
        ((Action)(() => ((IServiceCollection)null!).AddCompendiumTeiReranker(new ConfigurationBuilder().Build()))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TeiRerankerOptions_Defaults_AreTeiDefaults()
    {
        var options = new TeiRerankerOptions();

        options.MaxBatchSize.Should().Be(32);
        options.Truncate.Should().BeTrue();
        options.TruncationDirection.Should().Be(TeiTruncationDirection.Right);
        options.TimeoutSeconds.Should().Be(10);
        options.ApiKey.Should().BeNull();
        options.IsValid().Should().BeFalse("there is no base URL");
    }

    [Theory]
    [InlineData("http://tei:8080", 32, 10, true)]
    [InlineData("not a url", 32, 10, false)]
    [InlineData("http://tei:8080", 0, 10, false)]
    [InlineData("http://tei:8080", 32, 0, false)]
    public void TeiRerankerOptions_IsValid(string baseUrl, int maxBatchSize, int timeoutSeconds, bool expected)
    {
        new TeiRerankerOptions { BaseUrl = baseUrl, MaxBatchSize = maxBatchSize, TimeoutSeconds = timeoutSeconds }
            .IsValid().Should().Be(expected);
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        return services;
    }
}
