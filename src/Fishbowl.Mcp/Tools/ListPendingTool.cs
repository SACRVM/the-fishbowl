using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;

namespace Fishbowl.Mcp.Tools;

// Convenience wrapper around "give me every note tagged review:pending".
// Useful for Claude Code to self-inspect — "what have I written that hasn't
// been reviewed yet?".
public class ListPendingTool : IMcpTool
{
    private readonly INoteRepository _notes;

    public ListPendingTool(INoteRepository notes) { _notes = notes; }

    public string Name => "list_pending";
    public string Description =>
        "Lists notes tagged review:pending — typically notes written by this client that the human hasn't approved yet.";
    public string RequiredScope => "read:notes";
    public object InputSchema => new
    {
        type = "object",
        properties = new { limit = new { type = "integer", @default = 50 } },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var limit = arguments.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? Math.Clamp(McpArgs.Int(l, "limit"), 1, 500) : 50;

        var pending = await _notes.GetAllAsync(ctx, new[] { "review:pending" }, match: "all", ct);
        var stripped = pending.Take(limit).Select(SecretStripper.StripNote).ToList();
        return new { notes = stripped, count = stripped.Count };
    }
}
