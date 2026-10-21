// -----------------------------------------------------------------------
// <copyright file="AzureOpenAIHttpClient.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Text;
using Compendium.Adapters.AzureOpenAI.Configuration;
using Compendium.Adapters.AzureOpenAI.Http.Models;

namespace Compendium.Adapters.AzureOpenAI.Http;

/// <summary>
/// HTTP client for communicating with the Azure OpenAI Service REST API.
/// </summary>
/// <remarks>
/// Endpoint shape (chat):
///   <c>{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={api-version}</c>.
/// Auth is configured externally — either via an <see cref="EntraIDAuthHandler"/> registered in
/// the typed HttpClient pipeline, or via the static <c>api-key</c> header stamped on construction.
/// </remarks>
internal sealed class AzureOpenAIHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly AzureOpenAIOptions _options;
    private readonly ILogger<AzureOpenAIHttpClient> _logger;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public AzureOpenAIHttpClient(
        HttpClient httpClient,
        IOptions<AzureOpenAIOptions> options,
        ILogger<AzureOpenAIHttpClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        ConfigureHttpClient();
    }

    private void ConfigureHttpClient()
    {
        if (_httpClient.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            _httpClient.BaseAddress = new Uri(_options.Endpoint.TrimEnd('/') + "/");
        }

        if (_options.AuthMode == AzureOpenAIAuthMode.ApiKey
            && !string.IsNullOrEmpty(_options.ApiKey)
            && !_httpClient.DefaultRequestHeaders.Contains("api-key"))
        {
            _httpClient.DefaultRequestHeaders.Add("api-key", _options.ApiKey);
        }
    }

    public async Task<Result<AzureOpenAIChatCompletionResponse>> CreateChatCompletionAsync(
        string deployment,
        AzureOpenAIChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("Azure OpenAI request: {Request}", json);
            }

            var url = BuildChatUrl(deployment);
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            return await HandleResponseAsync<AzureOpenAIChatCompletionResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Azure OpenAI chat request timed out");
            return Result.Failure<AzureOpenAIChatCompletionResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with Azure OpenAI");
            return Result.Failure<AzureOpenAIChatCompletionResponse>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    public async IAsyncEnumerable<Result<AzureOpenAIStreamChunk>> StreamChatCompletionAsync(
        string deployment,
        AzureOpenAIChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        Stream? stream = null;

        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildChatUrl(deployment))
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
                yield return Result.Failure<AzureOpenAIStreamChunk>(error);
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

                AzureOpenAIStreamChunk? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<AzureOpenAIStreamChunk>(data, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse Azure OpenAI stream chunk: {Data}", data);
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

    public async Task<Result<AzureOpenAIEmbeddingsResponse>> CreateEmbeddingsAsync(
        string deployment,
        AzureOpenAIEmbeddingsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("Azure OpenAI embeddings request: {Request}", json);
            }

            var url = BuildEmbeddingsUrl(deployment);
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            return await HandleResponseAsync<AzureOpenAIEmbeddingsResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Azure OpenAI embeddings request timed out");
            return Result.Failure<AzureOpenAIEmbeddingsResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with Azure OpenAI embeddings");
            return Result.Failure<AzureOpenAIEmbeddingsResponse>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    /// <summary>
    /// Pings the chat endpoint with a minimal payload to verify connectivity and credentials.
    /// </summary>
    public async Task<Result> PingDeploymentAsync(string deployment, CancellationToken cancellationToken)
    {
        try
        {
            var probe = new AzureOpenAIChatCompletionRequest
            {
                Messages = new List<AzureOpenAIChatMessage>
                {
                    new() { Role = "user", Content = "ping" }
                },
                MaxTokens = 1
            };
            var result = await CreateChatCompletionAsync(deployment, probe, cancellationToken);
            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure OpenAI health check failed");
            return Result.Failure(AIErrors.ProviderUnavailable("azure-openai"));
        }
    }

    private string BuildChatUrl(string deployment)
    {
        return $"openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version={Uri.EscapeDataString(_options.ApiVersion)}";
    }

    private string BuildEmbeddingsUrl(string deployment)
    {
        return $"openai/deployments/{Uri.EscapeDataString(deployment)}/embeddings?api-version={Uri.EscapeDataString(_options.ApiVersion)}";
    }

    private async Task<Result<T>> HandleResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (_options.EnableLogging)
        {
            _logger.LogDebug("Azure OpenAI response ({StatusCode}): {Content}", response.StatusCode, content);
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
                _logger.LogError(ex, "Failed to deserialize Azure OpenAI response");
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

    private static Error ParseErrorBody(HttpStatusCode status, string content)
    {
        string? errorMessage = null;
        string? errorCode = null;
        AzureOpenAIContentFilterResults? contentFilter = null;

        try
        {
            var errorResponse = JsonSerializer.Deserialize<AzureOpenAIErrorResponse>(content, JsonOptions);
            errorMessage = errorResponse?.Error?.Message;
            errorCode = errorResponse?.Error?.Code;
            contentFilter = errorResponse?.Error?.InnerError?.ContentFilterResult;
        }
        catch (JsonException)
        {
            // Fall through — we'll surface the raw body.
        }

        // Azure's canonical content-filter signal: error.code == "content_filter" or any inner content-filter
        // category with filtered: true. Both map to AIErrors.ContentFiltered.
        var filteredCategories = contentFilter?.FilteredCategories() ?? new List<string>();
        var isContentFilter =
            string.Equals(errorCode, "content_filter", StringComparison.OrdinalIgnoreCase)
            || filteredCategories.Count > 0;

        if (isContentFilter)
        {
            var reason = filteredCategories.Count > 0
                ? $"Azure content filter blocked categories: {string.Join(", ", filteredCategories)}"
                : errorMessage ?? "Azure content filter blocked the request.";
            return AIErrors.ContentFiltered(reason);
        }

        errorMessage ??= string.IsNullOrWhiteSpace(content) ? status.ToString() : content;

        return status switch
        {
            HttpStatusCode.Unauthorized => AIErrors.InvalidApiKey(),
            HttpStatusCode.Forbidden => AIErrors.InvalidApiKey(),
            HttpStatusCode.TooManyRequests => AIErrors.RateLimitExceeded(),
            HttpStatusCode.PaymentRequired => AIErrors.InsufficientCredits(),
            HttpStatusCode.NotFound => AIErrors.ModelNotFound(errorMessage),
            _ => AIErrors.ProviderError(errorMessage, errorCode)
        };
    }
}
