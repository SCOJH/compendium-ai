// -----------------------------------------------------------------------
// <copyright file="ToolCall.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Abstractions.AI.Models;

/// <summary>
/// A tool call the model made through its provider's native tool calling (the request's <c>tools</c>), with the id the
/// provider gave it.
/// </summary>
/// <remarks>
/// It is read from a response (<see cref="CompletionResponse.ToolCalls"/>) or from the end of a stream
/// (<see cref="CompletionChunk.ToolCalls"/>). The next request sends it back on the assistant turn that made it
/// (<see cref="Message.ToolCalls"/>), and the tool's result answers it by its id (<see cref="Message.ToolCallId"/>).
/// </remarks>
public sealed record ToolCall
{
    /// <summary>
    /// Gets the provider's id for this call. The <see cref="MessageRole.Tool"/> message that carries the result names
    /// it in <see cref="Message.ToolCallId"/>. Send it back exactly as received: a provider can refuse an id it did not
    /// give (Mistral's are nine letters and digits, such as <c>D681PevKs</c> in its guide).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the name of the tool called, as the request's tools declared it.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the arguments: a JSON object as text, as the model wrote it. In a stream, every piece the provider sent is
    /// already joined. Not validated: a model can write invalid JSON, so parse it before use.
    /// </summary>
    public required string ArgumentsJson { get; init; }
}
