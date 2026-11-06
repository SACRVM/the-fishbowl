using System;
using System.Collections.Generic;

namespace Fishbowl.Core.Models;

public class Note
{
    public string Id { get; set; } = string.Empty; // ULID
    public string Title { get; set; } = string.Empty;
    public string? Content { get; set; } // Markdown (public part)
    public byte[]? ContentSecret { get; set; } // AES-256 encrypted
    public string Type { get; set; } = "note"; // note | idea | journal | password
    public List<string> Tags { get; set; } = new();
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool Pinned { get; set; }
    public bool Archived { get; set; }

    // A stable external key (user/space schema v31): what a copy kept
    // elsewhere names the note by — a file name in an agent's Git mirror.
    // Unique in the workspace, any case. On an update null keeps the stored
    // one and "" clears it (NoteSlug).
    public string? Slug { get; set; }

    // A locked note's title and text don't change (v31) — a decision once
    // written; only an append at its end. Tags, pin and archive still do.
    // Set and lifted only through a patch (NotePatch.Locked): an update
    // keeps what is stored, whatever it says here.
    public bool Locked { get; set; }
}
