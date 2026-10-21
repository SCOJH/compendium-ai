// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensionsTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.ZAi.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Compendium.Adapters.ZAi.Tests.DependencyInjection;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCompendiumZAi_WithConfiguration_RegistersIAIProvider()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ZAi:ApiKey"] = "zai-config",
            ["ZAi:BaseUrl"] = ZAiOptions.MainlandChinaBaseUrl,
            ["ZAi:DefaultModel"] = "glm-5.3-flash",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddCompendiumZAi(config);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAIProvider>().ProviderId.Should().Be("zai");
        var options = provider.GetRequiredService<IOptions<ZAiOptions>>().Value;
        options.ApiKey.Should().Be("zai-config");
        options.BaseUrl.Should().Be(ZAiOptions.MainlandChinaBaseUrl);
        options.DefaultModel.Should().Be("glm-5.3-flash");
    }

    [Fact]
    public void AddCompendiumZAi_WithAnAction_RegistersOneSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddCompendiumZAi(o =>
        {
            o.ApiKey = "zai-action";
            o.TimeoutSeconds = 42;
        });
        using var provider = services.BuildServiceProvider();

        var a = provider.GetRequiredService<IAIProvider>();
        a.Should().BeSameAs(provider.GetRequiredService<IAIProvider>());
        a.Should().BeOfType<ZAiAIProvider>();
        provider.GetRequiredService<IOptions<ZAiOptions>>().Value.TimeoutSeconds.Should().Be(42);
    }

    [Fact]
    public void TheDefaults()
    {
        var options = new ZAiOptions();

        options.BaseUrl.Should().Be("https://api.z.ai/api/paas/v4");
        options.DefaultModel.Should().Be("glm-5.3");
        options.DefaultMaxTokens.Should().Be(4096);
        options.TimeoutSeconds.Should().Be(300);
        options.EnableLogging.Should().BeFalse();
        ZAiOptions.SectionName.Should().Be("ZAi");
    }

    [Fact]
    public void NullArguments_AreRefused()
    {
        IServiceCollection? none = null;
        var services = new ServiceCollection();

        var noServicesAction = () => none!.AddCompendiumZAi(_ => { });
        var noServicesConfig = () => none!.AddCompendiumZAi(new ConfigurationBuilder().Build());
        var noAction = () => services.AddCompendiumZAi((Action<ZAiOptions>)null!);
        var noConfig = () => services.AddCompendiumZAi((IConfiguration)null!);

        noServicesAction.Should().Throw<ArgumentNullException>();
        noServicesConfig.Should().Throw<ArgumentNullException>();
        noAction.Should().Throw<ArgumentNullException>();
        noConfig.Should().Throw<ArgumentNullException>();
    }
}
