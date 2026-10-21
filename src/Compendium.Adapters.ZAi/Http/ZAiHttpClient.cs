// -----------------------------------------------------------------------
// <copyright file="ZAiHttpClient.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Text;
using Compendium.Adapters.ZAi.Configuration;
using Compendium.Adapters.ZAi.Http.Models;

namespace Compendium.Adapters.ZAi.Http;

/// <summary>
/// HTTP client for Z.ai's OpenAI-compatible chat completions API.
/// </summary>
internal sealed class ZAiHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly ZAiOptions _options;
    private readonly ILogger<ZAiHttpClient> _logger;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public ZAiHttpClient(
        HttpClient httpClient,
        IOptions<ZAiOptions> options,
        ILogger<ZAiHttpClient> logger)
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
    }

    public async Task<Result<ZAiChatCompletionResponse>> CreateChatCompletionAsync(
        ZAiChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("Z.ai request: {Request}", json);
            }

            using var response = await _httpClient.PostAsync("chat/completions", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (_options.EnableLogging)
            {
                _logger.LogDebug("Z.ai response ({StatusCode}): {Content}", response.StatusCode, body);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<ZAiChatCompletionResponse>(MapError(response.StatusCode, body));
            }

            try
            {
                var result = JsonSerializer.Deserialize<ZAiChatCompletionResponse>(body, JsonOptions);
                return result != null
                    ? Result.Success(result)
                    : Result.Failure<ZAiChatCompletionResponse>(AIErrors.ProviderError("Empty response from provider"));
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to deserialize Z.ai response");
                return Result.Failure<ZAiChatCompletionResponse>(AIErrors.ProviderError("Invalid response format"));
            }
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Z.ai chat request timed out");
            return Result.Failure<ZAiChatCompletionResponse>(
                AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error communicating with Z.ai");
            return Result.Failure<ZAiChatCompletionResponse>(AIErrors.ProviderError(ex.Message));
        }
    }

    public async IAsyncEnumerable<Result<ZAiStreamChunk>> StreamChatCompletionAsync(
        ZAiChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        Stream? stream = null;

        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (_options.EnableLogging)
            {
                _logger.LogDebug("Z.ai streaming request: {Request}", json);
            }

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = content
            };

            Error? sendError = null;
            try
            {
                response = await _httpClient.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Z.ai streaming request timed out");
                sendError = AIErrors.Timeout(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error communicating with Z.ai");
                sendError = AIErrors.ProviderError(ex.Message);
            }

            if (sendError is not null)
            {
                yield return Result.Failure<ZAiStreamChunk>(sendError);
                yield break;
            }

            if (!response!.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                yield return Result.Failure<ZAiStreamChunk>(MapError(response.StatusCode, body));
                yield break;
            }

            stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            // Ends when ReadLineAsync returns null (end of stream), not on EndOfStream, which can block synchronously.
            while (!cancellationToken.IsCancellationRequested
                   && await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line[6..];
                if (data == "[DONE]")
                {
                    yield break;
                }

                ZAiStreamChunk? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<ZAiStreamChunk>(data, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse Z.ai stream chunk: {Data}", data);
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
    /// Z.ai's error: the HTTP status, and a business code in <c>error.code</c>
    /// (https://docs.z.ai/api-reference/api-code.md, read 2026-10-20). The code decides where it says more than the
    /// status: an exhausted balance is a 429 with 1113 there, not a 402.
    /// </summary>
    internal static Error MapError(HttpStatusCode status, string content)
    {
        string? message = null;
        string? code = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<ZAiErrorResponse>(content, JsonOptions);
            message = parsed?.Error?.Message;
            code = parsed?.Error?.Code switch
            {
                { ValueKind: JsonValueKind.String } s => s.GetString(),
                { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            // Not Z.ai's envelope: the raw body is the message.
        }

        message ??= string.IsNullOrWhiteSpace(content) ? status.ToString() : content;

        // The code overrides the status only where it means something else; the other 401s (1000-1005) and 429s
        // (1302, 1305, the usage windows 1308-1321) read from the status.
        return code switch
        {
            // A 429: no balance or resource package; a Coding Plan or enterprise package that expired.
            "1113" or "1309" or "1314" => AIErrors.InsufficientCredits(),

            // A 429 too, but no rate: the plan does not include the model, or the key is for another product.
            "1311" or "1315" => AIErrors.ProviderError(message, code),

            // 400s: an unknown model, content the filter stopped.
            "1211" => AIErrors.ModelNotFound(message),
            "1301" => AIErrors.ContentFiltered(message),
            _ => status switch
            {
                HttpStatusCode.Unauthorized => AIErrors.InvalidApiKey(),
                HttpStatusCode.PaymentRequired => AIErrors.InsufficientCredits(),
                HttpStatusCode.NotFound => AIErrors.ModelNotFound(message),
                HttpStatusCode.TooManyRequests => AIErrors.RateLimitExceeded(),
                _ => AIErrors.ProviderError(message, code),
            },
        };
    }
}
