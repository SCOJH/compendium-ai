// -----------------------------------------------------------------------
// <copyright file="ResponseSchema.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Compendium.Abstractions.AI.Models;

/// <summary>
/// A JSON Schema the answer of one request must follow, constrained by the provider itself (structured output): OpenAI,
/// Azure OpenAI, Mistral and LiteLLM's <c>response_format.json_schema</c>, Gemini's <c>responseJsonSchema</c>, Ollama's
/// <c>format</c>, Claude's <c>output_config.format</c>. Each adapter maps it to what its provider accepts. Unset
/// (<see cref="CompletionRequest.ResponseSchema"/> <see langword="null"/>), the request is the one it was before this
/// option existed.
/// </summary>
/// <remarks>
/// <para>
/// Whether a provider constrains a given model is <see cref="IAIProvider.SupportsJsonSchema"/>. Where it does not, the
/// request is sent without the schema, without an error, and the adapter logs a warning: the caller that needs the constraint asks first and, on
/// <see langword="false"/>, puts the schema in the prompt and checks the answer itself. A provider that has a JSON mode
/// but no schema (DeepSeek) gets its JSON mode.
/// </para>
/// <para>
/// A constrained answer still comes as text, in <see cref="CompletionResponse.Content"/> or the stream's
/// <c>ContentDelta</c>: the caller parses it. It can still fall short of the schema when the answer is cut
/// (<see cref="FinishReason.Length"/>) or refused. Providers accept different subsets of JSON Schema; one the provider
/// refuses comes back as its error.
/// </para>
/// </remarks>
public sealed partial record ResponseSchema
{
    /// <summary>
    /// The schema's name, sent where the provider takes one (OpenAI-style <c>json_schema.name</c>): 1 to 64 letters,
    /// digits, <c>_</c> or <c>-</c>, the strictest rule among them, so the same name goes to every provider.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>The JSON Schema, as a JSON object.</summary>
    /// <remarks>
    /// It goes to the provider as written: no adapter rewrites it. The subset every constraining provider takes: each
    /// object sets <c>additionalProperties: false</c> and lists every property in <c>required</c> (an optional one is a
    /// union with <c>null</c>, <c>"type": ["string", "null"]</c>), which OpenAI and Azure OpenAI demand with
    /// <see cref="Strict"/>, Claude and Bedrock always; and no <c>minimum</c> / <c>maximum</c>, <c>minLength</c> /
    /// <c>maxLength</c>, which Claude and Bedrock refuse (Gemini ignores the length bounds). Check those in the answer
    /// instead.
    /// </remarks>
    public required string SchemaJson { get; init; }

    /// <summary>
    /// Whether the provider is asked to follow the schema exactly (<c>strict</c>), where it has the setting (OpenAI,
    /// Azure OpenAI, Mistral, LiteLLM). Default <see langword="true"/>. The providers without it (Gemini, Ollama, Claude)
    /// always constrain to the schema they are given.
    /// </summary>
    public bool Strict { get; init; } = true;

    /// <summary>
    /// The schema as it goes on the wire, or <see cref="AIErrors.InvalidRequest"/> when <see cref="Name"/> breaks its rule
    /// or <see cref="SchemaJson"/> is not a JSON object. An adapter that sends the schema calls it before the request, so
    /// an invalid schema fails without reaching the provider.
    /// </summary>
    public Result<JsonElement> Parse()
    {
        if (string.IsNullOrEmpty(Name) || !NameRule().IsMatch(Name))
        {
            return Result.Failure<JsonElement>(AIErrors.InvalidRequest(
                $"ResponseSchema name '{Name}' must be 1 to 64 letters, digits, '_' or '-'."));
        }

        if (string.IsNullOrWhiteSpace(SchemaJson))
        {
            return Result.Failure<JsonElement>(AIErrors.InvalidRequest($"ResponseSchema '{Name}' has no schema."));
        }

        try
        {
            using var document = JsonDocument.Parse(SchemaJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? Result.Success(document.RootElement.Clone())
                : Result.Failure<JsonElement>(AIErrors.InvalidRequest(
                    $"ResponseSchema '{Name}' must be a JSON object, not {document.RootElement.ValueKind}."));
        }
        catch (JsonException ex)
        {
            return Result.Failure<JsonElement>(AIErrors.InvalidRequest(
                $"ResponseSchema '{Name}' is not valid JSON: {ex.Message}"));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRule();
}
