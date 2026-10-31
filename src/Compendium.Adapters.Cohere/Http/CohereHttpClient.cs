// -----------------------------------------------------------------------
// <copyright file="CohereHttpClient.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using Compendium.Adapters.Cohere.Configuration;
using Compendium.Adapters.Cohere.Http.Models;

namespace Compendium.Adapters.Cohere.Http;

/// <summary>
/// HTTP client for Cohere's <c>/v2/rerank</c>: the Bearer key, Cohere's error body (<c>{id, message}</c>), and every
/// failure of the exchange as <c>AI.RerankFailed</c>.
/// </summary>
internal sealed class CohereHttpClient
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient;
    private readonly CohereRerankerOptions _options;
    private readonly ILogger<CohereHttpClient> _logger;

    public CohereHttpClient(HttpClient httpClient, IOptions<CohereRerankerOptions> options, ILogger<CohereHttpClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        // Without validation: a malformed key fails the request, not the constructor with the key in its message.
        if (!string.IsNullOrWhiteSpace(_options.ApiKey) && !_httpClient.DefaultRequestHeaders.Contains("Authorization"))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey.Trim()}");
        }
    }

    /// <summary><c>{BaseUrl}/v2/rerank</c>.</summary>
    internal Uri RerankUrl => new($"{_options.BaseUrl.TrimEnd('/')}/v2/rerank");

    /// <summary>
    /// Sends one batch. A non-2xx answer, an unreadable body or a transport error is <c>AI.RerankFailed</c>; a cancelled
    /// <paramref name="cancellationToken"/> throws, for the caller to tell its own cancellation from its deadline.
    /// </summary>
    public async Task<Result<IReadOnlyList<CohereRerankResult>>> RerankAsync(CohereRerankRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            if (_options.EnableLogging)
            {
                _logger.LogDebug("Cohere rerank request to {Url}: {Request}", RerankUrl, json);
            }

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(RerankUrl, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<IReadOnlyList<CohereRerankResult>>(AIErrors.RerankFailed(DescribeError(response, body)));
            }

            var answer = JsonSerializer.Deserialize<CohereRerankResponse>(body, JsonOptions);
            return answer?.Results is { } results
                ? Result.Success<IReadOnlyList<CohereRerankResult>>(results)
                : Result.Failure<IReadOnlyList<CohereRerankResult>>(AIErrors.RerankFailed("Cohere answered without results"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Cohere rerank request timed out");
            return Result.Failure<IReadOnlyList<CohereRerankResult>>(AIErrors.RerankFailed("the Cohere request timed out"));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error talking to Cohere");
            return Result.Failure<IReadOnlyList<CohereRerankResult>>(AIErrors.RerankFailed($"HTTP error talking to Cohere: {ex.Message}"));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Unreadable Cohere rerank response");
            return Result.Failure<IReadOnlyList<CohereRerankResult>>(AIErrors.RerankFailed("Cohere answered with an unreadable body"));
        }
    }

    private static string DescribeError(HttpResponseMessage response, string body)
    {
        var status = $"Cohere answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
        try
        {
            if (JsonSerializer.Deserialize<CohereErrorResponse>(body, JsonOptions) is { Message: { Length: > 0 } message })
            {
                return $"{status}: {message}";
            }
        }
        catch (JsonException)
        {
            // Not Cohere's error body (a proxy's page, say): the status alone.
        }

        return status;
    }
}
