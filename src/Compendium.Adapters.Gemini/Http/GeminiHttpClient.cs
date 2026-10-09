// -----------------------------------------------------------------------
// <copyright file="GeminiHttpClient.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Text;
using Compendium.Adapters.Gemini.Configuration;
using Compendium.Adapters.Gemini.Http.Models;

namespace Compendium.Adapters.Gemini.Http;

/// <summary>
/// HTTP client for communicating with the Gemini REST API.
/// Authenticates via <c>?key=&lt;ApiKey&gt;</c>. Mirrors the OpenAI adapter's structure
/// (typed <c>HttpClient</c> + <c>System.Text.Json</c> + standard resilience pipeline) so callers
/// get the same retry/timeout/Polly behaviour as the rest of the Compendium AI adapters.
/// </summary>
internal sealed class GeminiHttpClient
{
    /// <summary>The page size asked of <c>models</c>: the API's maximum (its default is 50).</summary>
    internal const int ModelsPageSize = 1000;

    /// <summary>A guard on the <c>models</c> pagination, never reached by a sane answer.</summary>
    internal const int MaxModelPages = 50;

    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiHttpClient> _logger;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public GeminiHttpClient(
        HttpClient httpClient,
        IOptions<GeminiOptions> options,
        ILogger<GeminiHttpClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        ConfigureHttpClient();
    }

    private void ConfigureHttpClient()
    {
        if (_httpClient.BaseAddress is null)
        {
            var baseUrl = _options.BaseUrl.TrimEnd('/');
            _httpClient.BaseAddress = new Uri(baseUrl + "/");
        }
    }

    public async Task<Result<GeminiGenerateContentResponse>> GenerateContentAsync(
        string model,
        GeminiGenerateContentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("Gemini request: {Request}", json);
            }

            var path = BuildModelPath(model, "generateContent");
            var response = await _httpClient.PostAsync(path, content, cancellationToken);
            return await HandleResponseAsync<GeminiGenerateContentResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Gemini chat request timed out");
            return Result.Failure<GeminiGenerateContentResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with Gemini");
            return Result.Failure<GeminiGenerateContentResponse>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    public async IAsyncEnumerable<Result<GeminiGenerateContentResponse>> StreamGenerateContentAsync(
        string model,
        GeminiGenerateContentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        Stream? stream = null;

        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            // ?alt=sse is required to get true Server-Sent-Events framing (line-prefixed
            // "data: " blocks terminated by blank lines); without it Gemini returns a
            // JSON array of chunks which is harder to parse incrementally.
            var path = BuildModelPath(model, "streamGenerateContent", extraQuery: "alt=sse");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = content
            };

