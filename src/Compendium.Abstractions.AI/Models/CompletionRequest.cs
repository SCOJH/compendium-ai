// -----------------------------------------------------------------------
// <copyright file="CompletionRequest.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Abstractions.AI.Models;

/// <summary>
/// Represents a request for AI completion.
/// </summary>
public sealed record CompletionRequest
{
    /// <summary>
    /// The temperature every adapter except Anthropic sends when <see cref="Temperature"/> is <see langword="null"/>:
    /// 0.7, what <see cref="Temperature"/> defaulted to before it became optional, so a caller that never set one
    /// puts the same request on the wire as before.
    /// </summary>
    public const float FallbackTemperature = 0.7f;

    /// <summary>
    /// Gets the model identifier to use for completion.
    /// Examples: "anthropic/claude-3.5-sonnet", "openai/gpt-4o", "meta-llama/llama-3.1-70b-instruct".
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// Gets the messages to send to the model.
    /// </summary>
    public required IReadOnlyList<Message> Messages { get; init; }

    /// <summary>
    /// Gets the optional system prompt to prepend.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Gets the temperature for randomness (0.0 to 2.0), or <see langword="null"/> (the default) to leave it unset.
    /// </summary>
    /// <remarks>
    /// Unset, the Anthropic adapter sends no <c>temperature</c> at all: Claude Opus 4.7 and later, Sonnet 5, Fable and
    /// Mythos answer any sampling parameter with a 400. The other adapters send <see cref="FallbackTemperature"/>.
    /// </remarks>
    public float? Temperature { get; init; }

    /// <summary>
    /// Gets the maximum number of tokens to generate.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Gets the top-p (nucleus) sampling value, or <see langword="null"/> (the default) to leave it unset: no adapter
    /// then sends one.
    /// </summary>
    public float? TopP { get; init; }

    /// <summary>
    /// Gets the frequency penalty (-2.0 to 2.0).
    /// </summary>
    public float? FrequencyPenalty { get; init; }

    /// <summary>
    /// Gets the presence penalty (-2.0 to 2.0).
    /// </summary>
    public float? PresencePenalty { get; init; }

    /// <summary>
    /// Gets the model's own reasoning channel for this request, or <see langword="null"/> (the default) to leave the
    /// request as it was: see <see cref="ReasoningOptions"/>. With it, the adapters whose provider refuses sampling
    /// parameters with reasoning (Claude, OpenAI's reasoning models) send no temperature and no top_p.
    /// </summary>
    public ReasoningOptions? Reasoning { get; init; }

    /// <summary>
    /// Gets the JSON Schema the answer must follow, constrained by the provider itself, or <see langword="null"/> (the
    /// default) to leave the request as it was: see <see cref="Models.ResponseSchema"/>. An adapter that cannot constrain
    /// the model (<see cref="IAIProvider.SupportsJsonSchema"/> <see langword="false"/>) sends the request without it and
    /// logs a warning. It takes precedence over a provider's own structured-output parameters
    /// (<see cref="AdditionalParameters"/>).
    /// </summary>
    public ResponseSchema? ResponseSchema { get; init; }

    /// <summary>
    /// Gets the stop sequences that will halt generation.
    /// </summary>
    public IReadOnlyList<string>? StopSequences { get; init; }

    /// <summary>
    /// Gets additional provider-specific parameters.
    /// </summary>
    public IReadOnlyDictionary<string, object>? AdditionalParameters { get; init; }

    /// <summary>
    /// Gets the optional request ID for tracking and idempotency.
    /// </summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// Gets the tenant ID for multi-tenant scenarios.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Gets the user ID for usage tracking.
    /// </summary>
    public string? UserId { get; init; }
}

/// <summary>
/// Represents a message in a conversation.
/// </summary>
public sealed record Message
{
    /// <summary>
    /// Gets the role of the message sender.
    /// </summary>
    public required MessageRole Role { get; init; }

    /// <summary>
    /// Gets the content of the message.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Gets the optional name of the participant.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the tool calls the assistant made in this turn through the provider's native tool calling, as read from
    /// <see cref="CompletionResponse.ToolCalls"/> or <see cref="CompletionChunk.ToolCalls"/>. It is
    /// <see langword="null"/> by default, when the turn made no call.
    /// </summary>
    /// <remarks>
    /// It is read on an <see cref="MessageRole.Assistant"/> message only, whose <see cref="Content"/> is then the text
    /// sent with the calls, possibly empty. An empty list counts as no call. An adapter without native tool calling
    /// ignores it, and the message goes on the wire as it did before this property existed. Left out of JSON when
    /// <see langword="null"/>.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// Gets the id of the tool call (<see cref="ToolCall.Id"/>) whose result this message's <see cref="Content"/> is.
    /// It is <see langword="null"/> by default.
    /// </summary>
    /// <remarks>
    /// It is read on a <see cref="MessageRole.Tool"/> message only. An adapter without native tool calling ignores it,
    /// and the message goes on the wire as it did before this property existed. Left out of JSON when
    /// <see langword="null"/>.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    /// <summary>
    /// Creates a system message.
    /// </summary>
    public static Message System(string content) => new() { Role = MessageRole.System, Content = content };

    /// <summary>
    /// Creates a user message.
    /// </summary>
    public static Message User(string content) => new() { Role = MessageRole.User, Content = content };

    /// <summary>
    /// Creates an assistant message.
    /// </summary>
    public static Message Assistant(string content) => new() { Role = MessageRole.Assistant, Content = content };

    /// <summary>
    /// Creates an assistant message that carries the tool calls the model made in that turn.
    /// </summary>
    /// <param name="content">The text the model sent with the calls; empty when it sent none.</param>
    /// <param name="toolCalls">The calls, as the response or the stream gave them.</param>
    public static Message Assistant(string content, IReadOnlyList<ToolCall> toolCalls)
    {
        ArgumentNullException.ThrowIfNull(toolCalls);
        return new() { Role = MessageRole.Assistant, Content = content, ToolCalls = toolCalls };
    }

    /// <summary>
    /// Creates the <see cref="MessageRole.Tool"/> message that answers a tool call with its result.
    /// </summary>
    /// <param name="toolCallId">The <see cref="ToolCall.Id"/> of the call answered.</param>
    /// <param name="content">The tool's result, as text (JSON output included).</param>
    public static Message ToolResult(string toolCallId, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);
        return new() { Role = MessageRole.Tool, Content = content, ToolCallId = toolCallId };
    }
}

/// <summary>
/// Represents the role of a message sender.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageRole
{
    /// <summary>
    /// System message providing instructions to the model.
    /// </summary>
    System,

    /// <summary>
    /// User message from the human.
    /// </summary>
    User,

    /// <summary>
    /// Assistant message from the AI.
    /// </summary>
    Assistant,

    /// <summary>
    /// Tool/function result message.
    /// </summary>
    Tool
}
