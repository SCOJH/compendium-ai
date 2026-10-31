// -----------------------------------------------------------------------
// <copyright file="ZeroEntropyRerankerOptions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.ZeroEntropy.Configuration;

/// <summary>
/// Configuration of the <see cref="IReranker"/> over ZeroEntropy's rerank API (<c>POST /v1/models/rerank</c>).
/// </summary>
public sealed class ZeroEntropyRerankerOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "ZeroEntropyReranker";

    /// <summary>ZeroEntropy's API (<see cref="BaseUrl"/>).</summary>
    public const string DefaultBaseUrl = "https://api.zeroentropy.dev/v1";

    /// <summary>ZeroEntropy's API in its EU datacenters.</summary>
    public const string EuBaseUrl = "https://eu-api.zeroentropy.dev/v1";

    /// <summary>The model used when the call names none (<see cref="Model"/>): ZeroEntropy's flagship reranker.</summary>
    public const string DefaultModel = "zerank-2";

    /// <summary>The documents sent in one request by default (<see cref="MaxBatchSize"/>).</summary>
    public const int DefaultMaxBatchSize = 1000;

    /// <summary>
    /// ZeroEntropy's maximum request size, in its own count (<see cref="MaxBatchBytes"/>): 5 000 000 bytes, on every tier.
    /// </summary>
    public const int DefaultMaxBatchBytes = 5_000_000;

    /// <summary>
    /// What ZeroEntropy counts for each document on top of its text and the query's: 150 bytes.
    /// </summary>
    public const int BytesPerDocument = 150;

    /// <summary>The batches in flight at once by default (<see cref="MaxConcurrentBatches"/>).</summary>
    public const int DefaultMaxConcurrentBatches = 4;

    /// <summary>
    /// The API's base URL, an absolute <c>http</c> or <c>https</c> URL; <c>/models/rerank</c> is appended. Default
    /// <see cref="DefaultBaseUrl"/>; <see cref="EuBaseUrl"/> for the EU datacenters.
    /// </summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>The API key, sent as <c>Authorization: Bearer &lt;key&gt;</c>. Required; never logged.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The model (<c>model</c>) when <see cref="RerankOptions.Model"/> is unset: <c>zerank-2</c>, <c>zerank-1</c>,
    /// <c>zerank-1-small</c>. Default <see cref="DefaultModel"/>.
    /// </summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>
    /// The latency mode (<c>latency</c>). Unset, it is not sent: ZeroEntropy tries a fast call and falls back to a slow one
    /// past the fast rate limit. <see cref="ZeroEntropyLatency.Fast"/> makes it answer 429 instead.
    /// </summary>
    public ZeroEntropyLatency? Latency { get; set; }

    /// <summary>
    /// The most documents sent in one request; more are sent in several and merged. Default
    /// <see cref="DefaultMaxBatchSize"/>.
    /// </summary>
    public int MaxBatchSize { get; set; } = DefaultMaxBatchSize;

    /// <summary>
    /// The most bytes in one request, as ZeroEntropy counts them: for each document, <see cref="BytesPerDocument"/> plus
    /// the UTF-8 length of the query and of the document. A batch is closed before it goes past them; a single document
    /// past them fails the call before any request. At most, and by default, <see cref="DefaultMaxBatchBytes"/>, ZeroEntropy's
    /// maximum.
    /// </summary>
    public int MaxBatchBytes { get; set; } = DefaultMaxBatchBytes;

    /// <summary>
    /// The most batches in flight at once: each counts against the key's rate limits (by default 1 000 requests and
    /// 500 000 bytes a minute, refreshed every 15 seconds). Default <see cref="DefaultMaxConcurrentBatches"/>.
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
    /// <see cref="MaxBatchBytes"/> between 1 and <see cref="DefaultMaxBatchBytes"/>, and <see cref="MaxBatchSize"/>,
    /// <see cref="MaxConcurrentBatches"/> and <see cref="TimeoutSeconds"/> of at least 1. Checked when the reranker is resolved
    /// (<c>AddCompendiumZeroEntropyReranker</c> validates on start) and again before each call.
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

        if (MaxBatchSize < 1)
        {
            return $"MaxBatchSize must be at least 1, not {MaxBatchSize}";
        }

        if (MaxBatchBytes is < 1 or > DefaultMaxBatchBytes)
        {
            return $"MaxBatchBytes must be between 1 and {DefaultMaxBatchBytes}, not {MaxBatchBytes}";
        }

        if (MaxConcurrentBatches < 1)
        {
            return $"MaxConcurrentBatches must be at least 1, not {MaxConcurrentBatches}";
        }

        return TimeoutSeconds < 1 ? $"TimeoutSeconds must be at least 1, not {TimeoutSeconds}" : null;
    }

    /// <summary>The message <c>AddCompendiumZeroEntropyReranker</c> fails the start with when <see cref="IsValid"/> is false.</summary>
    internal const string InvalidMessage =
        "ZeroEntropyRerankerOptions: BaseUrl must be an absolute http or https URL, ApiKey and Model are required, MaxBatchBytes between 1 and 5000000, and MaxBatchSize, MaxConcurrentBatches and TimeoutSeconds at least 1.";
}

/// <summary>ZeroEntropy's latency mode (<c>latency</c>).</summary>
public enum ZeroEntropyLatency
{
    /// <summary><c>fast</c>: a 429 rather than a slow call past the fast rate limit.</summary>
    Fast,

    /// <summary><c>slow</c>: the slow mode, whatever the load.</summary>
    Slow,
}
