// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Anthropic.Configuration;
using Compendium.Adapters.Anthropic.Http;
using Compendium.Adapters.Anthropic.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Compendium.Adapters.Anthropic.DependencyInjection;

/// <summary>
/// DI registration helpers for the Anthropic adapter.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Anthropic <see cref="IAIProvider"/> using
    /// <paramref name="configuration"/> bound to <see cref="AnthropicOptions.SectionName"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration source.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddCompendiumAnthropic(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SectionName));
        return services.AddCompendiumAnthropicCore();
    }

    /// <summary>
    /// Registers the Anthropic <see cref="IAIProvider"/> using an inline
    /// configuration callback.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Callback to mutate <see cref="AnthropicOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddCompendiumAnthropic(
        this IServiceCollection services,
        Action<AnthropicOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.Configure(configureOptions);
        return services.AddCompendiumAnthropicCore();
    }

    private static IServiceCollection AddCompendiumAnthropicCore(this IServiceCollection services)
    {
        services.AddHttpClient<AnthropicHttpClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Add("x-api-key", options.ApiKey);
            client.DefaultRequestHeaders.Add("anthropic-version", options.AnthropicVersion);
            if (!string.IsNullOrWhiteSpace(options.WorkspaceId))
            {
                client.DefaultRequestHeaders.Add(AnthropicHttpClient.WorkspaceHeader, options.WorkspaceId.Trim());
            }
        })
        .AddStandardResilienceHandler();

        services.AddSingleton<AnthropicAIProvider>();
        services.AddSingleton<IAIProvider>(sp => sp.GetRequiredService<AnthropicAIProvider>());

        return services;
    }
}
