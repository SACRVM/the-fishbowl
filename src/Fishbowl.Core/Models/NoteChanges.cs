using System;
using System.Collections.Generic;

namespace Fishbowl.Core.Models;

/// <summary>
/// What a copy kept in sync asks for (MCP <c>note_list</c>): every note in
/// the order it last changed — <c>updated_at</c>, then id, both ascending —
/// optionally only those changed after <see cref="Since"/>, past the keyset
/// <see cref="AfterStamp"/>/<see cref="AfterId"/> of the previous page.
/// </summary>
public sealed record NoteChangeQuery(
    IReadOnlyCollection<string>? Tags,
    string Match,
    DateTime? Since,
    string? AfterStamp,
    string? AfterId,
    int Limit);

/// <summary>A note with its stored <c>updated_at</c> text — the keyset a
/// page's cursor holds, compared exactly as stored.</summary>
public sealed record NoteChange(Note Note, string Stamp);
