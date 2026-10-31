// -----------------------------------------------------------------------
// <copyright file="ZeroEntropyHttpClient.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using Compendium.Adapters.ZeroEntropy.Configuration;
using Compendium.Adapters.ZeroEntropy.Http.Models;

namespace Compendium.Adapters.ZeroEntropy.Http;

/// <summary>
/// HTTP client for ZeroEntropy's <c>/models/rerank</c>: the Bearer key, ZeroEntropy's error body (<c>{detail}</c>, a
/// string or a list of <c>{loc, msg, type}</c>), and every failure of the exchange as <c>AI.RerankFailed</c>.
/// </summary>
internal sealed class ZeroEntropyHttpClient
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient;
    private readonly ZeroEntropyRerankerOptions _options;
    private readonly ILogger<ZeroEntropyHttpClient> _logger;

    public ZeroEntropyHttpClient(HttpClient httpClient, IOptions<ZeroEntropyRerankerOptions> options, ILogger<ZeroEntropyHttpClient> logger)
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

    /// <summary><c>{BaseUrl}/models/rerank</c>.</summary>
    internal Uri RerankUrl => new($"{_options.BaseUrl.TrimEnd('/')}/models/rerank");

    /// <summary>
    /// Sends one batch. A non-2xx answer, an unreadable body or a transport error is <c>AI.RerankFailed</c>; a cancelled
    /// <paramref name="cancellationToken"/> throws, for the caller to tell its own cancellation from its deadline.
    /// </summary>
    public async Task<Result<IReadOnlyList<ZeroEntropyRerankResult>>> RerankAsync(ZeroEntropyRerankRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            if (_options.EnableLogging)
            {
                _logger.LogDebug("ZeroEntropy rerank request to {Url}: {Request}", RerankUrl, json);
            }

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(RerankUrl, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<IReadOnlyList<ZeroEntropyRerankResult>>(AIErrors.RerankFailed(DescribeError(response, body)));
            }

            var answer = JsonSerializer.Deserialize<ZeroEntropyRerankResponse>(body, JsonOptions);
            return answer?.Results is { } results
                ? Result.Success<IReadOnlyList<ZeroEntropyRerankResult>>(results)
                : Result.Failure<IReadOnlyList<ZeroEntropyRerankResult>>(AIErrors.RerankFailed("ZeroEntropy answered without results"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "ZeroEntropy rerank request timed out");
            return Result.Failure<IReadOnlyList<ZeroEntropyRerankResult>>(AIErrors.RerankFailed("the ZeroEntropy request timed out"));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error talking to ZeroEntropy");
            return Result.Failure<IReadOnlyList<ZeroEntropyRerankResult>>(AIErrors.RerankFailed($"HTTP error talking to ZeroEntropy: {ex.Message}"));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Unreadable ZeroEntropy rerank response");
            return Result.Failure<IReadOnlyList<ZeroEntropyRerankResult>>(AIErrors.RerankFailed("ZeroEntropy answered with an unreadable body"));
        }
    }

    private static string DescribeError(HttpResponseMessage response, string body)
    {
        var status = $"ZeroEntropy answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("detail", out var detail))
            {
                var described = detail.ValueKind switch
                {
                    JsonValueKind.String => detail.GetString(),

                    // A validation error: [{loc, msg, type}], each as "loc.path: msg".
                    JsonValueKind.Array => string.Join("; ", detail.EnumerateArray().Select(Describe)),
                    _ => null,
                };
                if (described is { Length: > 0 })
                {
                    return $"{status}: {described}";
                }
            }
        }
        catch (JsonException)
        {
            // Not ZeroEntropy's error body (a proxy's page, say): the status alone.
        }

        return status;
    }

    private static string Describe(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object)
        {
            return error.ToString();
        }

        var message = error.TryGetProperty("msg", out var msg) ? msg.ToString() : error.ToString();
        return error.TryGetProperty("loc", out var loc) && loc.ValueKind == JsonValueKind.Array
            ? $"{string.Join('.', loc.EnumerateArray().Select(l => l.ToString()))}: {message}"
            : message;
    }
}
