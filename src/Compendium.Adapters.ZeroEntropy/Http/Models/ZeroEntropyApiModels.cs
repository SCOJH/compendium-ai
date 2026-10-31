// -----------------------------------------------------------------------
// <copyright file="ZeroEntropyApiModels.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.ZeroEntropy.Http.Models;

/// <summary>
/// ZeroEntropy <c>POST /models/rerank</c> request. <c>top_n</c> is not sent: every batch comes back whole, so that it can
/// be checked, and the best are kept after the merge.
/// </summary>
internal sealed class ZeroEntropyRerankRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("query")]
    public required string Query { get; set; }

    [JsonPropertyName("documents")]
    public required IReadOnlyList<string> Documents { get; set; }

    /// <summary><c>"fast"</c>, <c>"slow"</c>, or not sent.</summary>
    [JsonPropertyName("latency")]
    public string? Latency { get; set; }
}

/// <summary>
/// ZeroEntropy's <c>/models/rerank</c> answer: <c>{results: [{index, relevance_score}], total_bytes, total_tokens,
/// actual_latency_mode, e2e_latency, inference_latency}</c>; only <c>results</c> is read.
/// </summary>
internal sealed class ZeroEntropyRerankResponse
{
    [JsonPropertyName("results")]
    public List<ZeroEntropyRerankResult>? Results { get; set; }
}

/// <summary>One entry of <c>results</c>: the document's position in <c>documents</c> and its score, 0 to 1.</summary>
internal sealed class ZeroEntropyRerankResult
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("relevance_score")]
    public double RelevanceScore { get; set; }
}
