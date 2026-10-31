// -----------------------------------------------------------------------
// <copyright file="BedrockStructuredOutput.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Text.RegularExpressions;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace Compendium.Adapters.Bedrock.Services;

/// <summary>
/// Converse's structured output (<see cref="CompletionRequest.ResponseSchema"/>): <c>outputConfig.textFormat</c>
/// <c>{type: "json_schema", structure: {jsonSchema: {name, schema}}}</c>, the schema as a JSON string. Sent to the Claude
/// models that have it, Sonnet, Opus and Haiku 4.5 and later, Fable and Mythos, read from the model id whatever its
/// region prefix (<c>us.</c>, <c>global.</c>…) or version suffix.
/// </summary>
/// <remarks>
/// Bedrock lists other models with structured output on their model cards; until each is checked, they get the request
/// without the schema and a warning. Bedrock takes a subset of JSON Schema: every object must set
/// <c>additionalProperties: false</c>, and numeric and string length bounds are refused (400). A first request with a new
/// schema can take up to about 90 seconds while Bedrock compiles it.
/// </remarks>
internal static partial class BedrockStructuredOutput
{
    /// <summary>Whether <paramref name="modelId"/> takes <c>outputConfig.textFormat</c>, as far as the adapter knows.</summary>
    public static bool Supports(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var claude = Claude().Match(modelId);
        if (!claude.Success)
        {
            return false;
        }

        var family = claude.Groups["family"].Value.ToLowerInvariant();
        var major = claude.Groups["major"].Success ? int.Parse(claude.Groups["major"].Value, CultureInfo.InvariantCulture) : -1;
        var minor = claude.Groups["minor"].Success ? int.Parse(claude.Groups["minor"].Value, CultureInfo.InvariantCulture) : 0;
        return family is "fable" or "mythos" || major >= 5 || (major == 4 && minor >= 5);
    }

    /// <summary>The <c>outputConfig</c> that carries <paramref name="schema"/>.</summary>
    public static OutputConfig For(ResponseSchema schema) => new()
    {
        TextFormat = new OutputFormat
        {
            Type = OutputFormatType.Json_schema,
            Structure = new OutputFormatStructure
            {
                JsonSchema = new JsonSchemaDefinition { Name = schema.Name, Schema = schema.SchemaJson },
            },
        },
    };

    // anthropic.claude-sonnet-4-5-20250929-v1:0, us.anthropic.claude-opus-4-7, global.anthropic.claude-haiku-4-5-20251001-v1:0:
    // a 1-2 digit minor, never the start of a date (the pattern the Anthropic adapter reads its ids with).
    [GeneratedRegex(@"claude-(?<family>opus|sonnet|haiku|fable|mythos)(?:-(?<major>\d{1,2})(?:[-.](?<minor>\d{1,2})(?!\d))?(?!\d))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Claude();
}
