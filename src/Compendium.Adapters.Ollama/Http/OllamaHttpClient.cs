// -----------------------------------------------------------------------
// <copyright file="OllamaHttpClient.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Text;
using Compendium.Adapters.Ollama.Configuration;
using Compendium.Adapters.Ollama.Http.Models;

namespace Compendium.Adapters.Ollama.Http;

/// <summary>
/// Typed HTTP client for the Ollama HTTP API.
/// </summary>
/// <remarks>
/// Ollama's streaming protocol is newline-delimited JSON (NOT Server-Sent Events) — each line
/// is a complete <see cref="OllamaChatResponse"/> object. The final line has <c>done=true</c>.
/// </remarks>
internal sealed class OllamaHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaHttpClient> _logger;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Ollama uses snake_case (e.g. total_duration, prompt_eval_count). We control the wire
        // shape with explicit JsonPropertyName attributes, so we don't need a policy here. We
        // do keep null-skipping so optional fields don't leak as nulls.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaHttpClient"/> class.
    /// </summary>
    public OllamaHttpClient(
        HttpClient httpClient,
        IOptions<OllamaOptions> options,
        ILogger<OllamaHttpClient> logger)
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
    }

    /// <summary>
    /// Non-streaming chat request.
    /// </summary>
    public async Task<Result<OllamaChatResponse>> ChatAsync(
        OllamaChatRequest request,
        CancellationToken cancellationToken)
    {
        request.Stream = false;
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("Ollama chat request: {Request}", json);
            }

            var response = await _httpClient.PostAsync("api/chat", content, cancellationToken);
            return await HandleResponseAsync<OllamaChatResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Ollama chat request timed out");
            return Result.Failure<OllamaChatResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with Ollama");
            return Result.Failure<OllamaChatResponse>(
                AIErrors.ProviderUnavailable("ollama"));
        }
    }

    /// <summary>
    /// Streaming chat request — yields one chunk per newline-delimited JSON line.
    /// </summary>
    public async IAsyncEnumerable<Result<OllamaChatResponse>> ChatStreamAsync(
        OllamaChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        request.Stream = true;

        HttpResponseMessage? response = null;
        Stream? stream = null;

        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/chat")
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
                yield return Result.Failure<OllamaChatResponse>(error);
                yield break;
            }

            stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!cancellationToken.IsCancellationRequested
                   && await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                OllamaChatResponse? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse Ollama stream chunk: {Line}", line);
                    continue;
                }

                if (chunk != null)
                {
                    yield return Result.Success(chunk);

                    if (chunk.Done)
                    {
                        yield break;
                    }
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
    /// Batched embeddings via the newer <c>/api/embed</c> endpoint.
    /// </summary>
    public async Task<Result<OllamaEmbedResponse>> EmbedAsync(
        OllamaEmbedRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("Ollama embed request: {Request}", json);
            }

            var response = await _httpClient.PostAsync("api/embed", content, cancellationToken);
            return await HandleResponseAsync<OllamaEmbedResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Ollama embed request timed out");
            return Result.Failure<OllamaEmbedResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with Ollama embed");
            return Result.Failure<OllamaEmbedResponse>(
                AIErrors.ProviderUnavailable("ollama"));
        }
    }

    /// <summary>
    /// Single-prompt embedding via the legacy <c>/api/embeddings</c> endpoint. Used as a fallback
    /// when <c>/api/embed</c> is unavailable.
    /// </summary>
    public async Task<Result<OllamaLegacyEmbeddingsResponse>> EmbedLegacyAsync(
        OllamaLegacyEmbeddingsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("api/embeddings", content, cancellationToken);
            return await HandleResponseAsync<OllamaLegacyEmbeddingsResponse>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Ollama legacy embed request timed out");
            return Result.Failure<OllamaLegacyEmbeddingsResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with Ollama legacy embeddings");
            return Result.Failure<OllamaLegacyEmbeddingsResponse>(
                AIErrors.ProviderUnavailable("ollama"));
        }
    }

    /// <summary>
    /// Pulls a model. The Ollama server streams newline-delimited progress lines; we drain
    /// the stream and surface the final outcome.
    /// </summary>
    public async Task<Result> PullModelAsync(
        string model,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        Stream? stream = null;
        try
        {
            var json = JsonSerializer.Serialize(
                new OllamaPullRequest { Model = model, Stream = true },
                JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/pull")
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
                return Result.Failure(error);
            }

            stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            string? lastStatus = null;
            while (!cancellationToken.IsCancellationRequested
                   && await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                OllamaPullProgress? progress;
                try
                {
                    progress = JsonSerializer.Deserialize<OllamaPullProgress>(line, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse Ollama pull progress: {Line}", line);
                    continue;
                }

                if (progress == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(progress.Error))
                {
                    return Result.Failure(AIErrors.ModelNotFound(progress.Error));
                }

                lastStatus = progress.Status;
                if (_options.EnableLogging)
                {
                    _logger.LogDebug("Ollama pull progress: {Status} ({Completed}/{Total})",
                        progress.Status, progress.Completed, progress.Total);
                }
            }

            // Ollama signals completion with status="success" on its last line.
            if (string.Equals(lastStatus, "success", StringComparison.OrdinalIgnoreCase))
            {
                return Result.Success();
            }

            return Result.Failure(AIErrors.ProviderError(
                $"Ollama pull did not report success (last status: {lastStatus ?? "<none>"})"));
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Ollama pull request timed out");
            return Result.Failure(AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error pulling model from Ollama");
            return Result.Failure(AIErrors.ProviderUnavailable("ollama"));
        }
        finally
        {
            stream?.Dispose();
            response?.Dispose();
        }
    }

    /// <summary>
    /// Lists locally-available models via <c>GET /api/tags</c>.
    /// </summary>
    public async Task<Result<List<OllamaTagModel>>> ListModelsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync("api/tags", cancellationToken);
            var result = await HandleResponseAsync<OllamaTagsResponse>(response, cancellationToken);
            return result.Match(
                success => Result.Success(success.Models),
                error => Result.Failure<List<OllamaTagModel>>(error));
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing Ollama models");
            return Result.Failure<List<OllamaTagModel>>(AIErrors.ProviderUnavailable("ollama"));
        }
    }

    private async Task<Result<T>> HandleResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (_options.EnableLogging)
        {
            _logger.LogDebug("Ollama response ({StatusCode}): {Content}", response.StatusCode, content);
        }

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var result = JsonSerializer.Deserialize<T>(content, JsonOptions);
                return result != null
                    ? Result.Success(result)
                    : Result.Failure<T>(AIErrors.ProviderError("Empty response from Ollama"));
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to deserialize Ollama response");
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
    /// Maps an Ollama HTTP error to a Compendium AI error code. Notably, Ollama returns 404
    /// with a body like <c>{"error":"model 'foo' not found, try pulling it first"}</c>.
    /// </summary>
    internal static Error ParseErrorBody(HttpStatusCode status, string content)
    {
        string? message = null;
        try
        {
            var err = JsonSerializer.Deserialize<OllamaErrorResponse>(content, JsonOptions);
            message = err?.Error;
        }
        catch (JsonException)
        {
            // Fall through.
        }

        message ??= string.IsNullOrWhiteSpace(content) ? status.ToString() : content;

        // Ollama signals "model not found" either with a 404 or a 400 with a
        // discriminator inside the body. Surface both as ModelNotFound so callers
        // can hook into auto-pull recovery.
        var isModelMissing = status == HttpStatusCode.NotFound
            || (message != null
                && (message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("try pulling it first", StringComparison.OrdinalIgnoreCase)));

        return status switch
        {
            HttpStatusCode.Unauthorized => AIErrors.InvalidApiKey(),
            HttpStatusCode.TooManyRequests => AIErrors.RateLimitExceeded(),
            _ when isModelMissing => AIErrors.ModelNotFound(message ?? "model not found"),
            HttpStatusCode.ServiceUnavailable => AIErrors.ProviderUnavailable("ollama"),
            HttpStatusCode.GatewayTimeout => AIErrors.ProviderUnavailable("ollama"),
            _ => AIErrors.ProviderError(message ?? status.ToString()),
        };
    }
}
