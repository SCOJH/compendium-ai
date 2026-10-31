// -----------------------------------------------------------------------
// <copyright file="CohereApiModels.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.Cohere.Http.Models;

/// <summary>
/// Cohere <c>POST /v2/rerank</c> request. <c>top_n</c> is not sent: every batch comes back whole, so that it can be
/// checked, and the best are kept after the merge.
/// </summary>
internal sealed class CohereRerankRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("query")]
    public required string Query { get; set; }

    [JsonPropertyName("documents")]
    public required IReadOnlyList<string> Documents { get; set; }

    [JsonPropertyName("max_tokens_per_doc")]
    public int? MaxTokensPerDoc { get; set; }
}

/// <summary>Cohere's <c>/v2/rerank</c> answer: <c>{id, results: [{index, relevance_score}], meta}</c>.</summary>
internal sealed class CohereRerankResponse
{
    [JsonPropertyName("results")]
    public List<CohereRerankResult>? Results { get; set; }
}

/// <summary>One entry of <c>results</c>: the document's position in <c>documents</c> and its score, 0 to 1.</summary>
internal sealed class CohereRerankResult
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("relevance_score")]
    public double RelevanceScore { get; set; }
}

/// <summary>Cohere's error body: <c>{ "id": "…", "message": "…" }</c>.</summary>
internal sealed class CohereErrorResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
