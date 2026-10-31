// -----------------------------------------------------------------------
// <copyright file="TeiHttpClient.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using Compendium.Adapters.HuggingFace.Configuration;
using Compendium.Adapters.HuggingFace.Http.Models;

namespace Compendium.Adapters.HuggingFace.Http;

/// <summary>
/// HTTP client for a Text Embeddings Inference server's <c>/rerank</c> route: the optional Bearer key, TEI's error body
/// (<c>{error, error_type}</c>), and every failure of the exchange as <c>AI.RerankFailed</c>.
/// </summary>
internal sealed class TeiHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly TeiRerankerOptions _options;
    private readonly ILogger<TeiHttpClient> _logger;

    public TeiHttpClient(HttpClient httpClient, IOptions<TeiRerankerOptions> options, ILogger<TeiHttpClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        if (!string.IsNullOrEmpty(_options.ApiKey) && !_httpClient.DefaultRequestHeaders.Contains("Authorization"))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_options.ApiKey}");
        }
    }

    /// <summary><c>{BaseUrl}/rerank</c>.</summary>
    internal Uri RerankUrl => new($"{_options.BaseUrl.TrimEnd('/')}/rerank");

    /// <summary>
    /// Sends one batch. A non-2xx answer, an unreadable body or a transport error is <c>AI.RerankFailed</c>; a cancelled
    /// <paramref name="cancellationToken"/> throws, for the caller to tell its own cancellation from its deadline.
    /// </summary>
    public async Task<Result<IReadOnlyList<TeiRank>>> RerankAsync(TeiRerankRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, HuggingFaceHttpClient.JsonOptions);
            if (_options.EnableLogging)
            {
                _logger.LogDebug("TEI rerank request to {Url}: {Request}", RerankUrl, json);
            }

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(RerankUrl, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<IReadOnlyList<TeiRank>>(AIErrors.RerankFailed(DescribeError(response, body)));
            }

            var ranks = JsonSerializer.Deserialize<List<TeiRank>>(body, HuggingFaceHttpClient.JsonOptions);
            return ranks is null
                ? Result.Failure<IReadOnlyList<TeiRank>>(AIErrors.RerankFailed("TEI answered with an empty body"))
                : Result.Success<IReadOnlyList<TeiRank>>(ranks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "TEI rerank request timed out");
            return Result.Failure<IReadOnlyList<TeiRank>>(AIErrors.RerankFailed("the TEI request timed out"));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error talking to TEI");
            return Result.Failure<IReadOnlyList<TeiRank>>(AIErrors.RerankFailed($"HTTP error talking to TEI: {ex.Message}"));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Unreadable TEI rerank response");
            return Result.Failure<IReadOnlyList<TeiRank>>(AIErrors.RerankFailed("TEI answered with an unreadable body"));
        }
    }

    private static string DescribeError(HttpResponseMessage response, string body)
    {
        var status = $"TEI answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
        try
        {
            if (JsonSerializer.Deserialize<TeiErrorResponse>(body, HuggingFaceHttpClient.JsonOptions) is { Error: { Length: > 0 } error } parsed)
            {
                return parsed.ErrorType is { Length: > 0 } type ? $"{status} ({type}): {error}" : $"{status}: {error}";
            }
        }
        catch (JsonException)
        {
            // Not TEI's error body (a proxy's page, say): the status alone.
        }

        return status;
    }
}
