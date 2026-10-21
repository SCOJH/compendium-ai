// -----------------------------------------------------------------------
// <copyright file="LiteLLMHttpClient.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Text;
using Compendium.Adapters.LiteLLM.Configuration;
using Compendium.Adapters.LiteLLM.Http.Models;

namespace Compendium.Adapters.LiteLLM.Http;

/// <summary>
/// HTTP client for communicating with a LiteLLM proxy over its OpenAI-compatible REST surface.
/// </summary>
internal sealed class LiteLLMHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly LiteLLMOptions _options;
    private readonly ILogger<LiteLLMHttpClient> _logger;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="LiteLLMHttpClient"/> class.
    /// </summary>
    public LiteLLMHttpClient(
        HttpClient httpClient,
        IOptions<LiteLLMOptions> options,
        ILogger<LiteLLMHttpClient> logger)
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
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }

        var token = _options.GetEffectiveAuthorizationToken();
        if (!string.IsNullOrEmpty(token)
            && !_httpClient.DefaultRequestHeaders.Contains("Authorization"))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        }

        // Configured pass-through headers (e.g. x-litellm-cache-key, x-litellm-tags) are forwarded
        // on every outbound request. Skip headers HttpClient owns (Authorization is set above).
        foreach (var (name, value) in _options.PassThroughHeaders)
        {
            if (string.IsNullOrWhiteSpace(name) || value is null)
            {
                continue;
            }
            if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (_httpClient.DefaultRequestHeaders.Contains(name))
            {
                continue;
            }
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>
    /// Sends a non-streaming chat completion.
    /// </summary>
    public async Task<Result<LiteLLMChatCompletionResponse>> CreateChatCompletionAsync(
        LiteLLMChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("LiteLLM request: {Request}", json);
            }

            var response = await _httpClient.PostAsync("chat/completions", content, cancellationToken);
            return await HandleResponseAsync<LiteLLMChatCompletionResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "LiteLLM chat request timed out");
            return Result.Failure<LiteLLMChatCompletionResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with LiteLLM");
            return Result.Failure<LiteLLMChatCompletionResponse>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    /// <summary>
    /// Sends a streaming chat completion and yields parsed SSE chunks.
    /// </summary>
    public async IAsyncEnumerable<Result<LiteLLMStreamChunk>> StreamChatCompletionAsync(
        LiteLLMChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        Stream? stream = null;

        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
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
                yield return Result.Failure<LiteLLMStreamChunk>(error);
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

                LiteLLMStreamChunk? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<LiteLLMStreamChunk>(data, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse LiteLLM stream chunk: {Data}", data);
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
    /// Sends an embeddings request.
    /// </summary>
    public async Task<Result<LiteLLMEmbeddingsResponse>> CreateEmbeddingsAsync(
        LiteLLMEmbeddingsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("LiteLLM embeddings request: {Request}", json);
            }

            var response = await _httpClient.PostAsync("embeddings", content, cancellationToken);
            return await HandleResponseAsync<LiteLLMEmbeddingsResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "LiteLLM embeddings request timed out");
            return Result.Failure<LiteLLMEmbeddingsResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with LiteLLM embeddings");
            return Result.Failure<LiteLLMEmbeddingsResponse>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    /// <summary>
    /// Lists available models from the proxy.
    /// </summary>
    public async Task<Result<List<LiteLLMModelInfo>>> ListModelsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync("models", cancellationToken);
            var result = await HandleResponseAsync<LiteLLMModelsResponse>(response, cancellationToken);

            return result.Match(
                success => Result.Success(success.Data),
                error => Result.Failure<List<LiteLLMModelInfo>>(error));
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing LiteLLM models");
            return Result.Failure<List<LiteLLMModelInfo>>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    private async Task<Result<T>> HandleResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (_options.EnableLogging)
        {
            _logger.LogDebug("LiteLLM response ({StatusCode}): {Content}", response.StatusCode, content);
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
                _logger.LogError(ex, "Failed to deserialize LiteLLM response");
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
            var errorResponse = JsonSerializer.Deserialize<LiteLLMErrorResponse>(content, JsonOptions);
            errorMessage = errorResponse?.Error?.Message;
            errorCode = errorResponse?.Error?.Code;
        }
        catch (JsonException)
        {
            // Fall through — we'll surface the raw body.
        }

        errorMessage ??= string.IsNullOrWhiteSpace(content) ? status.ToString() : content;

        var error = status switch
        {
            HttpStatusCode.Unauthorized => AIErrors.InvalidApiKey(),
            HttpStatusCode.TooManyRequests => AIErrors.RateLimitExceeded(),
            HttpStatusCode.PaymentRequired => AIErrors.InsufficientCredits(),
            HttpStatusCode.NotFound => AIErrors.ModelNotFound(errorMessage),
            _ => AIErrors.ProviderError(errorMessage, errorCode)
        };
        return Task.FromResult(error);
    }
}
