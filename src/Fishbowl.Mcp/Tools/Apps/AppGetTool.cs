using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

public class AppGetTool : IMcpTool
{
    private readonly IAppRowRepository _rows;
    public AppGetTool(IAppRowRepository rows) { _rows = rows; }

    public string Name => "app_get";
    public string Description =>
        "Fetches a single row by id. Soft-deleted rows excluded unless includeDeleted=true.";
    public string RequiredScope => ScopeCatalog.AppRead;
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            table = new { type = "string" },
            id = new { type = "string" },
            includeDeleted = new { type = "boolean", @default = false },
        },
        required = new[] { "table", "id" },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var appRef = AppToolHelpers.ResolveAppRef(ctx, principal);
        var table = AppToolHelpers.RequireString(arguments, "table");
        var id = AppToolHelpers.RequireString(arguments, "id");
        var includeDeleted = AppToolHelpers.OptionalBool(arguments, "includeDeleted");
        var row = await _rows.GetAsync(appRef, table, id, includeDeleted, ct);
        return row is null ? (object)new { found = false } : new { found = true, row };
    }
}
