// -----------------------------------------------------------------------
// <copyright file="MercuryHttpClient.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Text;
using Compendium.Adapters.Mercury.Configuration;
using Compendium.Adapters.Mercury.Http.Models;

namespace Compendium.Adapters.Mercury.Http;

/// <summary>
/// HTTP transport for the Mercury (Inception Labs) chat-completions API.
/// Hand-rolled <see cref="HttpClient"/> + <c>System.Text.Json</c> — the same
/// pattern used by the OpenAI / OpenRouter adapters.
/// </summary>
internal sealed class MercuryHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly MercuryOptions _options;
    private readonly ILogger<MercuryHttpClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Initialises a new instance of the <see cref="MercuryHttpClient"/> class.
    /// </summary>
    public MercuryHttpClient(
        HttpClient httpClient,
        IOptions<MercuryOptions> options,
        ILogger<MercuryHttpClient> logger)
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
            _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        }

        if (!_httpClient.DefaultRequestHeaders.Contains("Authorization") && !string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_options.ApiKey}");
        }
    }

    /// <summary>
    /// Sends a non-streaming chat-completion request.
    /// </summary>
    public async Task<Result<MercuryCompletionResponse>> CreateCompletionAsync(
        MercuryCompletionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("Mercury request: {Request}", json);
            }

            var response = await _httpClient.PostAsync("/chat/completions", content, cancellationToken);

            return await HandleResponseAsync<MercuryCompletionResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Mercury request timed out");
            return Result.Failure<MercuryCompletionResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with Mercury");
            return Result.Failure<MercuryCompletionResponse>(
                AIErrors.ProviderError(ex.Message));
        }
    }

    /// <summary>
    /// Streams a chat-completion as Server-Sent-Events chunks.
    /// </summary>
    public async IAsyncEnumerable<Result<MercuryStreamChunk>> CreateCompletionStreamAsync(
        MercuryCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        Stream? stream = null;

        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/chat/completions")
            {
                Content = content,
            };

            response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ParseErrorAsync(response, cancellationToken);
                yield return Result.Failure<MercuryStreamChunk>(error);
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

                MercuryStreamChunk? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<MercuryStreamChunk>(data, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse Mercury stream chunk: {Data}", data);
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
    /// Lists models exposed by Mercury.
    /// </summary>
    public async Task<Result<List<MercuryModel>>> ListModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync("/models", cancellationToken);
            var result = await HandleResponseAsync<MercuryModelsResponse>(response, cancellationToken);

            return result.Match(
                success => Result.Success(success.Data),
                error => Result.Failure<List<MercuryModel>>(error));
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing Mercury models");
            return Result.Failure<List<MercuryModel>>(AIErrors.ProviderError(ex.Message));
        }
    }

    private async Task<Result<T>> HandleResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (_options.EnableLogging)
        {
            _logger.LogDebug(
                "Mercury response ({StatusCode}): {Content}",
                response.StatusCode,
                content);
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
                _logger.LogError(ex, "Failed to deserialize Mercury response");
                return Result.Failure<T>(AIErrors.ProviderError("Invalid response format"));
            }
        }

        var error = await ParseErrorAsync(response, cancellationToken);
        return Result.Failure<T>(error);
    }

    private async Task<Error> ParseErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        try
        {
            var errorResponse = JsonSerializer.Deserialize<MercuryErrorResponse>(content, JsonOptions);
            var errorMessage = errorResponse?.Error?.Message ?? content;
            var errorCode = errorResponse?.Error?.Code;

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => AIErrors.InvalidApiKey(),
                HttpStatusCode.TooManyRequests => AIErrors.RateLimitExceeded(),
                HttpStatusCode.PaymentRequired => AIErrors.InsufficientCredits(),
                HttpStatusCode.NotFound => AIErrors.ModelNotFound(errorMessage),
                _ => AIErrors.ProviderError(errorMessage, errorCode),
            };
        }
        catch
        {
            return AIErrors.ProviderError(content);
        }
    }
}
