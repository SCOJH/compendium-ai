// -----------------------------------------------------------------------
// <copyright file="MistralToolCallAssembler.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using Compendium.Adapters.Mistral.Http.Models;

namespace Compendium.Adapters.Mistral.Tools;

/// <summary>
/// Puts together the tool calls of one stream from its deltas' <c>tool_calls</c>. A call can come whole in one delta
/// (what Mistral usually sends) or in pieces: its id and name first, then its arguments over several deltas.
/// </summary>
/// <remarks>
/// The pieces of one call share an <c>index</c>. A piece with no <c>index</c> continues the last call. A piece that
/// carries an id other than its call's starts a new call, so two whole calls sent with Mistral's default index 0 stay
/// apart. An id that is empty or the literal <c>"null"</c> (the default in Mistral's schema) counts as no id. The name is
/// taken once, never joined. Calls keep the order in which they started.
/// </remarks>
internal sealed class MistralToolCallAssembler
{
    private readonly List<Call> _calls = new();
    private readonly Dictionary<int, Call> _byIndex = new();

    /// <summary>Adds the <c>tool_calls</c> of one delta; <see langword="null"/> or empty adds nothing.</summary>
    public void Add(IReadOnlyList<MistralToolCall>? pieces)
    {
        if (pieces is null)
        {
            return;
        }

        foreach (var piece in pieces)
        {
            var id = IdOf(piece.Id);
            var call = piece.Index is { } index
                ? _byIndex.GetValueOrDefault(index)
                : _calls.LastOrDefault();
            if (call is null || (id is not null && call.Id is not null && id != call.Id))
            {
                call = new Call();
                _calls.Add(call);
                if (piece.Index is { } newIndex)
                {
                    _byIndex[newIndex] = call;
                }
            }

            call.Id ??= id;
            if (call.Name is null && !string.IsNullOrEmpty(piece.Function?.Name))
            {
                call.Name = piece.Function.Name;
            }

            call.Arguments.Append(piece.Function?.Arguments);
        }
    }

    /// <summary>
    /// The calls put together, or <see langword="null"/> when the stream carried none. A call whose arguments never
    /// came gets <c>{}</c>.
    /// </summary>
    public IReadOnlyList<ToolCall>? Build() => _calls.Count == 0
        ? null
        : _calls.Select(c => new ToolCall
        {
            Id = c.Id ?? string.Empty,
            Name = c.Name ?? string.Empty,
            ArgumentsJson = c.Arguments.Length == 0 ? "{}" : c.Arguments.ToString(),
        }).ToList();

    /// <summary>An id as Mistral sent it, or <see langword="null"/> for none: empty, or the schema's default "null".</summary>
    internal static string? IdOf(string? id) =>
        string.IsNullOrEmpty(id) || string.Equals(id, "null", StringComparison.Ordinal) ? null : id;

    private sealed class Call
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
