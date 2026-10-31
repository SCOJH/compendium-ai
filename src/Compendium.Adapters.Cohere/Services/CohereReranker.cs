// -----------------------------------------------------------------------
// <copyright file="CohereReranker.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Cohere.Configuration;
using Compendium.Adapters.Cohere.Http;
using Compendium.Adapters.Cohere.Http.Models;

namespace Compendium.Adapters.Cohere.Services;

/// <summary>
/// <see cref="IReranker"/> over Cohere's <c>/v2/rerank</c>: the documents are scored against the query by a Cohere rerank
/// model, and come back by descending relevance (0 to 1), each with its index in the input list.
/// </summary>
/// <remarks>
/// <para>
/// More documents than <see cref="CohereRerankerOptions.MaxBatchSize"/> go in several requests, at most
/// <see cref="CohereRerankerOptions.MaxConcurrentBatches"/> at a time, and are merged: a rerank model scores each
/// (query, document) pair. Equal scores keep the input order. The first batch that fails stops the others.
/// </para>
/// <para>
/// Every failure is a <see cref="Result"/>, never an exception: a 4xx or 5xx, an unreadable answer, an index outside its
/// batch, a transport error, or the call running past <see cref="CohereRerankerOptions.TimeoutSeconds"/> is
/// <c>AI.RerankFailed</c>, and so is an answer that does not rank every document of its batch exactly once, or options that
/// cannot serve a request (<see cref="CohereRerankerOptions.IsValid"/>). Only the caller's own cancellation throws.
/// </para>
/// <para>
/// <see cref="RerankOptions.Model"/> picks the model, <see cref="CohereRerankerOptions.Model"/> when unset.
/// <see cref="RerankOptions.TopN"/> is applied after the merge, not sent; <see cref="RerankOptions.ReturnDocuments"/> fills
/// <see cref="RerankedDocument.Document"/> from the input (the v2 API does not send the texts back).
/// </para>
/// </remarks>
internal sealed class CohereReranker : IReranker
{
    private readonly CohereHttpClient _httpClient;
    private readonly CohereRerankerOptions _options;
    private readonly ILogger<CohereReranker> _logger;

    public CohereReranker(CohereHttpClient httpClient, IOptions<CohereRerankerOptions> options, ILogger<CohereReranker> logger)
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

        if (_options.Problem() is { } problem)
        {
            return Result.Failure<IReadOnlyList<RerankedDocument>>(
                AIErrors.RerankFailed($"the Cohere reranker is misconfigured: {problem}"));
        }

        var model = string.IsNullOrWhiteSpace(opts.Model) ? _options.Model : opts.Model;
        var batchSize = _options.MaxBatchSize;
        var batchCount = (documents.Count + batchSize - 1) / batchSize;
        var timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        _logger.LogDebug(
            "Reranking {Count} documents with Cohere {Model} in {Batches} batches of {BatchSize}, {Concurrent} at a time",
            documents.Count,
            model,
            batchCount,
            batchSize,
            _options.MaxConcurrentBatches);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        // A failed batch cancels this one, which stops the batches in flight and those still waiting for the gate.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var gate = new SemaphoreSlim(_options.MaxConcurrentBatches);
        var answers = new Result<IReadOnlyList<CohereRerankResult>>?[batchCount];
        var cut = false;

        async Task SendAsync(int b)
        {
            await gate.WaitAsync(stop.Token);
            try
            {
                var answer = await _httpClient.RerankAsync(Request(model, query, documents, b * batchSize, batchSize), stop.Token);
                answers[b] = answer;
                if (answer.IsFailure)
                {
                    stop.Cancel();
                }
            }
            finally
            {
                gate.Release();
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, batchCount).Select(SendAsync));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            cut = true;
        }

        // A failed batch first, however the loop ended (cut short by it, or done); then the deadline.
        if (answers.FirstOrDefault(a => a is { IsFailure: true }) is { } failed)
        {
            return Result.Failure<IReadOnlyList<RerankedDocument>>(failed.Error);
        }

        if (cut)
        {
            _logger.LogWarning("Cohere rerank of {Count} documents ran past {Timeout}", documents.Count, timeout);
            return Result.Failure<IReadOnlyList<RerankedDocument>>(
                AIErrors.RerankFailed($"Cohere did not answer within {timeout.TotalSeconds} seconds"));
        }

        var ranked = new List<RerankedDocument>(documents.Count);
        for (var b = 0; b < batchCount; b++)
        {
            // Every batch ran, and succeeded.
            var answer = answers[b]!;
            var offset = b * batchSize;
            var length = Math.Min(batchSize, documents.Count - offset);
            if (answer.Value.Count != length)
            {
                return Result.Failure<IReadOnlyList<RerankedDocument>>(AIErrors.RerankFailed(
                    $"Cohere ranked {answer.Value.Count} documents of a batch of {length}"));
            }

            var seen = new HashSet<int>();
            foreach (var result in answer.Value)
            {
                if (result.Index < 0 || result.Index >= length || !seen.Add(result.Index))
                {
                    return Result.Failure<IReadOnlyList<RerankedDocument>>(AIErrors.RerankFailed(
                        $"Cohere answered index {result.Index} for a batch of {length} documents"));
                }

                var index = offset + result.Index;
                ranked.Add(new RerankedDocument
                {
                    Index = index,
                    RelevanceScore = result.RelevanceScore,
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

    private CohereRerankRequest Request(string model, string query, IReadOnlyList<string> documents, int offset, int batchSize) => new()
    {
        Model = model,
        Query = query,
        Documents = documents.Skip(offset).Take(batchSize).ToList(),
        MaxTokensPerDoc = _options.MaxTokensPerDoc,
    };
}
