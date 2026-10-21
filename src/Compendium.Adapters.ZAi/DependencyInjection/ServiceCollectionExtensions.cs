// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.ZAi.Configuration;
using Compendium.Adapters.ZAi.Http;
using Compendium.Adapters.ZAi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Compendium.Adapters.ZAi.DependencyInjection;

/// <summary>
/// DI extensions for the Z.ai Compendium adapter.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Z.ai as the <see cref="IAIProvider"/> with options bound from <paramref name="configuration"/> at section
    /// <see cref="ZAiOptions.SectionName"/>.
    /// </summary>
    public static IServiceCollection AddCompendiumZAi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ZAiOptions>(configuration.GetSection(ZAiOptions.SectionName));
        return services.AddCompendiumZAiCore();
    }

    /// <summary>
    /// Registers Z.ai as the <see cref="IAIProvider"/> with options configured inline.
    /// </summary>
    public static IServiceCollection AddCompendiumZAi(
        this IServiceCollection services,
        Action<ZAiOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.Configure(configureOptions);
        return services.AddCompendiumZAiCore();
    }

    private static IServiceCollection AddCompendiumZAiCore(this IServiceCollection services)
    {
        services.AddHttpClient<ZAiHttpClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<ZAiOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        })
        .AddStandardResilienceHandler(o =>
        {
            // GLM reasons before it answers (5.3 always, at max effort by default): stretch the per-attempt and total
            // timeouts so a long reasoning is not cancelled. The circuit breaker's window must be at least twice the
            // attempt timeout.
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(300);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(900);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(600);
        });

        services.AddSingleton<ZAiAIProvider>();
        services.AddSingleton<IAIProvider>(sp => sp.GetRequiredService<ZAiAIProvider>());

        return services;
    }
}
