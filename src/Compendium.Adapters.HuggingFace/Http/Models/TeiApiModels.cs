// -----------------------------------------------------------------------
// <copyright file="TeiApiModels.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.HuggingFace.Configuration;

namespace Compendium.Adapters.HuggingFace.Http.Models;

/// <summary>
/// TEI <c>POST /rerank</c> request. <c>raw_scores</c> stays false (scores through a sigmoid, 0 to 1) and
/// <c>return_text</c> false: the reranker has the texts already.
/// </summary>
internal sealed class TeiRerankRequest
{
    [JsonPropertyName("query")]
    public required string Query { get; set; }

    [JsonPropertyName("texts")]
    public required IReadOnlyList<string> Texts { get; set; }

    [JsonPropertyName("raw_scores")]
    public bool RawScores { get; set; }

    [JsonPropertyName("return_text")]
    public bool ReturnText { get; set; }

    [JsonPropertyName("truncate")]
    public bool Truncate { get; set; }

    [JsonPropertyName("truncation_direction")]
    public TeiTruncationDirection TruncationDirection { get; set; }
}

/// <summary>One entry of TEI's <c>/rerank</c> answer: the text's position in <c>texts</c> and its score.</summary>
internal sealed class TeiRank
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }
}

/// <summary>TEI's error body: <c>{ "error": "…", "error_type": "Validation" }</c>.</summary>
internal sealed class TeiErrorResponse
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_type")]
    public string? ErrorType { get; set; }
}
