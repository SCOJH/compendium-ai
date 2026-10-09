// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensionsTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Anthropic.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Compendium.Adapters.Anthropic.Tests.DependencyInjection;

public class ServiceCollectionExtensionsTests
{
    private static IServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        return services;
    }

    [Fact]
    public void AddCompendiumAnthropic_WithConfigureAction_RegistersOptionsAndProvider()
    {
        // Arrange
        var services = BuildServices();

        // Act
        services.AddCompendiumAnthropic(o => o.ApiKey = "sk-ant-xyz");
        var sp = services.BuildServiceProvider();

        // Assert
        sp.GetService<IOptions<AnthropicOptions>>().Should().NotBeNull();
        sp.GetService<IOptions<AnthropicOptions>>()!.Value.ApiKey.Should().Be("sk-ant-xyz");
        sp.GetService<IAIProvider>().Should().NotBeNull();
        sp.GetService<IAIProvider>()!.ProviderId.Should().Be("anthropic");
    }

    [Fact]
    public void AddCompendiumAnthropic_WithConfigureAction_RegistersHttpClientFactory()
    {
        // Arrange
        var services = BuildServices();

        // Act
        services.AddCompendiumAnthropic(o =>
        {
            o.ApiKey = "sk-ant";
            o.TimeoutSeconds = 42;
        });
        var sp = services.BuildServiceProvider();

        // Assert
        sp.GetService<IHttpClientFactory>().Should().NotBeNull();
        sp.GetRequiredService<IOptions<AnthropicOptions>>().Value.TimeoutSeconds.Should().Be(42);
    }

    [Fact]
    public void AddCompendiumAnthropic_WithConfigureAction_ResolvesIAIProviderAsSingleton()
    {
        // Arrange
        var services = BuildServices();
        services.AddCompendiumAnthropic(o => o.ApiKey = "sk-ant");
        var sp = services.BuildServiceProvider();

        // Act
        var first = sp.GetRequiredService<IAIProvider>();
        var second = sp.GetRequiredService<IAIProvider>();

        // Assert
        first.Should().BeSameAs(second);
    }

    [Fact]
    public void AddCompendiumAnthropic_WithConfiguration_BindsAnthropicSection()
    {
        // Arrange
        var services = BuildServices();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Anthropic:ApiKey"] = "sk-bound",
                ["Anthropic:DefaultModel"] = "claude-opus-4-5",
                ["Anthropic:DefaultMaxTokens"] = "8192",
                ["Anthropic:TimeoutSeconds"] = "30",
                ["Anthropic:EnablePromptCaching"] = "true",
            })
            .Build();

        // Act
        services.AddCompendiumAnthropic(config);
        var sp = services.BuildServiceProvider();

        // Assert
        var opts = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
        opts.ApiKey.Should().Be("sk-bound");
        opts.DefaultModel.Should().Be("claude-opus-4-5");
        opts.DefaultMaxTokens.Should().Be(8192);
        opts.TimeoutSeconds.Should().Be(30);
        opts.EnablePromptCaching.Should().BeTrue();
        sp.GetService<IAIProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddCompendiumAnthropic_WithWorkspaceId_PutsTheHeaderOnTheFactoryClient()
    {
        // Arrange
        var services = BuildServices();
        services.AddCompendiumAnthropic(o =>
        {
            o.ApiKey = "sk-ant";
            o.WorkspaceId = " wrkspc_01JwQvzr7rXLA5AGx3HKfFUJ ";
        });
        var sp = services.BuildServiceProvider();

        // Act — the named client of the factory, without the typed client's own header logic.
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("AnthropicHttpClient");

        // Assert
        client.DefaultRequestHeaders.GetValues("anthropic-workspace-id")
            .Should().ContainSingle().Which.Should().Be("wrkspc_01JwQvzr7rXLA5AGx3HKfFUJ");
    }

    [Fact]
    public void AddCompendiumAnthropic_WithoutWorkspaceId_PutsNoWorkspaceHeaderOnTheFactoryClient()
    {
        // Arrange
        var services = BuildServices();
        services.AddCompendiumAnthropic(o => o.ApiKey = "sk-ant");
        var sp = services.BuildServiceProvider();

        // Act
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("AnthropicHttpClient");

        // Assert
        client.DefaultRequestHeaders.Contains("anthropic-workspace-id").Should().BeFalse();
    }

    [Fact]
    public void AddCompendiumAnthropic_WithConfiguration_BindsWorkspaceId()
    {
        // Arrange
        var services = BuildServices();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Anthropic:ApiKey"] = "sk-bound",
                ["Anthropic:WorkspaceId"] = "wrkspc_01JwQvzr7rXLA5AGx3HKfFUJ",
            })
            .Build();

        // Act
        services.AddCompendiumAnthropic(config);
        var sp = services.BuildServiceProvider();

        // Assert
        sp.GetRequiredService<IOptions<AnthropicOptions>>().Value.WorkspaceId
            .Should().Be("wrkspc_01JwQvzr7rXLA5AGx3HKfFUJ");
    }

    [Fact]
    public void AddCompendiumAnthropic_ReturnsServiceCollectionForChaining()
    {
        // Arrange
        var services = BuildServices();

        // Act
        var returned = services.AddCompendiumAnthropic(o => o.ApiKey = "sk-ant");

        // Assert
        returned.Should().BeSameAs(services);
    }

    [Fact]
    public void AddCompendiumAnthropic_WithConfiguration_ReturnsServiceCollectionForChaining()
    {
        // Arrange
        var services = BuildServices();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Anthropic:ApiKey"] = "sk-ant" })
            .Build();

        // Act
        var returned = services.AddCompendiumAnthropic(config);

        // Assert
        returned.Should().BeSameAs(services);
    }

    [Fact]
    public void AddCompendiumAnthropic_WithNullServices_Throws()
    {
        IServiceCollection? services = null;

        var act1 = () => services!.AddCompendiumAnthropic(_ => { });
        act1.Should().Throw<ArgumentNullException>();

        var act2 = () => services!.AddCompendiumAnthropic(new ConfigurationBuilder().Build());
        act2.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddCompendiumAnthropic_WithNullConfigureAction_Throws()
    {
        var services = BuildServices();
        Action<AnthropicOptions>? configure = null;

        var act = () => services.AddCompendiumAnthropic(configure!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddCompendiumAnthropic_WithNullConfiguration_Throws()
    {
        var services = BuildServices();
        IConfiguration? configuration = null;

        var act = () => services.AddCompendiumAnthropic(configuration!);

        act.Should().Throw<ArgumentNullException>();
    }
}
