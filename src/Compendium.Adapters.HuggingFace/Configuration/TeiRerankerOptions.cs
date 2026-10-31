// -----------------------------------------------------------------------
// <copyright file="TeiRerankerOptions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.HuggingFace.Configuration;

/// <summary>
/// Configuration of the <see cref="IReranker"/> over a Text Embeddings Inference (TEI) server's <c>/rerank</c> route, a
/// self-hosted or dedicated endpoint that serves one cross-encoder (bge-reranker-v2-m3, for instance).
/// </summary>
public sealed class TeiRerankerOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "TeiReranker";

    /// <summary>The batches in flight at once by default (<see cref="MaxConcurrentBatches"/>).</summary>
    public const int DefaultMaxConcurrentBatches = 4;

    /// <summary>
    /// The batch TEI accepts by default (<c>--max-client-batch-size 32</c>): a larger one is answered with a 422.
    /// </summary>
    public const int DefaultMaxBatchSize = 32;

    /// <summary>
    /// The server's base URL, an absolute <c>http</c> or <c>https</c> URL, e.g. <c>http://tei-reranker:8080</c>;
    /// <c>/rerank</c> is appended. Required.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The key the server was started with (<c>--api-key</c>), sent as <c>Authorization: Bearer &lt;key&gt;</c>; unset, no
    /// header is sent.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Whether TEI cuts a query + passage pair longer than the model takes (<c>truncate</c>), rather than refusing it with
    /// a 422. Default <see langword="true"/>.
    /// </summary>
    public bool Truncate { get; set; } = true;

    /// <summary>Which end TEI cuts when it truncates (<c>truncation_direction</c>). Default <see cref="TeiTruncationDirection.Right"/>.</summary>
    public TeiTruncationDirection TruncationDirection { get; set; } = TeiTruncationDirection.Right;

    /// <summary>
    /// The most documents sent in one request; more are sent in several, scored apart (a cross-encoder scores each pair on
    /// its own) and merged. Default <see cref="DefaultMaxBatchSize"/>; raise it up to the server's
    /// <c>--max-client-batch-size</c>.
    /// </summary>
    public int MaxBatchSize { get; set; } = DefaultMaxBatchSize;

    /// <summary>
    /// The most batches in flight at once. TEI takes one permit per (query, passage) pair from its
    /// <c>--max-concurrent-requests</c> (512 by default) and answers 429 at once when they run out, so a large rerank does
    /// not send all its batches together. Default <see cref="DefaultMaxConcurrentBatches"/>.
    /// </summary>
    public int MaxConcurrentBatches { get; set; } = DefaultMaxConcurrentBatches;

    /// <summary>
    /// The time one <see cref="IReranker.RerankAsync"/> call may take, every batch included, in seconds; past it, the
    /// call ends with <c>AI.RerankFailed</c>. Default 10.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// When <see langword="true"/>, logs the request bodies at <see cref="LogLevel.Debug"/>. The passages are in them.
    /// </summary>
    public bool EnableLogging { get; set; }

    /// <summary>
    /// Whether the options can serve a request: an absolute <c>http</c> / <c>https</c> base URL, and
    /// <see cref="MaxBatchSize"/>, <see cref="MaxConcurrentBatches"/> and <see cref="TimeoutSeconds"/> of at least 1.
    /// Checked when the reranker is resolved (<c>AddCompendiumTeiReranker</c> validates on start) and again before each
    /// call.
    /// </summary>
    public bool IsValid() => Problem() is null;

    /// <summary>What makes the options unable to serve a request, or <see langword="null"/>.</summary>
    internal string? Problem()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return $"BaseUrl must be an absolute http or https URL, not '{BaseUrl}'";
        }

        if (MaxBatchSize < 1)
        {
            return $"MaxBatchSize must be at least 1, not {MaxBatchSize}";
        }

        if (MaxConcurrentBatches < 1)
        {
            return $"MaxConcurrentBatches must be at least 1, not {MaxConcurrentBatches}";
        }

        return TimeoutSeconds < 1 ? $"TimeoutSeconds must be at least 1, not {TimeoutSeconds}" : null;
    }

    /// <summary>The message <c>AddCompendiumTeiReranker</c> fails the start with when <see cref="IsValid"/> is false.</summary>
    internal const string InvalidMessage =
        "TeiRerankerOptions: BaseUrl must be an absolute http or https URL, and MaxBatchSize, MaxConcurrentBatches and TimeoutSeconds at least 1.";
}

/// <summary>Which end of a too-long input TEI cuts (<c>truncation_direction</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TeiTruncationDirection
{
    /// <summary>The end of the input is cut.</summary>
    Right,

    /// <summary>The start of the input is cut.</summary>
    Left,
}
