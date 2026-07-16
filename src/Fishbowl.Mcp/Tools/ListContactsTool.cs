using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools;

// CONCEPT.md § Contacts — "Find people by what you remember about them,
// not just their name." This tool only lists; richer find-by-fuzzy is a
// separate tool once we have contacts_fts query wiring on the hybrid
// search layer. For now, clients can filter client-side on the returned
// rows — still way better than having no agent-visible contacts at all.
public class ListContactsTool : IMcpTool
{
    private readonly IContactRepository _contacts;

    public ListContactsTool(IContactRepository contacts) { _contacts = contacts; }

    public string Name => "list_contacts";
    public string Description =>
        "Lists contacts in the current context (personal or space). Returns name/email/phone/notes and archived flag.";
    public string RequiredScope => "read:contacts";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            includeArchived = new
            {
                type = "boolean",
                @default = false,
                description = "Include archived contacts in the list.",
            },
            limit = new
            {
                type = "integer",
                @default = 100,
                description = "Maximum number of contacts to return (1–500).",
            },
        },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var includeArchived = arguments.TryGetProperty("includeArchived", out var ia)
                              && ia.ValueKind == JsonValueKind.True;
        var limit = arguments.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? Math.Clamp(l.GetInt32(), 1, 500) : 100;

        var all = await _contacts.GetAllAsync(ctx, includeArchived, ct);
        var rows = all.Take(limit).Select(c => new
        {
            id = c.Id,
            name = c.Name,
            email = c.Email,
            phone = c.Phone,
            notes = c.Notes,
            archived = c.Archived,
            updatedAt = c.UpdatedAt,
        }).ToList();

        return new { contacts = rows, count = rows.Count };
    }
}
