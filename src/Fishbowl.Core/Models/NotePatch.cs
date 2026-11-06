using System;
using System.Collections.Generic;

namespace Fishbowl.Core.Models;

/// <summary>
/// A change to one note, applied in one transaction against what is stored
/// (<c>INoteRepository.PatchAsync</c>) — no read-modify-write race, and the
/// fields left null stay as they are. Tags: <see cref="Tags"/> replaces the
/// list, <see cref="AddTags"/> / <see cref="RemoveTags"/> change it in place
/// (after a replace, when both are given). <see cref="Append"/> adds text at
/// the end — the one text change a locked note takes. <see cref="Slug"/> ""
/// clears it. <see cref="Locked"/> sets or lifts the lock; who may lift it is
/// the caller's check.
/// </summary>
public sealed class NotePatch
{
    public string? Title { get; set; }
    public string? Content { get; set; }
    public string? Append { get; set; }
    public List<string>? Tags { get; set; }
    public List<string>? AddTags { get; set; }
    public List<string>? RemoveTags { get; set; }
    public string? Slug { get; set; }
    public bool? Locked { get; set; }
    public bool? Pinned { get; set; }
    public bool? Archived { get; set; }

    /// <summary>Whether the patch touches the title or the text.</summary>
    public bool ChangesText => Title is not null || Content is not null || Append is not null;
}

/// <summary>
/// A note as it was before an agent changed it (user/space schema v31,
/// <c>note_versions</c>): the owner sees what the write under
/// <c>review:pending</c> did. The newest <see cref="NoteVersionLimits.Kept"/> a note are kept.
/// </summary>
public sealed class NoteVersion
{
    public string Id { get; set; } = string.Empty;
    public string NoteId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Content { get; set; }
    public List<string> Tags { get; set; } = new();
    /// <summary>When the change that replaced this state was made.</summary>
    public DateTime SavedAt { get; set; }
    /// <summary>Who made that change (a user id; for an agent, its key's owner).</summary>
    public string? SavedBy { get; set; }
    /// <summary>"mcp" for an agent's write.</summary>
    public string Source { get; set; } = "mcp";
}

public static class NoteVersionLimits
{
    public const int Kept = 50;
}

/// <summary>
/// A saved filter of the Notes app (user/space schema v31,
/// <c>note_filters</c>): a name and a tag expression
/// (<c>kind:decision AND NOT status:superseded</c>). The workspace's — in a
/// space everyone's.
/// </summary>
public sealed class NoteFilter
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Expr { get; set; } = string.Empty;
    public double Position { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A change to one todo, applied in one transaction (<c>ITodoRepository.PatchAsync</c>).
/// Null = unchanged; <see cref="ClearDue"/> removes the due date.
/// </summary>
public sealed class TodoPatch
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public DateTime? DueAt { get; set; }
    public bool ClearDue { get; set; }
    public bool? Done { get; set; }
    public List<string>? Tags { get; set; }
    public List<string>? AddTags { get; set; }
    public List<string>? RemoveTags { get; set; }
}
