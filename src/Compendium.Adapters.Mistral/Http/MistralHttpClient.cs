// -----------------------------------------------------------------------
// <copyright file="MistralHttpClient.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Text;
using Compendium.Adapters.Mistral.Configuration;
using Compendium.Adapters.Mistral.Http.Models;

namespace Compendium.Adapters.Mistral.Http;

/// <summary>
/// HTTP client for communicating with the Mistral REST API ("la Plateforme").
/// </summary>
internal sealed class MistralHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly MistralOptions _options;
    private readonly ILogger<MistralHttpClient> _logger;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public MistralHttpClient(
        HttpClient httpClient,
        IOptions<MistralOptions> options,
        ILogger<MistralHttpClient> logger)
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
            // Mistral exposes its REST surface under /v1/. We tack the /v1/ on here so the per-call
            // path can stay short ("chat/completions", "embeddings", …) and we don't accidentally
            // collide with a base URL the user already pinned (e.g. a regional proxy).
            var baseUrl = _options.BaseUrl.TrimEnd('/');
            if (!baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl += "/v1";
            }
            _httpClient.BaseAddress = new Uri(baseUrl + "/");
        }

        if (!string.IsNullOrEmpty(_options.ApiKey)
            && !_httpClient.DefaultRequestHeaders.Contains("Authorization"))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_options.ApiKey}");
        }
    }

    public async Task<Result<MistralChatCompletionResponse>> CreateChatCompletionAsync(
        MistralChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("Mistral request: {Request}", json);
            }

            var response = await _httpClient.PostAsync("chat/completions", content, cancellationToken);
            return await HandleResponseAsync<MistralChatCompletionResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Mistral chat request timed out");
            return Result.Failure<MistralChatCompletionResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with Mistral");
            return Result.Failure<MistralChatCompletionResponse>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    /// <summary>
    /// The stream's chunks. <paramref name="onDone"/> is called when its <c>[DONE]</c> is read: a stream that ends
    /// without it was cut.
    /// </summary>
    public async IAsyncEnumerable<Result<MistralStreamChunk>> StreamChatCompletionAsync(
        MistralChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Action? onDone = null)
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
                yield return Result.Failure<MistralStreamChunk>(error);
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
                    onDone?.Invoke();
                    yield break;
                }

                MistralStreamChunk? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<MistralStreamChunk>(data, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse Mistral stream chunk: {Data}", data);
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

    public async Task<Result<MistralEmbeddingsResponse>> CreateEmbeddingsAsync(
        MistralEmbeddingsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("Mistral embeddings request: {Request}", json);
            }

            var response = await _httpClient.PostAsync("embeddings", content, cancellationToken);
            return await HandleResponseAsync<MistralEmbeddingsResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Mistral embeddings request timed out");
            return Result.Failure<MistralEmbeddingsResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with Mistral embeddings");
            return Result.Failure<MistralEmbeddingsResponse>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    public async Task<Result<List<MistralModelInfo>>> ListModelsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync("models", cancellationToken);
            var result = await HandleResponseAsync<MistralModelsResponse>(response, cancellationToken);

            return result.Match(
                success => Result.Success(success.Data),
                error => Result.Failure<List<MistralModelInfo>>(error));
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing Mistral models");
            return Result.Failure<List<MistralModelInfo>>(
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
            _logger.LogDebug("Mistral response ({StatusCode}): {Content}", response.StatusCode, content);
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
                _logger.LogError(ex, "Failed to deserialize Mistral response");
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
        var (errorMessage, errorCode) = TryParseMistralError(content);
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

    /// <summary>
    /// Mistral returns two distinct error shapes — the OpenAI-compat envelope <c>{ error: { message, code } }</c>
    /// and a native shape with a top-level <c>message</c> that may itself be a string or an object with a <c>detail</c>
    /// field. This method tries both shapes and returns whichever produces a usable message.
    /// </summary>
    private static (string? Message, string? Code) TryParseMistralError(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return (null, null);
        }

        try
        {
            var errorResponse = JsonSerializer.Deserialize<MistralErrorResponse>(content, JsonOptions);
            if (errorResponse == null)
            {
                return (null, null);
            }

            // OpenAI-compat shape wins when present.
            if (errorResponse.Error != null && !string.IsNullOrEmpty(errorResponse.Error.Message))
            {
                return (errorResponse.Error.Message, errorResponse.Error.Code);
            }

            // Native shape — top-level message may be a string or { "detail": "..." }.
            if (errorResponse.Message.HasValue)
            {
                var msg = errorResponse.Message.Value;
                switch (msg.ValueKind)
                {
                    case JsonValueKind.String:
                        return (msg.GetString(), errorResponse.Code);
                    case JsonValueKind.Object:
                        if (msg.TryGetProperty("detail", out var detail)
                            && detail.ValueKind == JsonValueKind.String)
                        {
                            return (detail.GetString(), errorResponse.Code);
                        }
                        return (msg.GetRawText(), errorResponse.Code);
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to caller's fallback.
        }

        return (null, null);
    }
}
