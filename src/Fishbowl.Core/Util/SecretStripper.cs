using System.Text.RegularExpressions;
using Fishbowl.Core.Models;

namespace Fishbowl.Core.Util;

// Removes `:::secret ... :::end` blocks and `content_secret` blobs from any
// note that's about to cross a trust boundary (MCP response, embeddings
// input, search snippet). CONCEPT § Core Philosophy § MCP Server makes
// this non-negotiable — secrets are human-access only.
public static class SecretStripper
{
    // One grammar with the editor (vendored sac-md-secret.js) and the
    // client-side encryption (api.js): a block OPENS on a line `:::secret`
    // (or the old two-colon `::secret`, optional label) and CLOSES on a line
    // that is `:::end` / `::end`, optionally followed by whitespace and text;
    // a block without a closer runs to the end of the note.
    //
    // The two rules lean opposite ways on purpose: opening is generous (any
    // case, indented — we'd rather hide too much), closing is strict (exactly
    // what the editor treats as the end, never e.g. `::endpoint=` or an
    // indented `:::end`) — closing earlier than the editor would put the rest
    // of what it shows as secret in the clear.
    private static readonly Regex Open = new(
        @"^[ \t]*:{2,3}secret(?:[ \t].*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Close = new(
        @"^:{2,3}end(?:\s.*)?$", RegexOptions.Compiled);

    // The secret blocks of a text as line ranges [open, close] (close = the
    // last line when the block isn't closed).
    private static List<(int Open, int Close)> Blocks(string[] lines)
    {
        var blocks = new List<(int, int)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Open.IsMatch(lines[i].TrimEnd('\r'))) continue;
            var end = i + 1;
            while (end < lines.Length && !Close.IsMatch(lines[end].TrimEnd('\r'))) end++;
            if (end >= lines.Length) end = lines.Length - 1;
            blocks.Add((i, end));
            i = end;
        }
        return blocks;
    }

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
        => !string.IsNullOrEmpty(content) && (Blocks(content.Split('\n')).Count > 0 || Marker.IsMatch(content));

    public static string? Strip(string? content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        var lines = content.Split('\n');
        var blocks = Blocks(lines);
        if (blocks.Count > 0)
        {
            var kept = new List<string>(lines.Length);
            var b = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                if (b < blocks.Count && i == blocks[b].Open)
                {
                    kept.Add(Placeholder);
                    i = blocks[b++].Close;
                    continue;
                }
                kept.Add(lines[i]);
            }
            content = string.Join('\n', kept);
        }
        return Marker.Replace(content, Placeholder);
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
