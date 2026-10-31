// -----------------------------------------------------------------------
// <copyright file="OllamaApiModels.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.Ollama.Http.Models;

/// <summary>
/// Ollama chat request — <c>POST /api/chat</c>.
/// </summary>
internal sealed class OllamaChatRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("messages")]
    public required List<OllamaChatMessage> Messages { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("tools")]
    public List<OllamaToolDefinition>? Tools { get; set; }

    /// <summary>A JSON Schema the answer must follow (<see cref="CompletionRequest.ResponseSchema"/>), or unset.</summary>
    [JsonPropertyName("format")]
    public JsonElement? Format { get; set; }

    [JsonPropertyName("keep_alive")]
    public string? KeepAlive { get; set; }

    [JsonPropertyName("options")]
    public OllamaRequestOptions? Options { get; set; }
}

/// <summary>
/// A single chat message.
/// </summary>
internal sealed class OllamaChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("images")]
    public List<string>? Images { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OllamaToolCall>? ToolCalls { get; set; }
}

/// <summary>
/// Generation-time tuning. Maps a subset of the Compendium completion fields to Ollama's
/// <c>options</c> object. Field names follow Ollama's <c>snake_case</c> convention.
/// </summary>
internal sealed class OllamaRequestOptions
{
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public float? FrequencyPenalty { get; set; }

    [JsonPropertyName("presence_penalty")]
    public float? PresencePenalty { get; set; }

    [JsonPropertyName("num_predict")]
    public int? NumPredict { get; set; }

    [JsonPropertyName("stop")]
    public List<string>? Stop { get; set; }
}

/// <summary>
/// Top-level tool definition forwarded to a tool-capable Ollama model.
/// </summary>
internal sealed class OllamaToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public required OllamaFunctionDefinition Function { get; set; }
}

/// <summary>
/// Function shape Ollama expects under <c>tools[].function</c>.
/// </summary>
internal sealed class OllamaFunctionDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }
}

/// <summary>
/// A single tool invocation surfaced by the model.
/// </summary>
internal sealed class OllamaToolCall
{
    [JsonPropertyName("function")]
    public OllamaToolCallFunction? Function { get; set; }
}

/// <summary>
/// Function payload of a tool call. Ollama returns <c>arguments</c> as a JSON object
/// (not a JSON-encoded string as OpenAI does).
/// </summary>
internal sealed class OllamaToolCallFunction
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; set; }
}

/// <summary>
/// Non-streaming chat response shape, or a single streaming chunk when <c>stream=true</c>.
/// Each streamed chunk is itself a self-contained <c>OllamaChatResponse</c>; the final chunk
/// has <c>done=true</c> and includes the aggregated timings.
/// </summary>
internal sealed class OllamaChatResponse
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("message")]
    public OllamaChatMessage? Message { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("done_reason")]
    public string? DoneReason { get; set; }

    [JsonPropertyName("total_duration")]
    public long? TotalDuration { get; set; }

    [JsonPropertyName("load_duration")]
    public long? LoadDuration { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int? PromptEvalCount { get; set; }

    [JsonPropertyName("prompt_eval_duration")]
    public long? PromptEvalDuration { get; set; }

    [JsonPropertyName("eval_count")]
    public int? EvalCount { get; set; }

    [JsonPropertyName("eval_duration")]
    public long? EvalDuration { get; set; }
}

/// <summary>
/// Ollama embeddings request — newer endpoint <c>POST /api/embed</c> supports batch input.
/// </summary>
internal sealed class OllamaEmbedRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    /// <summary>
    /// Either a single string or an array of strings. The newer <c>/api/embed</c> endpoint
    /// accepts both shapes; we always send an array.
    /// </summary>
    [JsonPropertyName("input")]
    public required List<string> Input { get; set; }

    [JsonPropertyName("keep_alive")]
    public string? KeepAlive { get; set; }

    [JsonPropertyName("options")]
    public OllamaRequestOptions? Options { get; set; }
}

/// <summary>
/// Response from <c>/api/embed</c>.
/// </summary>
internal sealed class OllamaEmbedResponse
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("embeddings")]
    public List<float[]> Embeddings { get; set; } = new();

    [JsonPropertyName("total_duration")]
    public long? TotalDuration { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int? PromptEvalCount { get; set; }
}

/// <summary>
/// Legacy embeddings request — <c>POST /api/embeddings</c>. Single-string only. Retained for
/// fallback against older Ollama releases.
/// </summary>
internal sealed class OllamaLegacyEmbeddingsRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; set; }

    [JsonPropertyName("keep_alive")]
    public string? KeepAlive { get; set; }
}

/// <summary>
/// Response from the legacy <c>/api/embeddings</c> endpoint.
/// </summary>
internal sealed class OllamaLegacyEmbeddingsResponse
{
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = Array.Empty<float>();
}

/// <summary>
/// Pull request — <c>POST /api/pull</c>.
/// </summary>
internal sealed class OllamaPullRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }
}

/// <summary>
/// A single line emitted by <c>/api/pull</c> while a model is being downloaded.
/// </summary>
internal sealed class OllamaPullProgress
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("total")]
    public long? Total { get; set; }

    [JsonPropertyName("completed")]
    public long? Completed { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Response from <c>GET /api/tags</c> (list local models).
/// </summary>
internal sealed class OllamaTagsResponse
{
    [JsonPropertyName("models")]
    public List<OllamaTagModel> Models { get; set; } = new();
}

/// <summary>
/// Single locally-available model.
/// </summary>
internal sealed class OllamaTagModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("modified_at")]
    public string? ModifiedAt { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("details")]
    public OllamaModelDetails? Details { get; set; }
}

/// <summary>
/// Per-model metadata returned by <c>/api/tags</c>.
/// </summary>
internal sealed class OllamaModelDetails
{
    [JsonPropertyName("family")]
    public string? Family { get; set; }

    [JsonPropertyName("families")]
    public List<string>? Families { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("parameter_size")]
    public string? ParameterSize { get; set; }

    [JsonPropertyName("quantization_level")]
    public string? QuantizationLevel { get; set; }
}

/// <summary>
/// Error payload Ollama returns on non-2xx responses.
/// </summary>
internal sealed class OllamaErrorResponse
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
