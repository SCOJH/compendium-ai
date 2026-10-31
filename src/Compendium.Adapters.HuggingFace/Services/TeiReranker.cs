// -----------------------------------------------------------------------
// <copyright file="TeiReranker.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.HuggingFace.Configuration;
using Compendium.Adapters.HuggingFace.Http;
using Compendium.Adapters.HuggingFace.Http.Models;

namespace Compendium.Adapters.HuggingFace.Services;

/// <summary>
/// <see cref="IReranker"/> over a Text Embeddings Inference server's <c>/rerank</c>: the documents are scored against the
/// query by the cross-encoder the server serves, and come back by descending relevance (0 to 1), each with its index in the
/// input list.
/// </summary>
/// <remarks>
/// <para>
/// More documents than <see cref="TeiRerankerOptions.MaxBatchSize"/> go in several requests, sent together: a cross-encoder
/// scores each pair on its own, so the scores of the batches compare, and are merged. Equal scores keep the input order.
/// </para>
/// <para>
/// Every failure is a <see cref="Result"/>, never an exception: a 4xx or 5xx, an unreadable answer, an index outside its
/// batch, a transport error, or the call running past <see cref="TeiRerankerOptions.TimeoutSeconds"/> is
/// <c>AI.RerankFailed</c>. Only the caller's own cancellation throws.
/// </para>
/// <para>
/// <see cref="RerankOptions.Model"/> is not sent: a TEI server serves one model. <see cref="RerankOptions.ReturnDocuments"/>
/// fills <see cref="RerankedDocument.Document"/> from the input; the texts do not travel back.
/// </para>
/// </remarks>
internal sealed class TeiReranker : IReranker
{
    private readonly TeiHttpClient _httpClient;
    private readonly TeiRerankerOptions _options;
    private readonly ILogger<TeiReranker> _logger;

    public TeiReranker(TeiHttpClient httpClient, IOptions<TeiRerankerOptions> options, ILogger<TeiReranker> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RerankedDocument>>> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        RerankOptions opts,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(opts);

        if (string.IsNullOrWhiteSpace(query))
        {
            return Result.Failure<IReadOnlyList<RerankedDocument>>(AIErrors.InvalidQuery("The rerank query is empty."));
        }

        if (opts.TopN is < 1)
        {
            return Result.Failure<IReadOnlyList<RerankedDocument>>(
                AIErrors.InvalidRequest($"TopN must be at least 1, not {opts.TopN}."));
        }

        if (documents.Count == 0)
        {
            return Result.Success<IReadOnlyList<RerankedDocument>>(Array.Empty<RerankedDocument>());
        }

        var batchSize = Math.Max(1, _options.MaxBatchSize);
        var timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        _logger.LogDebug(
            "Reranking {Count} documents with TEI in batches of {BatchSize}",
            documents.Count,
            batchSize);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        Result<IReadOnlyList<TeiRank>>[] answers;
        try
        {
            var batches = Enumerable.Range(0, (documents.Count + batchSize - 1) / batchSize)
                .Select(b => _httpClient.RerankAsync(Request(query, documents, b * batchSize, batchSize), deadline.Token));
            answers = await Task.WhenAll(batches);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("TEI rerank of {Count} documents ran past {Timeout}", documents.Count, timeout);
            return Result.Failure<IReadOnlyList<RerankedDocument>>(
                AIErrors.RerankFailed($"TEI did not answer within {timeout.TotalSeconds} seconds"));
        }

        var ranked = new List<RerankedDocument>(documents.Count);
        for (var b = 0; b < answers.Length; b++)
        {
            if (answers[b].IsFailure)
            {
                return Result.Failure<IReadOnlyList<RerankedDocument>>(answers[b].Error);
            }

            var offset = b * batchSize;
            var length = Math.Min(batchSize, documents.Count - offset);
            var seen = new HashSet<int>();
            foreach (var rank in answers[b].Value)
            {
                if (rank.Index < 0 || rank.Index >= length || !seen.Add(rank.Index))
                {
                    return Result.Failure<IReadOnlyList<RerankedDocument>>(AIErrors.RerankFailed(
                        $"TEI answered index {rank.Index} for a batch of {length} documents"));
                }

                var index = offset + rank.Index;
                ranked.Add(new RerankedDocument
                {
                    Index = index,
                    RelevanceScore = rank.Score,
                    Document = opts.ReturnDocuments ? documents[index] : null,
                });
            }
        }

        IEnumerable<RerankedDocument> ordered = ranked.OrderByDescending(d => d.RelevanceScore).ThenBy(d => d.Index);
        if (opts.TopN is { } topN)
        {
            ordered = ordered.Take(topN);
        }

        return Result.Success<IReadOnlyList<RerankedDocument>>(ordered.ToList());
    }

    private TeiRerankRequest Request(string query, IReadOnlyList<string> documents, int offset, int batchSize) => new()
    {
        Query = query,
        Texts = documents.Skip(offset).Take(batchSize).ToList(),
        Truncate = _options.Truncate,
        TruncationDirection = _options.TruncationDirection,
    };
}
