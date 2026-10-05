using Fishbowl.Core.Models;

namespace Fishbowl.Core.Util;

// Hard upper bounds for the user-supplied fields on a Note. Generous on
// purpose — "stop-the-broken-client" guards, not editorial constraints.
// A note's not supposed to be a multi-megabyte buffer; if a request blows
// past these, the client either has a bug or is trying to fill the WAL.
//
// Validation runs in the repository (defense in depth, covers MCP +
// cookie + any future caller) and the API edge translates the throw into
// a friendlier 413 with the offending field name.
public static class NoteLimits
{
    public const string Resource = "note";

    // 8 KB titles. The UI's title field is single-line; anything bigger is
    // pasted accidentally or programmatically.
    public const int MaxTitleLength = 8 * 1024;

    // 4 MB plaintext content. Markdown notes are normally <100 KB; the
    // ceiling exists so a runaway paste or buggy import doesn't crater
    // FTS5 indexing.
    public const int MaxContentLength = 4 * 1024 * 1024;

    // 4 MB encrypted blob. Practical upper bound is set by client-side
    // crypto: each :::secret block becomes IV(12) + ciphertext + tag(16)
    // base64-encoded inside a JSON envelope. Even with hundreds of
    // secrets, real notes don't approach this.
    public const int MaxContentSecretBytes = 4 * 1024 * 1024;

    // 64 distinct tags per note. The model permits arbitrary tag arrays
    // but anything past a couple dozen is almost certainly a bug.
    public const int MaxTagCount = 64;

    // Per-tag length. Tag colour + system flags live elsewhere; the tag
    // *name* itself wants to fit on one line of UI chrome. TagName's rule —
    // the one every tag is stored under — decides it.
    public const int MaxTagLength = TagName.MaxLength;

    // Returns the first validation error, or null if everything fits.
    // Single-error return keeps the API response small and unambiguous —
    // the client fixes one thing and retries.
    public static ResourceValidationError? Validate(Note note)
    {
        if (note.Title is { Length: > MaxTitleLength })
            return new ResourceValidationError(Resource, "title", $"exceeds {MaxTitleLength} characters");

        if (note.Content is { Length: > MaxContentLength })
            return new ResourceValidationError(Resource, "content", $"exceeds {MaxContentLength} characters");

        if (note.ContentSecret is { Length: > MaxContentSecretBytes })
            return new ResourceValidationError(Resource, "contentSecret", $"exceeds {MaxContentSecretBytes} bytes");

        if (note.Tags is { Count: > MaxTagCount })
            return new ResourceValidationError(Resource, "tags", $"exceeds {MaxTagCount} entries");

        if (note.Tags is not null)
        {
            // Every tag is stored under TagName's rule; one it can't take is
            // the caller's mistake (400), not a crash in the tag repository.
            // Blank entries are dropped on save.
            foreach (var tag in note.Tags)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                if (!TagName.IsValid(tag))
                    return new ResourceValidationError(Resource, "tags",
                        $"a tag must be 1–{MaxTagLength} characters of a–z, 0–9, _, : and -",
                        ResourceValidationKind.Invalid);
            }
        }

        return null;
    }
}
