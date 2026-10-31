// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Cohere.Configuration;
using Compendium.Adapters.Cohere.Http;
using Compendium.Adapters.Cohere.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Compendium.Adapters.Cohere.DependencyInjection;

/// <summary>
/// DI registration helpers for the Cohere adapter.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IReranker"/> over Cohere's <c>/v2/rerank</c>, bound to a configuration section (the
    /// <c>CohereReranker</c> section is bound).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">A configuration root.</param>
    public static IServiceCollection AddCompendiumCohereReranker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CohereRerankerOptions>().Bind(configuration.GetSection(CohereRerankerOptions.SectionName));
        return services.AddCompendiumCohereRerankerCore();
    }

    /// <summary>
    /// Registers the <see cref="IReranker"/> over Cohere's <c>/v2/rerank</c>, using an inline configuration callback.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to mutate <see cref="CohereRerankerOptions"/>.</param>
    public static IServiceCollection AddCompendiumCohereReranker(
        this IServiceCollection services,
        Action<CohereRerankerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<CohereRerankerOptions>().Configure(configure);
        return services.AddCompendiumCohereRerankerCore();
    }

    // No resilience handler: a rerank runs on a short budget and its caller falls back to the order it has, so a retry
    // with backoff would only spend the budget. The HTTP timeout is the call's own, as a backstop.
    private static IServiceCollection AddCompendiumCohereRerankerCore(this IServiceCollection services)
    {
        // A misconfiguration fails the start (or the first resolution), not a rerank.
        services.AddOptions<CohereRerankerOptions>()
            .Validate(options => options.IsValid(), CohereRerankerOptions.InvalidMessage)
            .ValidateOnStart();

        services.AddHttpClient<CohereHttpClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<CohereRerankerOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
        });

        services.AddSingleton<CohereReranker>();
        services.AddSingleton<IReranker>(sp => sp.GetRequiredService<CohereReranker>());

        return services;
    }
}
