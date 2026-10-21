// -----------------------------------------------------------------------
// <copyright file="OpenAIHttpClient.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Text;
using Compendium.Adapters.OpenAI.Configuration;
using Compendium.Adapters.OpenAI.Http.Models;

namespace Compendium.Adapters.OpenAI.Http;

/// <summary>
/// HTTP client for communicating with the OpenAI REST API.
/// </summary>
internal sealed class OpenAIHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenAIOptions _options;
    private readonly ILogger<OpenAIHttpClient> _logger;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public OpenAIHttpClient(
        HttpClient httpClient,
        IOptions<OpenAIOptions> options,
        ILogger<OpenAIHttpClient> logger)
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

        if (!string.IsNullOrEmpty(_options.ApiKey)
            && !_httpClient.DefaultRequestHeaders.Contains("Authorization"))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_options.ApiKey}");
        }

        if (!string.IsNullOrEmpty(_options.Organization)
            && !_httpClient.DefaultRequestHeaders.Contains("OpenAI-Organization"))
        {
            _httpClient.DefaultRequestHeaders.Add("OpenAI-Organization", _options.Organization);
        }

        if (!string.IsNullOrEmpty(_options.Project)
            && !_httpClient.DefaultRequestHeaders.Contains("OpenAI-Project"))
        {
            _httpClient.DefaultRequestHeaders.Add("OpenAI-Project", _options.Project);
        }
    }

    public async Task<Result<OpenAIChatCompletionResponse>> CreateChatCompletionAsync(
        OpenAIChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("OpenAI request: {Request}", json);
            }

            var response = await _httpClient.PostAsync("chat/completions", content, cancellationToken);
            return await HandleResponseAsync<OpenAIChatCompletionResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "OpenAI chat request timed out");
            return Result.Failure<OpenAIChatCompletionResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with OpenAI");
            return Result.Failure<OpenAIChatCompletionResponse>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    public async IAsyncEnumerable<Result<OpenAIStreamChunk>> StreamChatCompletionAsync(
        OpenAIChatCompletionRequest request,
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
                yield return Result.Failure<OpenAIStreamChunk>(error);
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

                OpenAIStreamChunk? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<OpenAIStreamChunk>(data, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse OpenAI stream chunk: {Data}", data);
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

    public async Task<Result<OpenAIEmbeddingsResponse>> CreateEmbeddingsAsync(
        OpenAIEmbeddingsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("OpenAI embeddings request: {Request}", json);
            }

            var response = await _httpClient.PostAsync("embeddings", content, cancellationToken);
            return await HandleResponseAsync<OpenAIEmbeddingsResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "OpenAI embeddings request timed out");
            return Result.Failure<OpenAIEmbeddingsResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with OpenAI embeddings");
            return Result.Failure<OpenAIEmbeddingsResponse>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    public async Task<Result<List<OpenAIModelInfo>>> ListModelsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync("models", cancellationToken);
            var result = await HandleResponseAsync<OpenAIModelsResponse>(response, cancellationToken);

            return result.Match(
                success => Result.Success(success.Data),
                error => Result.Failure<List<OpenAIModelInfo>>(error));
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing OpenAI models");
            return Result.Failure<List<OpenAIModelInfo>>(
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
            _logger.LogDebug("OpenAI response ({StatusCode}): {Content}", response.StatusCode, content);
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
                _logger.LogError(ex, "Failed to deserialize OpenAI response");
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
            var errorResponse = JsonSerializer.Deserialize<OpenAIErrorResponse>(content, JsonOptions);
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
