using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;

namespace Fishbowl.Mcp.Tools;

public class GetMemoryTool : IMcpTool
{
    private readonly INoteRepository _notes;

    public GetMemoryTool(INoteRepository notes) { _notes = notes; }

    public string Name => "get_memory";
    public string Description => "Fetch a single note by `id` or by its `slug`. Secrets stripped on the way out.";
    public string RequiredScope => "read:notes";
    public object InputSchema => new
    {
        type = "object",
        properties = new { id = new { type = "string" }, slug = new { type = "string" } },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var note = await NoteArgs.FindAsync(_notes, ctx, arguments, "slug", ct);
        if (note is null) return new { found = false };

        return new { found = true, note = SecretStripper.StripNote(note) };
    }
}
