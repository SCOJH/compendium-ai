// -----------------------------------------------------------------------
// <copyright file="ClaudeStructuredOutput.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using Compendium.Adapters.Anthropic.Http.Models;

namespace Compendium.Adapters.Anthropic.Services;

/// <summary>
/// Claude's structured output (<see cref="CompletionRequest.ResponseSchema"/>): <c>output_config.format</c>, with no beta
/// header, on Claude Sonnet 4.5, Opus 4.5, Haiku 4.5 and every later model (Fable and Mythos included), read from the id
/// whatever the date suffix. The older models (3.x, Opus 4 and 4.1, Sonnet 4) have none.
/// </summary>
/// <remarks>
/// Claude always constrains to the schema it is given (<see cref="ResponseSchema.Strict"/> has no setting there), and
/// refuses with a 400 what its subset of JSON Schema leaves out (numeric and string length bounds, among others). The
/// thinking is not constrained, only the answer. Citations and a prefilled assistant turn do not go with
/// <c>output_config.format</c>: Claude answers them with a 400, which comes back as the provider's error.
/// </remarks>
internal static class ClaudeStructuredOutput
{
    /// <summary>Whether <paramref name="model"/> takes <c>output_config.format</c>.</summary>
    public static bool Supports(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var current = ClaudeReasoning.Family().Match(model);
        if (!current.Success)
        {
            return false;
        }

        var family = current.Groups["family"].Value.ToLowerInvariant();
        var major = current.Groups["major"].Success ? int.Parse(current.Groups["major"].Value, CultureInfo.InvariantCulture) : -1;
        var minor = current.Groups["minor"].Success ? int.Parse(current.Groups["minor"].Value, CultureInfo.InvariantCulture) : 0;
        return family is "fable" or "mythos" || major >= 5 || (major == 4 && minor >= 5);
    }

    /// <summary>
    /// Sets <c>output_config.format</c> on <paramref name="apiRequest"/>, beside the effort the reasoning may have put
    /// there.
    /// </summary>
    public static void Apply(AnthropicMessagesRequest apiRequest, JsonElement schema)
    {
        apiRequest.OutputConfig ??= new AnthropicOutputConfig();
        apiRequest.OutputConfig.Format = new AnthropicOutputFormat { Schema = schema };
    }
}
