// -----------------------------------------------------------------------
// <copyright file="CohereRerankerOptions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.Cohere.Configuration;

/// <summary>
/// Configuration of the <see cref="IReranker"/> over Cohere's Rerank API (<c>POST /v2/rerank</c>).
/// </summary>
public sealed class CohereRerankerOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "CohereReranker";

    /// <summary>Cohere's API (<see cref="BaseUrl"/>).</summary>
    public const string DefaultBaseUrl = "https://api.cohere.com";

    /// <summary>The model used when the call names none (<see cref="Model"/>): the one Cohere's reference uses.</summary>
    public const string DefaultModel = "rerank-v5.0-pro";

    /// <summary>
    /// The documents sent in one request by default (<see cref="MaxBatchSize"/>): Cohere recommends against sending more
    /// than 1 000.
    /// </summary>
    public const int DefaultMaxBatchSize = 1000;

    /// <summary>
    /// The most documents Cohere takes in one request (documents × <c>max_chunks_per_doc</c>, 1 by default, at most
    /// 10 000): the ceiling of <see cref="MaxBatchSize"/>.
    /// </summary>
    public const int MaxDocumentsPerRequest = 10_000;

    /// <summary>The batches in flight at once by default (<see cref="MaxConcurrentBatches"/>).</summary>
    public const int DefaultMaxConcurrentBatches = 4;

    /// <summary>
    /// The API's base URL, an absolute <c>http</c> or <c>https</c> URL; <c>/v2/rerank</c> is appended. Default
    /// <see cref="DefaultBaseUrl"/>.
    /// </summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>The API key, sent as <c>Authorization: Bearer &lt;key&gt;</c>. Required; never logged.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The model (<c>model</c>) when <see cref="RerankOptions.Model"/> is unset: <c>rerank-v5.0-pro</c>,
    /// <c>rerank-v5.0-fast</c>, <c>rerank-v4.0-pro</c>, <c>rerank-v4.0-fast</c>, <c>rerank-v3.5</c>… Default
    /// <see cref="DefaultModel"/>.
    /// </summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>
    /// The tokens past which Cohere cuts a document (<c>max_tokens_per_doc</c>); unset, it is not sent and Cohere cuts at
    /// 4 096. The 32k models (v4.0, v5.0) take more.
    /// </summary>
    public int? MaxTokensPerDoc { get; set; }

    /// <summary>
    /// The most documents sent in one request, at most <see cref="MaxDocumentsPerRequest"/>; more are sent in several and
    /// merged on their scores, which assumes a score does not depend on the other documents of its request (not
    /// guaranteed by Cohere). Default <see cref="DefaultMaxBatchSize"/>.
    /// </summary>
    public int MaxBatchSize { get; set; } = DefaultMaxBatchSize;

    /// <summary>
    /// The most batches in flight at once: each is a request against the key's rate limit (1 000 a minute on a production
    /// key, 10 on a trial key). Default <see cref="DefaultMaxConcurrentBatches"/>.
    /// </summary>
    public int MaxConcurrentBatches { get; set; } = DefaultMaxConcurrentBatches;

    /// <summary>
    /// The time one <see cref="IReranker.RerankAsync"/> call may take, every batch included, in seconds; past it, the
    /// call ends with <c>AI.RerankFailed</c>. Default 10.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// When <see langword="true"/>, logs the request bodies at <see cref="LogLevel.Debug"/>. The documents are in them;
    /// the key is not.
    /// </summary>
    public bool EnableLogging { get; set; }

    /// <summary>
    /// Whether the options can serve a request: an absolute <c>http</c> / <c>https</c> base URL, a key, a model,
    /// <see cref="MaxBatchSize"/> between 1 and <see cref="MaxDocumentsPerRequest"/>, <see cref="MaxConcurrentBatches"/> and
    /// <see cref="TimeoutSeconds"/> of at least 1, and
    /// <see cref="MaxTokensPerDoc"/> unset or at least 1. Checked when the reranker is resolved
    /// (<c>AddCompendiumCohereReranker</c> validates on start) and again before each call.
    /// </summary>
    public bool IsValid() => Problem() is null;

    /// <summary>What makes the options unable to serve a request, or <see langword="null"/>. Never the key.</summary>
    internal string? Problem()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return $"BaseUrl must be an absolute http or https URL, not '{BaseUrl}'";
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            return "ApiKey is required";
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            return "Model is required";
        }

        if (MaxTokensPerDoc is < 1)
        {
            return $"MaxTokensPerDoc must be at least 1 when set, not {MaxTokensPerDoc}";
        }

        if (MaxBatchSize is < 1 or > MaxDocumentsPerRequest)
        {
            return $"MaxBatchSize must be between 1 and {MaxDocumentsPerRequest}, not {MaxBatchSize}";
        }

        if (MaxConcurrentBatches < 1)
        {
            return $"MaxConcurrentBatches must be at least 1, not {MaxConcurrentBatches}";
        }

        return TimeoutSeconds < 1 ? $"TimeoutSeconds must be at least 1, not {TimeoutSeconds}" : null;
    }

    /// <summary>The message <c>AddCompendiumCohereReranker</c> fails the start with when <see cref="IsValid"/> is false.</summary>
    internal const string InvalidMessage =
        "CohereRerankerOptions: BaseUrl must be an absolute http or https URL, ApiKey and Model are required, MaxTokensPerDoc unset or at least 1, MaxBatchSize between 1 and 10000, and MaxConcurrentBatches and TimeoutSeconds at least 1.";
}
