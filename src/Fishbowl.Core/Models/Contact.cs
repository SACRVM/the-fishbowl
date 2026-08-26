using System;

namespace Fishbowl.Core.Models;

public class Contact
{
    public string Id { get; set; } = string.Empty; // ULID
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }

    // Free-form Markdown about the person. Kept deliberately small — if the
    // record gets long, a proper Note linked to this contact is the right
    // shape. No `:::secret` blocks here in v1: there's no encrypted-BLOB
    // column and no decryption UI for contacts, so we do NOT strip markers
    // before FTS either — what you type is what gets indexed.
    public string? Notes { get; set; }

    // Archived rows stay in the DB but are hidden from the default list.
    // Matches Note.Archived semantics.
    public bool Archived { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