            response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ParseErrorAsync(response, cancellationToken);
                yield return Result.Failure<GeminiGenerateContentResponse>(error);
                yield break;
            }

            stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);

                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line[6..];
                if (data == "[DONE]")
                {
                    yield break;
                }

                GeminiGenerateContentResponse? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<GeminiGenerateContentResponse>(data, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse Gemini stream chunk: {Data}", data);
                    continue;
                }

                if (chunk != null)
                {
                    yield return Result.Success(chunk);
                }
            }
        }
        finally
        {
            stream?.Dispose();
            response?.Dispose();
        }
    }

    public async Task<Result<GeminiEmbedContentResponse>> EmbedContentAsync(
        string model,
        GeminiEmbedContentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("Gemini embedContent request: {Request}", json);
            }

            var path = BuildModelPath(model, "embedContent");
            var response = await _httpClient.PostAsync(path, content, cancellationToken);
            return await HandleResponseAsync<GeminiEmbedContentResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Gemini embedContent request timed out");
            return Result.Failure<GeminiEmbedContentResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with Gemini embedContent");
            return Result.Failure<GeminiEmbedContentResponse>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    public async Task<Result<GeminiBatchEmbedContentsResponse>> BatchEmbedContentsAsync(
        string model,
        GeminiBatchEmbedContentsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("Gemini batchEmbedContents request: {Request}", json);
            }

            var path = BuildModelPath(model, "batchEmbedContents");
            var response = await _httpClient.PostAsync(path, content, cancellationToken);
            return await HandleResponseAsync<GeminiBatchEmbedContentsResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Gemini batchEmbedContents request timed out");
            return Result.Failure<GeminiBatchEmbedContentsResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with Gemini batchEmbedContents");
            return Result.Failure<GeminiBatchEmbedContentsResponse>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    /// <summary>
    /// GETs every page of <c>models</c> (<c>pageSize</c> = <see cref="ModelsPageSize"/>, then <c>pageToken</c> = the
    /// previous page's <c>nextPageToken</c>) until the token is empty: by default Google returns 50 models per page.
    /// A repeated token, or more than <see cref="MaxModelPages"/> pages, is a provider error rather than a loop.
    /// </summary>
    public async Task<Result<List<GeminiModelInfo>>> ListModelsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var models = new List<GeminiModelInfo>();
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            string? token = null;

            for (var page = 1; page <= MaxModelPages; page++)
            {
                var path = $"{_options.ApiVersion}/models?pageSize={ModelsPageSize}&{KeyQuery()}";
                if (token is not null)
                {
                    path += $"&pageToken={Uri.EscapeDataString(token)}";
                }

                var response = await _httpClient.GetAsync(path, cancellationToken);
                var result = await HandleResponseAsync<GeminiModelsResponse>(response, cancellationToken);
                if (result.IsFailure)
                {
                    return Result.Failure<List<GeminiModelInfo>>(result.Error);
                }

                models.AddRange(result.Value.Models);

                if (string.IsNullOrEmpty(result.Value.NextPageToken))
                {
                    return Result.Success(models);
                }

                if (!seenTokens.Add(result.Value.NextPageToken))
                {
                    return PaginationStalled();
                }

                token = result.Value.NextPageToken;
            }

            return PaginationStalled();
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing Gemini models");
            return Result.Failure<List<GeminiModelInfo>>(AIErrors.ProviderError(ex.Message));
        }
    }

    private static Result<List<GeminiModelInfo>> PaginationStalled() =>
        Result.Failure<List<GeminiModelInfo>>(
            AIErrors.ProviderError("Gemini models pagination did not advance"));

    private string BuildModelPath(string model, string action, string? extraQuery = null)
    {
        // Gemini accepts both "gemini-2.0-flash" and "models/gemini-2.0-flash" as identifiers;
        // strip the prefix so the path stays consistent regardless of caller convention.
        var trimmed = model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? model["models/".Length..]
            : model;

        var path = $"{_options.ApiVersion}/models/{trimmed}:{action}?{KeyQuery()}";
        return extraQuery is null ? path : $"{path}&{extraQuery}";
    }

    private string KeyQuery() => string.IsNullOrEmpty(_options.ApiKey) ? "key=" : $"key={Uri.EscapeDataString(_options.ApiKey)}";

    private async Task<Result<T>> HandleResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (_options.EnableLogging)
        {
            _logger.LogDebug("Gemini response ({StatusCode}): {Content}", response.StatusCode, content);
        }

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var result = JsonSerializer.Deserialize<T>(content, JsonOptions);
                return result != null
                    ? Result.Success(result)
                    : Result.Failure<T>(AIErrors.ProviderError("Empty response from provider"));
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to deserialize Gemini response");
                return Result.Failure<T>(AIErrors.ProviderError("Invalid response format"));
            }
        }

        var err = await ParseErrorBodyAsync(response.StatusCode, content);
        return Result.Failure<T>(err);
    }

    private async Task<Error> ParseErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return await ParseErrorBodyAsync(response.StatusCode, content);
    }

    private Task<Error> ParseErrorBodyAsync(HttpStatusCode status, string content)
    {
        string? errorMessage = null;
        string? errorCode = null;

        try
        {
            var errorResponse = JsonSerializer.Deserialize<GeminiErrorResponse>(content, JsonOptions);
            errorMessage = errorResponse?.Error?.Message;
            errorCode = errorResponse?.Error?.Status;
        }
        catch (JsonException)
        {
            // Fall through — we'll surface the raw body.
        }

        errorMessage ??= string.IsNullOrWhiteSpace(content) ? status.ToString() : content;

        var error = status switch
        {
            HttpStatusCode.Unauthorized => AIErrors.InvalidApiKey(),
            HttpStatusCode.Forbidden => AIErrors.InvalidApiKey(),
            HttpStatusCode.TooManyRequests => AIErrors.RateLimitExceeded(),
            HttpStatusCode.PaymentRequired => AIErrors.InsufficientCredits(),
            HttpStatusCode.NotFound => AIErrors.ModelNotFound(errorMessage),
            _ => AIErrors.ProviderError(errorMessage, errorCode)
        };
        return Task.FromResult(error);
    }
}
