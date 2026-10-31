// -----------------------------------------------------------------------
// <copyright file="MistralToolCallAssembler.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
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
/// apart. Without two ids to compare, a piece that carries a name when its call already has one starts a new call too:
/// another tool's name, or the same name once the call's arguments are whole JSON (before that, a repeated name
/// continues the call). An id that is empty or the literal <c>"null"</c> (the default in Mistral's schema) counts as no
/// id. The name is taken once, never joined. Calls keep the order in which they started.
/// </remarks>
internal sealed class MistralToolCallAssembler
{
    private const string IdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

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
            var name = piece.Function?.Name;
            var call = piece.Index is { } index
                ? _byIndex.GetValueOrDefault(index)
                : _calls.LastOrDefault();
            if (StartsANewCall(call, id, name))
            {
                call = new Call();
                _calls.Add(call);
                if (piece.Index is { } newIndex)
                {
                    _byIndex[newIndex] = call;
                }
            }

            call.Id ??= id;
            if (call.Name is null && !string.IsNullOrEmpty(name))
            {
                call.Name = name;
            }

            call.Arguments.Append(piece.Function?.Arguments);
        }
    }

    /// <summary>
    /// The calls put together, or <see langword="null"/> when the stream carried none. A call whose arguments never
    /// came gets <c>{}</c>; one that never got an id gets <see cref="IdFor"/> of the stream's id and its place.
    /// </summary>
    /// <param name="streamId">The stream's completion id (its chunks' <c>id</c>).</param>
    public IReadOnlyList<ToolCall>? Build(string? streamId) => _calls.Count == 0
        ? null
        : _calls.Select((c, position) =>
        {
            var name = c.Name ?? string.Empty;
            var arguments = c.Arguments.Length == 0 ? "{}" : c.Arguments.ToString();
            return new ToolCall
            {
                Id = c.Id ?? IdFor(streamId, position, name, arguments),
                Name = name,
                ArgumentsJson = arguments,
            };
        }).ToList();

    /// <summary>An id as Mistral sent it, or <see langword="null"/> for none: empty, or the schema's default "null".</summary>
    internal static string? IdOf(string? id) =>
        string.IsNullOrEmpty(id) || string.Equals(id, "null", StringComparison.Ordinal) ? null : id;

    /// <summary>
    /// The id given to a call Mistral sent without one, so that its result can answer it
    /// (<see cref="Message.ToolResult"/> takes no empty id). Nine letters and digits, the format of Mistral's own ids,
    /// which its API checks on <c>tool_call_id</c>. Derived from the completion's id, the call's place among its calls,
    /// its name and its arguments: the same response, read whole or from a stream, always gives the same id.
    /// </summary>
    internal static string IdFor(string? completionId, int position, string name, string arguments)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{completionId}\n{position}\n{name}\n{arguments}"));
        var id = new char[9];
        for (var i = 0; i < id.Length; i++)
        {
            id[i] = IdAlphabet[hash[i] % IdAlphabet.Length];
        }

        return new string(id);
    }

    private static bool StartsANewCall([NotNullWhen(false)] Call? call, string? id, string? name)
    {
        if (call is null)
        {
            return true;
        }

        if (id is not null && call.Id is not null)
        {
            return id != call.Id;
        }

        return !string.IsNullOrEmpty(name)
            && call.Name is not null
            && (name != call.Name || IsWholeJson(call.Arguments.ToString()));
    }

    /// <summary>Whether arguments joined so far are one whole JSON value (a call's arguments can be invalid JSON).</summary>
    private static bool IsWholeJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class Call
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
