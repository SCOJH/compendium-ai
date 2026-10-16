// -----------------------------------------------------------------------
// <copyright file="HuggingFaceHttpClient.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Text;
using Compendium.Adapters.HuggingFace.Configuration;
using Compendium.Adapters.HuggingFace.Http.Models;

namespace Compendium.Adapters.HuggingFace.Http;

/// <summary>
/// HTTP client for communicating with Hugging Face inference endpoints. Handles URL resolution
/// for both <see cref="HuggingFaceMode.InferenceEndpoint"/> (per-tenant URL) and
/// <see cref="HuggingFaceMode.ServerlessInference"/> (model id in path), Bearer auth, and the
/// two error-body shapes Hugging Face returns.
/// </summary>
internal sealed class HuggingFaceHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly HuggingFaceOptions _options;
    private readonly ILogger<HuggingFaceHttpClient> _logger;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public HuggingFaceHttpClient(
        HttpClient httpClient,
        IOptions<HuggingFaceOptions> options,
        ILogger<HuggingFaceHttpClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        ConfigureHttpClient();
    }

    private void ConfigureHttpClient()
    {
        if (!string.IsNullOrEmpty(_options.HfToken)
            && !_httpClient.DefaultRequestHeaders.Contains("Authorization"))
        {
            _httpClient.DefaultRequestHeaders.Add(
                "Authorization",
                $"Bearer {_options.HfToken}");
        }
    }

    /// <summary>
    /// Resolves the absolute URL for chat completions for the configured mode and the request's
    /// model id.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description><see cref="HuggingFaceMode.InferenceEndpoint"/>: <c>{EndpointUrl}/v1/chat/completions</c>.</description></item>
    ///   <item><description><see cref="HuggingFaceMode.ServerlessInference"/>: <c>{ServerlessBaseUrl}/{model}/v1/chat/completions</c>.</description></item>
    /// </list>
    /// </remarks>
    internal Uri ResolveChatUrl(string model)
    {
        return _options.Mode switch
        {
            HuggingFaceMode.InferenceEndpoint => new Uri(
                $"{NormalizeBase(RequireEndpointUrl())}/v1/chat/completions"),
            HuggingFaceMode.ServerlessInference => new Uri(
                $"{NormalizeBase(_options.ServerlessBaseUrl)}/{model.TrimStart('/')}/v1/chat/completions"),
            _ => throw new InvalidOperationException($"Unsupported HuggingFaceMode: {_options.Mode}")
        };
    }

    /// <summary>
    /// Resolves the absolute URL for the feature-extraction (embeddings) task. The HF feature-extraction
    /// task is invoked by <c>POST</c>ing to the bare model URL (no <c>/v1</c> suffix).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description><see cref="HuggingFaceMode.InferenceEndpoint"/>: <c>{EndpointUrl}</c>.</description></item>
    ///   <item><description><see cref="HuggingFaceMode.ServerlessInference"/>: <c>{ServerlessBaseUrl}/{model}</c>.</description></item>
    /// </list>
    /// </remarks>
    internal Uri ResolveEmbeddingUrl(string model)
    {
        return _options.Mode switch
        {
            HuggingFaceMode.InferenceEndpoint => new Uri(NormalizeBase(RequireEndpointUrl())),
            HuggingFaceMode.ServerlessInference => new Uri(
                $"{NormalizeBase(_options.ServerlessBaseUrl)}/{model.TrimStart('/')}"),
            _ => throw new InvalidOperationException($"Unsupported HuggingFaceMode: {_options.Mode}")
        };
    }

    private string RequireEndpointUrl()
    {
        if (string.IsNullOrWhiteSpace(_options.EndpointUrl))
        {
            throw new InvalidOperationException(
                "HuggingFaceOptions.EndpointUrl is required when Mode is InferenceEndpoint.");
        }
        return _options.EndpointUrl;
    }

    private static string NormalizeBase(string url) => url.TrimEnd('/');

    public async Task<Result<HuggingFaceChatCompletionResponse>> CreateChatCompletionAsync(
        HuggingFaceChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = ResolveChatUrl(request.Model);
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("HuggingFace chat request to {Url}: {Request}", url, json);
            }

            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            return await HandleResponseAsync<HuggingFaceChatCompletionResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "HuggingFace chat request timed out");
            return Result.Failure<HuggingFaceChatCompletionResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error talking to Hugging Face");
            return Result.Failure<HuggingFaceChatCompletionResponse>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    public async IAsyncEnumerable<Result<HuggingFaceStreamChunk>> StreamChatCompletionAsync(
        HuggingFaceChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        Stream? stream = null;

        try
        {
            var url = ResolveChatUrl(request.Model);
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
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
                yield return Result.Failure<HuggingFaceStreamChunk>(error);
                yield break;
            }

            stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!cancellationToken.IsCancellationRequested
                   && await reader.ReadLineAsync(cancellationToken) is { } line)
            {
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

                HuggingFaceStreamChunk? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<HuggingFaceStreamChunk>(data, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse HuggingFace stream chunk: {Data}", data);
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

    /// <summary>
    /// Calls the feature-extraction task and returns the raw response body. Caller is responsible
    /// for shape detection (a single vector <c>number[]</c> versus a batch <c>number[][]</c>).
    /// </summary>
    public async Task<Result<string>> CreateFeatureExtractionAsync(
        string model,
        HuggingFaceFeatureExtractionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = ResolveEmbeddingUrl(model);
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("HuggingFace embedding request to {Url}: {Request}", url, json);
            }

            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (_options.EnableLogging)
            {
                _logger.LogDebug("HuggingFace embedding response ({StatusCode}): {Content}", response.StatusCode, body);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = ParseErrorBody(response.StatusCode, body);
                return Result.Failure<string>(error);
            }

            return Result.Success(body);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "HuggingFace embedding request timed out");
            return Result.Failure<string>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error talking to Hugging Face for embeddings");
            return Result.Failure<string>(AIErrors.ProviderError(ex.Message));
        }
    }

    private async Task<Result<T>> HandleResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (_options.EnableLogging)
        {
            _logger.LogDebug("HuggingFace response ({StatusCode}): {Content}", response.StatusCode, content);
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
                _logger.LogError(ex, "Failed to deserialize HuggingFace response");
                return Result.Failure<T>(AIErrors.ProviderError("Invalid response format"));
            }
        }

        var err = ParseErrorBody(response.StatusCode, content);
        return Result.Failure<T>(err);
    }

    private async Task<Error> ParseErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseErrorBody(response.StatusCode, content);
    }

    /// <summary>
    /// Parses a Hugging Face error body. HF returns errors in two shapes:
    /// <list type="bullet">
    ///   <item><description><c>{ "error": "string message" }</c></description></item>
    ///   <item><description><c>{ "error": { "message": "…" } }</c></description></item>
    /// </list>
    /// Both are handled; raw bodies fall through to <see cref="AIErrors.ProviderError(string,string?)"/>.
    /// </summary>
    private static Error ParseErrorBody(HttpStatusCode status, string content)
    {
        string? errorMessage = null;
        string? errorCode = null;

        try
        {
            var parsed = JsonSerializer.Deserialize<HuggingFaceErrorResponse>(content, JsonOptions);
            var raw = parsed?.ErrorRaw;
            if (raw.HasValue)
            {
                switch (raw.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        errorMessage = raw.Value.GetString();
                        break;
                    case JsonValueKind.Object:
                        if (raw.Value.TryGetProperty("message", out var msgEl)
                            && msgEl.ValueKind == JsonValueKind.String)
                        {
                            errorMessage = msgEl.GetString();
                        }
                        if (raw.Value.TryGetProperty("code", out var codeEl)
                            && codeEl.ValueKind == JsonValueKind.String)
                        {
                            errorCode = codeEl.GetString();
                        }
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Fall through — raw body is best we can do.
        }

        errorMessage ??= string.IsNullOrWhiteSpace(content) ? status.ToString() : content;

        return status switch
        {
            HttpStatusCode.Unauthorized => AIErrors.InvalidApiKey(),
            HttpStatusCode.TooManyRequests => AIErrors.RateLimitExceeded(),
            HttpStatusCode.PaymentRequired => AIErrors.InsufficientCredits(),
            HttpStatusCode.NotFound => AIErrors.ModelNotFound(errorMessage),
            _ => AIErrors.ProviderError(errorMessage, errorCode)
        };
    }
}
