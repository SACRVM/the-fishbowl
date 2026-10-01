using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;

namespace Fishbowl.Mcp.Tools;

// Partial-update: only the fields present in the args replace what's stored.
// Missing fields leave existing values intact — MCP clients frequently send
// just `{ id, content }` without re-echoing the full note.
public class UpdateMemoryTool : IMcpTool
{
    private readonly INoteRepository _notes;

    public UpdateMemoryTool(INoteRepository notes) { _notes = notes; }

    public string Name => "update_memory";
    public string Description => "Update an existing note. Omitted fields are preserved.";
    public string RequiredScope => "write:notes";
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            id = new { type = "string" },
            title = new { type = "string" },
            content = new { type = "string" },
            tags = new { type = "array", items = new { type = "string" } },
        },
        required = new[] { "id" },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = arguments.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id is required", nameof(arguments));

        var existing = await _notes.GetByIdAsync(ctx, id!, ct);
        if (existing is null) return new { updated = false };

        if (arguments.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
            existing.Title = t.GetString() ?? existing.Title;
        if (arguments.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
        {
            // The agent only ever saw "[secret content hidden]": writing its text
            // back would drop the secret's marker and orphan the ciphertext.
            if (SecretStripper.ContainsSecret(existing.Content) || existing.ContentSecret is { Length: > 0 })
                throw new ArgumentException("This note holds secrets — its text can only be changed in Fishbowl. Title and tags can be changed here.");
            existing.Content = c.GetString();
        }
        if (arguments.TryGetProperty("tags", out var g) && g.ValueKind == JsonValueKind.Array)
        {
            existing.Tags = g.EnumerateArray().Select(e => e.GetString() ?? "")
                .Where(s => !string.IsNullOrEmpty(s)).ToList();
        }

        var ok = await _notes.UpdateAsync(ctx, existing, NoteSource.Mcp, ct);
        return new { updated = ok, note = SecretStripper.StripNote(existing) };
    }
}
