using System.Text.RegularExpressions;
using Fishbowl.Core.Models;

namespace Fishbowl.Core.Util;

// Removes `::secret ... ::end` blocks and `content_secret` blobs from any
// note that's about to cross a trust boundary (MCP response, embeddings
// input, search snippet). CONCEPT § Core Philosophy § MCP Server makes
// this non-negotiable — secrets are human-access only.
public static class SecretStripper
{
    // Matches `::secret` on its own line through `::end` on its own line,
    // inclusive. Singleline so `.` crosses newlines.
    private static readonly Regex Block = new(
        @"::secret\s*\r?\n.*?\r?\n\s*::end",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Post-encryption marker form (Phase 3): `::secret#N::end` on a single
    // line, where N is the ciphertext index in content_secret. These markers
    // contain no secret data themselves, but leaking them reveals which
    // notes contain secrets and how many — strip anyway, same placeholder.
    private static readonly Regex Marker = new(
        @"::secret#\d+::end",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string Placeholder = "[secret content hidden]";

    public static string? Strip(string? content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        var stripped = Block.Replace(content, Placeholder);
        return Marker.Replace(stripped, Placeholder);
    }

    // Returns a shallow copy of the note with content stripped and the
    // encrypted blob nulled. The original is not mutated.
    public static Note StripNote(Note note)
    {
        return new Note
        {
            Id = note.Id,
            Title = note.Title,
            Content = Strip(note.Content),
            ContentSecret = null,
            Type = note.Type,
            Tags = note.Tags?.ToList() ?? new List<string>(),
            CreatedBy = note.CreatedBy,
            CreatedAt = note.CreatedAt,
            UpdatedAt = note.UpdatedAt,
            Pinned = note.Pinned,
            Archived = note.Archived,
        };
    }
}
