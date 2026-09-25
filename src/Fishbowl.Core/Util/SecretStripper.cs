using System.Text.RegularExpressions;
using Fishbowl.Core.Models;

namespace Fishbowl.Core.Util;

// Removes `:::secret ... :::end` blocks and `content_secret` blobs from any
// note that's about to cross a trust boundary (MCP response, embeddings
// input, search snippet). CONCEPT § Core Philosophy § MCP Server makes
// this non-negotiable — secrets are human-access only.
public static class SecretStripper
{
    // Matches `:::secret` on its own line through `:::end` on its own line,
    // inclusive. Singleline so `.` crosses newlines.
    //
    // `:{2,3}` accepts both the current three-colon form and the original
    // two-colon one. New content is always written with three (it reads as a
    // Pandoc-style fenced div), but notes predating the change keep their two
    // and must keep being stripped — a delimiter this regex fails to match is
    // secret content crossing a trust boundary in the clear, so the old form
    // stays recognised until an explicit migration retires it.
    //
    // The opening line may carry a label (`:::secret AWS root`) — the editor
    // and the client-side encryption both accept one, so the stripper must
    // too, or a labelled block would cross a trust boundary in the clear.
    private static readonly Regex Block = new(
        @":{2,3}secret(?:[ \t][^\r\n]*)?\r?\n.*?\r?\n\s*:{2,3}end",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Post-encryption marker form (Phase 3): `:::secret#N:::end` on a single
    // line, where N is the ciphertext index in content_secret. These markers
    // contain no secret data themselves, but leaking them reveals which
    // notes contain secrets and how many — strip anyway, same placeholder.
    private static readonly Regex Marker = new(
        @":{2,3}secret#\d+:{2,3}end",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string Placeholder = "[secret content hidden]";

    // True when the text holds a secret in either form — an inline block or
    // an encrypted marker. Used to keep secrets out of contexts that can't
    // hold them (spaces, until they get a shared vault).
    public static bool ContainsSecret(string? content)
        => !string.IsNullOrEmpty(content) && (Block.IsMatch(content) || Marker.IsMatch(content));

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
