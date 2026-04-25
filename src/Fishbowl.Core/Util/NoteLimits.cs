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
    // 8 KB titles. The UI's title field is single-line; anything bigger is
    // pasted accidentally or programmatically.
    public const int MaxTitleLength = 8 * 1024;

    // 4 MB plaintext content. Markdown notes are normally <100 KB; the
    // ceiling exists so a runaway paste or buggy import doesn't crater
    // FTS5 indexing.
    public const int MaxContentLength = 4 * 1024 * 1024;

    // 4 MB encrypted blob. Practical upper bound is set by client-side
    // crypto: each ::secret block becomes IV(12) + ciphertext + tag(16)
    // base64-encoded inside a JSON envelope. Even with hundreds of
    // secrets, real notes don't approach this.
    public const int MaxContentSecretBytes = 4 * 1024 * 1024;

    // 64 distinct tags per note. The model permits arbitrary tag arrays
    // but anything past a couple dozen is almost certainly a bug.
    public const int MaxTagCount = 64;

    // Per-tag length. Tag colour + system flags live elsewhere; the tag
    // *name* itself wants to fit on one line of UI chrome.
    public const int MaxTagLength = 128;

    public sealed record ValidationError(string Field, string Reason)
    {
        public override string ToString() => $"{Field}: {Reason}";
    }

    // Returns the first validation error, or null if everything fits.
    // Single-error return keeps the API response small and unambiguous —
    // the client fixes one thing and retries.
    public static ValidationError? Validate(Note note)
    {
        if (note.Title is { Length: > MaxTitleLength })
            return new ValidationError("title", $"exceeds {MaxTitleLength} characters");

        if (note.Content is { Length: > MaxContentLength })
            return new ValidationError("content", $"exceeds {MaxContentLength} characters");

        if (note.ContentSecret is { Length: > MaxContentSecretBytes })
            return new ValidationError("contentSecret", $"exceeds {MaxContentSecretBytes} bytes");

        if (note.Tags is { Count: > MaxTagCount })
            return new ValidationError("tags", $"exceeds {MaxTagCount} entries");

        if (note.Tags is not null)
        {
            foreach (var tag in note.Tags)
            {
                if (tag is { Length: > MaxTagLength })
                    return new ValidationError("tags", $"a tag exceeds {MaxTagLength} characters");
            }
        }

        return null;
    }
}
