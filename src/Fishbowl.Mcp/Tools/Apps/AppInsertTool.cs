using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

public class AppInsertTool : IMcpTool
{
    private readonly IAppRowRepository _rows;
    public AppInsertTool(IAppRowRepository rows) { _rows = rows; }

    public string Name => "app_insert";
    public string Description =>
        "Inserts a row into an owner-defined table. Server fills id/created_at/last_modified/row_version/author; caller supplies title (required) + user columns + optional additional_data (JSON string, ≤ 256 KB).";
    public string RequiredScope => ScopeCatalog.AppWrite;
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            table = new { type = "string" },
            row = new { type = "object", description = "Field/value map; title is required." },
        },
        required = new[] { "table", "row" },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var appRef = AppToolHelpers.ResolveAppRef(ctx, principal);
        var table = AppToolHelpers.RequireString(arguments, "table");
        var fields = AppToolHelpers.ParseObject(arguments, "row");
        var row = await _rows.InsertAsync(appRef, table, fields,
            actorId: string.IsNullOrEmpty(actor) ? null : actor, ct);
        return new { inserted = true, row };
    }
}
