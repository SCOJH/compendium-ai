// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.HuggingFace.Configuration;
using Compendium.Adapters.HuggingFace.Http;
using Compendium.Adapters.HuggingFace.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Compendium.Adapters.HuggingFace.DependencyInjection;

/// <summary>
/// DI registration helpers for the Hugging Face adapter.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Hugging Face <see cref="IAIProvider"/> bound to a configuration section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">A configuration root (the <c>HuggingFace</c> section is bound).</param>
    public static IServiceCollection AddCompendiumHuggingFace(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<HuggingFaceOptions>(
            configuration.GetSection(HuggingFaceOptions.SectionName));

        return services.AddCompendiumHuggingFaceCore();
    }

    /// <summary>
    /// Registers the Hugging Face <see cref="IAIProvider"/> using an inline configuration callback.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to mutate <see cref="HuggingFaceOptions"/>.</param>
    public static IServiceCollection AddCompendiumHuggingFace(
        this IServiceCollection services,
        Action<HuggingFaceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        return services.AddCompendiumHuggingFaceCore();
    }

    /// <summary>
    /// Registers the <see cref="IReranker"/> over a Text Embeddings Inference server's <c>/rerank</c>, bound to a
    /// configuration section (the <c>TeiReranker</c> section is bound).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">A configuration root.</param>
    public static IServiceCollection AddCompendiumTeiReranker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<TeiRerankerOptions>().Bind(configuration.GetSection(TeiRerankerOptions.SectionName));
        return services.AddCompendiumTeiRerankerCore();
    }

    /// <summary>
    /// Registers the <see cref="IReranker"/> over a Text Embeddings Inference server's <c>/rerank</c>, using an inline
    /// configuration callback.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to mutate <see cref="TeiRerankerOptions"/>.</param>
    public static IServiceCollection AddCompendiumTeiReranker(
        this IServiceCollection services,
        Action<TeiRerankerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<TeiRerankerOptions>().Configure(configure);
        return services.AddCompendiumTeiRerankerCore();
    }

    // No resilience handler: a rerank runs on a short budget and its caller falls back to the order it has, so a retry
    // with backoff would only spend the budget. The HTTP timeout is the call's own, as a backstop.
    private static IServiceCollection AddCompendiumTeiRerankerCore(this IServiceCollection services)
    {
        // A misconfiguration fails the start (or the first resolution), not a rerank.
        services.AddOptions<TeiRerankerOptions>()
            .Validate(options => options.IsValid(), TeiRerankerOptions.InvalidMessage)
            .ValidateOnStart();

        services.AddHttpClient<TeiHttpClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<TeiRerankerOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
        });

        services.AddSingleton<TeiReranker>();
        services.AddSingleton<IReranker>(sp => sp.GetRequiredService<TeiReranker>());

        return services;
    }

    private static IServiceCollection AddCompendiumHuggingFaceCore(this IServiceCollection services)
    {
        services.AddHttpClient<HuggingFaceHttpClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<HuggingFaceOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        })
        .AddStandardResilienceHandler();

        services.AddSingleton<HuggingFaceAIProvider>();
        services.AddSingleton<IAIProvider>(sp => sp.GetRequiredService<HuggingFaceAIProvider>());

        return services;
    }
}
