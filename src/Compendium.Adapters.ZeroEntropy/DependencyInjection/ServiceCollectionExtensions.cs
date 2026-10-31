// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.ZeroEntropy.Configuration;
using Compendium.Adapters.ZeroEntropy.Http;
using Compendium.Adapters.ZeroEntropy.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Compendium.Adapters.ZeroEntropy.DependencyInjection;

/// <summary>
/// DI registration helpers for the ZeroEntropy adapter.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IReranker"/> over ZeroEntropy's <c>/models/rerank</c>, bound to a configuration section (the
    /// <c>ZeroEntropyReranker</c> section is bound).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">A configuration root.</param>
    public static IServiceCollection AddCompendiumZeroEntropyReranker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ZeroEntropyRerankerOptions>().Bind(configuration.GetSection(ZeroEntropyRerankerOptions.SectionName));
        return services.AddCompendiumZeroEntropyRerankerCore();
    }

    /// <summary>
    /// Registers the <see cref="IReranker"/> over ZeroEntropy's <c>/models/rerank</c>, using an inline configuration callback.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to mutate <see cref="ZeroEntropyRerankerOptions"/>.</param>
    public static IServiceCollection AddCompendiumZeroEntropyReranker(
        this IServiceCollection services,
        Action<ZeroEntropyRerankerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<ZeroEntropyRerankerOptions>().Configure(configure);
        return services.AddCompendiumZeroEntropyRerankerCore();
    }

    // No resilience handler: a rerank runs on a short budget and its caller falls back to the order it has, so a retry
    // with backoff would only spend the budget. The HTTP timeout is the call's own, as a backstop.
    private static IServiceCollection AddCompendiumZeroEntropyRerankerCore(this IServiceCollection services)
    {
        // A misconfiguration fails the start (or the first resolution), not a rerank.
        services.AddOptions<ZeroEntropyRerankerOptions>()
            .Validate(options => options.IsValid(), ZeroEntropyRerankerOptions.InvalidMessage)
            .ValidateOnStart();

        services.AddHttpClient<ZeroEntropyHttpClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<ZeroEntropyRerankerOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
        });

        services.AddSingleton<ZeroEntropyReranker>();
        services.AddSingleton<IReranker>(sp => sp.GetRequiredService<ZeroEntropyReranker>());

        return services;
    }
}
