// -----------------------------------------------------------------------
// <copyright file="ZeroEntropyReranker.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using Compendium.Adapters.ZeroEntropy.Configuration;
using Compendium.Adapters.ZeroEntropy.Http;
using Compendium.Adapters.ZeroEntropy.Http.Models;

namespace Compendium.Adapters.ZeroEntropy.Services;

/// <summary>
/// <see cref="IReranker"/> over ZeroEntropy's <c>/models/rerank</c>: the documents are scored against the query by a
/// zerank model, and come back by descending relevance (0 to 1), each with its index in the input list.
/// </summary>
/// <remarks>
/// <para>
/// A batch holds at most <see cref="ZeroEntropyRerankerOptions.MaxBatchSize"/> documents and
/// <see cref="ZeroEntropyRerankerOptions.MaxBatchBytes"/> bytes as ZeroEntropy counts them (150 per document, plus the
/// query and the document); more go in several requests, at most
/// <see cref="ZeroEntropyRerankerOptions.MaxConcurrentBatches"/> at a time, and are merged: ZeroEntropy documents each score
/// as depending only on the query and that document. Equal scores keep the input order. The first batch that fails stops
/// the others.
/// </para>
/// <para>
/// Every failure is a <see cref="Result"/>, never an exception: a document too large for a request on its own, a 4xx or
/// 5xx, an unreadable answer, an index outside its batch, a transport error, or the call running past
/// <see cref="ZeroEntropyRerankerOptions.TimeoutSeconds"/> is <c>AI.RerankFailed</c>, and so is an answer that does not
/// rank every document of its batch exactly once, or options that cannot serve a request
/// (<see cref="ZeroEntropyRerankerOptions.IsValid"/>). Only the caller's own cancellation throws.
/// </para>
/// <para>
/// <see cref="RerankOptions.Model"/> picks the model, <see cref="ZeroEntropyRerankerOptions.Model"/> when unset.
/// <see cref="RerankOptions.TopN"/> is applied after the merge, not sent; <see cref="RerankOptions.ReturnDocuments"/> fills
/// <see cref="RerankedDocument.Document"/> from the input (the API does not send the texts back).
/// </para>
/// </remarks>
internal sealed class ZeroEntropyReranker : IReranker
{
    private readonly ZeroEntropyHttpClient _httpClient;
    private readonly ZeroEntropyRerankerOptions _options;
    private readonly ILogger<ZeroEntropyReranker> _logger;

    public ZeroEntropyReranker(ZeroEntropyHttpClient httpClient, IOptions<ZeroEntropyRerankerOptions> options, ILogger<ZeroEntropyReranker> logger)
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
                AIErrors.RerankFailed($"the ZeroEntropy reranker is misconfigured: {problem}"));
        }

        if (Batch(query, documents) is not { } batches)
        {
            return Result.Failure<IReadOnlyList<RerankedDocument>>(AIErrors.RerankFailed(TooLarge(query, documents)));
        }

        var model = string.IsNullOrWhiteSpace(opts.Model) ? _options.Model : opts.Model;
        var timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        _logger.LogDebug(
            "Reranking {Count} documents with ZeroEntropy {Model} in {Batches} batches, {Concurrent} at a time",
            documents.Count,
            model,
            batches.Count,
            _options.MaxConcurrentBatches);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        // A failed batch cancels this one, which stops the batches in flight and those still waiting for the gate.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var gate = new SemaphoreSlim(_options.MaxConcurrentBatches);
        var answers = new Result<IReadOnlyList<ZeroEntropyRerankResult>>?[batches.Count];
        var cut = false;

        async Task SendAsync(int b)
        {
            await gate.WaitAsync(stop.Token);
            try
            {
                var answer = await _httpClient.RerankAsync(Request(model, query, documents, batches[b]), stop.Token);
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
            await Task.WhenAll(Enumerable.Range(0, batches.Count).Select(SendAsync));
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
            _logger.LogWarning("ZeroEntropy rerank of {Count} documents ran past {Timeout}", documents.Count, timeout);
            return Result.Failure<IReadOnlyList<RerankedDocument>>(
                AIErrors.RerankFailed($"ZeroEntropy did not answer within {timeout.TotalSeconds} seconds"));
        }

        var ranked = new List<RerankedDocument>(documents.Count);
        for (var b = 0; b < batches.Count; b++)
        {
            // Every batch ran, and succeeded.
            var answer = answers[b]!;
            var (offset, length) = batches[b];
            if (answer.Value.Count != length)
            {
                return Result.Failure<IReadOnlyList<RerankedDocument>>(AIErrors.RerankFailed(
                    $"ZeroEntropy ranked {answer.Value.Count} documents of a batch of {length}"));
            }

            var seen = new HashSet<int>();
            foreach (var result in answer.Value)
            {
                if (result.Index < 0 || result.Index >= length || !seen.Add(result.Index))
                {
                    return Result.Failure<IReadOnlyList<RerankedDocument>>(AIErrors.RerankFailed(
                        $"ZeroEntropy answered index {result.Index} for a batch of {length} documents"));
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

    /// <summary>The bytes ZeroEntropy counts for one document: 150, plus the query and the document in UTF-8.</summary>
    internal static long Bytes(string query, string document) =>
        ZeroEntropyRerankerOptions.BytesPerDocument + (long)Encoding.UTF8.GetByteCount(query) + Encoding.UTF8.GetByteCount(document);

    /// <summary>
    /// The batches, in input order, each as (offset, length): a batch is closed when it holds
    /// <see cref="ZeroEntropyRerankerOptions.MaxBatchSize"/> documents or the next one would take it past
    /// <see cref="ZeroEntropyRerankerOptions.MaxBatchBytes"/>. <see langword="null"/> when a document is past them alone.
    /// </summary>
    private List<(int Offset, int Length)>? Batch(string query, IReadOnlyList<string> documents)
    {
        var batches = new List<(int Offset, int Length)>();
        var offset = 0;
        var length = 0;
        long bytes = 0;
        for (var i = 0; i < documents.Count; i++)
        {
            var size = Bytes(query, documents[i]);
            if (size > _options.MaxBatchBytes)
            {
                return null;
            }

            if (length == _options.MaxBatchSize || bytes + size > _options.MaxBatchBytes)
            {
                batches.Add((offset, length));
                offset = i;
                length = 0;
                bytes = 0;
            }

            length++;
            bytes += size;
        }

        batches.Add((offset, length));
        return batches;
    }

    private string TooLarge(string query, IReadOnlyList<string> documents)
    {
        var i = Enumerable.Range(0, documents.Count).First(i => Bytes(query, documents[i]) > _options.MaxBatchBytes);
        return $"document {i} is {Bytes(query, documents[i])} bytes with the query, as ZeroEntropy counts them, past MaxBatchBytes ({_options.MaxBatchBytes})";
    }

    private ZeroEntropyRerankRequest Request(string model, string query, IReadOnlyList<string> documents, (int Offset, int Length) batch) => new()
    {
        Model = model,
        Query = query,
        Documents = documents.Skip(batch.Offset).Take(batch.Length).ToList(),
        Latency = _options.Latency switch
        {
            ZeroEntropyLatency.Fast => "fast",
            ZeroEntropyLatency.Slow => "slow",
            _ => null,
        },
    };
}
