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

    /// <summary>
    /// The batch TEI accepts by default (<c>--max-client-batch-size 32</c>): a larger one is answered with a 422.
    /// </summary>
    public const int DefaultMaxBatchSize = 32;

    /// <summary>
    /// The server's base URL, e.g. <c>http://tei-reranker:8080</c>; <c>/rerank</c> is appended. Required.
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
    /// The time one <see cref="IReranker.RerankAsync"/> call may take, every batch included, in seconds; past it, the
    /// call ends with <c>AI.RerankFailed</c>. Default 10.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// When <see langword="true"/>, logs the request bodies at <see cref="LogLevel.Debug"/>. The passages are in them.
    /// </summary>
    public bool EnableLogging { get; set; }

    /// <summary>Whether the options can serve a request: a base URL, a batch of at least one, a timeout.</summary>
    public bool IsValid() =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out _) && MaxBatchSize >= 1 && TimeoutSeconds >= 1;
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
