using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;

namespace Fishbowl.Mcp.Tools;

// Partial update, applied in one transaction against what is stored
// (INoteRepository.PatchAsync): only the fields given change. Tags are
// replaced (`tags`) or changed in place (`add_tags` / `remove_tags`) — no
// read-modify-write race, the other tags stay. A locked note keeps its title
// and text; `append` adds to its end. An agent may lock a note, never unlock it.
public class UpdateMemoryTool : IMcpTool
{
    private readonly INoteRepository _notes;

    public UpdateMemoryTool(INoteRepository notes) { _notes = notes; }

    public string Name => "update_memory";
    public string Description =>
        "Update an existing note, found by `id` or `by_slug`. Omitted fields are preserved. `tags` replaces the tag list; " +
        "`add_tags` / `remove_tags` change it in place (atomic — use these for a status change). `append` adds text at the end — " +
        "the one text change a locked note takes. `slug` sets the note's stable key (\"\" clears it). `lock: true` locks the note " +
        "(its title and text no longer change); only a person in Fishbowl can unlock it. Every change marks the note review:pending.";
    public string RequiredScope => "write:notes";
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            id = new { type = "string" },
            by_slug = new { type = "string", description = "Find the note by its slug instead of `id`" },
            title = new { type = "string" },
            content = new { type = "string" },
            append = new { type = "string", description = "Text to add at the end of the note" },
            tags = new { type = "array", items = new { type = "string" }, description = "Replaces the tag list" },
            add_tags = new { type = "array", items = new { type = "string" } },
            remove_tags = new { type = "array", items = new { type = "string" } },
            slug = new { type = "string", description = "The note's stable external key; \"\" clears it" },
            @lock = new { type = "boolean", description = "true locks the note" },
        },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var existing = await NoteArgs.FindAsync(_notes, ctx, arguments, "by_slug", ct);
        if (existing is null) return new { updated = false };

        var patch = new NotePatch
        {
            Title = McpArgs.String(arguments, "title"),
            Content = McpArgs.String(arguments, "content"),
            Append = McpArgs.String(arguments, "append"),
            Tags = McpArgs.Strings(arguments, "tags"),
            AddTags = McpArgs.Strings(arguments, "add_tags"),
            RemoveTags = McpArgs.Strings(arguments, "remove_tags"),
            Slug = McpArgs.String(arguments, "slug"),
        };
        if (McpArgs.Bool(arguments, "lock") is { } lockIt)
        {
            if (!lockIt) throw new ArgumentException("An agent can lock a note but not unlock it — a person does that in Fishbowl.");
            patch.Locked = true;
        }
        // The agent only ever saw "[secret content hidden]": writing its text
        // back would drop the secret's marker and orphan the ciphertext.
        if (patch.Content is not null && (SecretStripper.ContainsSecret(existing.Content) || existing.ContentSecret is { Length: > 0 }))
            throw new ArgumentException("This note holds secrets — its text can only be changed in Fishbowl. Title and tags can be changed here.");

        var note = await _notes.PatchAsync(ctx, existing.Id, patch, NoteSource.Mcp, actor, ct);
        return note is null ? new { updated = false } : new { updated = true, note = SecretStripper.StripNote(note) };
    }
}

// A note named by `id` or by its slug.
internal static class NoteArgs
{
    public static async Task<Note?> FindAsync(INoteRepository notes, ContextRef ctx, JsonElement args, string slugArg, CancellationToken ct)
    {
        var id = McpArgs.String(args, "id");
        var slug = McpArgs.String(args, slugArg);
        if (!string.IsNullOrWhiteSpace(id)) return await notes.GetByIdAsync(ctx, id, ct);
        if (!string.IsNullOrWhiteSpace(slug)) return await notes.GetBySlugAsync(ctx, slug, ct);
        throw new ArgumentException($"`id` or `{slugArg}` is required.");
    }
}
