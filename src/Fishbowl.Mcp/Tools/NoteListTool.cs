using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;

namespace Fishbowl.Mcp.Tools;

// Every note of the workspace in the order it last changed, for an agent that
// keeps a copy in sync (a Git mirror of a space's notes): page through with
// the cursor, next time ask only for what changed after the newest updatedAt
// it has. Light by default — id, title, tags, times, pinned, archived — the
// content only when asked for, secret-stripped like every note answer.
public class NoteListTool : IMcpTool
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    private readonly INoteRepository _notes;
    private readonly ITrashRepository _trash;

    public NoteListTool(INoteRepository notes, ITrashRepository trash)
    {
        _notes = notes;
        _trash = trash;
    }

    public string Name => "note_list";
    public string Description =>
        "Lists every note of the workspace in the order it last changed (oldest change first), for keeping a copy in sync. " +
        "Page with `cursor` (the previous answer's `nextCursor`; the last page has none). Next time pass `updated_since` = the newest " +
        "`updatedAt` you hold to get only what changed after it; the first page then also lists `deleted`: notes moved to the trash " +
        "since (whatever their tags). A note restored from the trash counts as changed. A full listing without `updated_since` is the " +
        "complete set — anything you hold that it lacks is gone. Light by default (id, slug, title, tags, createdAt, updatedAt, pinned, " +
        "archived, locked); `include_content: true` adds the markdown, with secret blocks removed.";
    public string RequiredScope => "read:notes";
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            tags = new { type = "array", items = new { type = "string" }, description = "Only notes with these tags" },
            match = new { type = "string", @enum = new[] { "any", "all" }, @default = "any", description = "Any of the tags, or all of them" },
            updated_since = new { type = "string", description = "ISO-8601 time: only notes changed after it (and `deleted` since it)" },
            limit = new { type = "integer", @default = DefaultLimit, minimum = 1, maximum = MaxLimit },
            cursor = new { type = "string", description = "`nextCursor` of the previous page" },
            include_content = new { type = "boolean", @default = false, description = "Also return each note's markdown" },
        },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var limit = arguments.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? Math.Clamp(McpArgs.Int(l, "limit"), 1, MaxLimit) : DefaultLimit;

        IReadOnlyCollection<string>? tags = null;
        if (arguments.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            tags = t.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new ArgumentException("`tags` must be a list of strings."))
                .Where(s => s.Length > 0)
                .ToList();
            if (tags.Count == 0) tags = null;
        }

        var match = "any";
        if (arguments.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.String)
            match = m.GetString() switch
            {
                "any" => "any",
                "all" => "all",
                _ => throw new ArgumentException("`match` must be \"any\" or \"all\"."),
            };

        DateTime? since = null;
        if (arguments.TryGetProperty("updated_since", out var s) && s.ValueKind == JsonValueKind.String)
        {
            if (!DateTimeOffset.TryParse(s.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                throw new ArgumentException("`updated_since` must be an ISO-8601 time, e.g. 2026-11-06T12:00:00Z.");
            since = parsed.UtcDateTime;
        }

        string? afterStamp = null, afterId = null;
        var first = true;
        if (arguments.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } cursor)
        {
            (afterStamp, afterId) = ReadCursor(cursor);
            first = false;
        }

        var includeContent = arguments.TryGetProperty("include_content", out var ic) && ic.ValueKind == JsonValueKind.True;

        // One more than the page, to know whether another follows.
        var rows = await _notes.ListChangesAsync(ctx, new NoteChangeQuery(tags, match, since, afterStamp, afterId, limit + 1), ct);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? WriteCursor(page[^1]) : null;

        var notes = page.Select(r =>
        {
            var n = SecretStripper.StripNote(r.Note);
            // UTC on the wire ("…Z"): an updatedAt passed back as updated_since compares exactly.
            var createdAt = n.CreatedAt.ToUniversalTime();
            var updatedAt = n.UpdatedAt.ToUniversalTime();
            return includeContent
                ? (object)new { n.Id, n.Slug, n.Title, n.Tags, createdAt, updatedAt, n.Pinned, n.Archived, n.Locked, n.Content }
                : new { n.Id, n.Slug, n.Title, n.Tags, createdAt, updatedAt, n.Pinned, n.Archived, n.Locked };
        }).ToList();

        // What went to the trash since, on the first page only (each once).
        object? deleted = null;
        if (since is { } from && first)
        {
            deleted = (await _trash.ListAsync(ctx, ct))
                .Where(i => i.Kind == TrashKinds.Note && i.DeletedAt.ToUniversalTime() > from)
                .OrderBy(i => i.DeletedAt)
                .Select(i => new { id = i.ItemId, title = i.Title, deletedAt = i.DeletedAt.ToUniversalTime() })
                .ToList();
        }

        return new { notes, count = notes.Count, nextCursor = next, deleted };
    }

    // The keyset of the page's last note — its stored updated_at and its id —
    // as opaque text, so a caller can't lean on its shape.
    private static string WriteCursor(NoteChange last) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(last.Stamp + "|" + last.Note.Id))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (string Stamp, string Id) ReadCursor(string cursor)
    {
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var bar = text.LastIndexOf('|');
            if (bar > 0 && bar < text.Length - 1) return (text[..bar], text[(bar + 1)..]);
        }
        catch (FormatException) { }
        throw new ArgumentException("`cursor` is not one this tool gave — pass `nextCursor` as it came.");
    }
}
