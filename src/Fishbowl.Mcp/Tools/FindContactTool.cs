using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools;

// CONCEPT.md § Contacts — "Find people by what you remember about them,
// not just their name." FTS5 indexes name/email/phone/notes, so a query
// like "venue sound check" will surface the contact with that phrase in
// its notes field, even if the name is unknown. Archived rows are
// excluded — "find me" queries aren't for archaeology.
public class FindContactTool : IMcpTool
{
    private readonly IContactRepository _contacts;

    public FindContactTool(IContactRepository contacts) { _contacts = contacts; }

    public string Name => "find_contact";
    public string Description =>
        "Full-text search across contacts (name/email/phone/notes), ranked by bm25. Excludes archived rows.";
    public string RequiredScope => "read:contacts";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            query = new
            {
                type = "string",
                description = "Free-text search. Tokens prefix-match and are AND-combined.",
            },
            limit = new
            {
                type = "integer",
                @default = 20,
                description = "Maximum number of contacts to return (1–500).",
            },
        },
        required = new[] { "query" },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var query = arguments.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
            ? q.GetString() ?? "" : "";
        var limit = arguments.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? Math.Clamp(McpArgs.Int(l, "limit"), 1, 500) : 20;

        var hits = await _contacts.SearchAsync(ctx, query, limit, ct);
        var rows = hits.Select(c => new
        {
            id = c.Id,
            name = c.Name,
            email = c.Email,
            phone = c.Phone,
            notes = c.Notes,
            updatedAt = c.UpdatedAt,
        }).ToList();

        return new { contacts = rows, count = rows.Count, query };
    }
}
