using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

public class AppCountTool : IMcpTool
{
    private readonly IAppRowRepository _rows;
    public AppCountTool(IAppRowRepository rows) { _rows = rows; }

    public string Name => "app_count";
    public string Description =>
        "Counts rows matching a MongoDB-style filter. Same operator + cap rules as app_query.";
    public string RequiredScope => ScopeCatalog.AppRead;
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            table = new { type = "string" },
            where = new { type = "object" },
            includeDeleted = new { type = "boolean", @default = false },
        },
        required = new[] { "table" },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var appRef = AppToolHelpers.ResolveAppRef(ctx, principal);
        var table = AppToolHelpers.RequireString(arguments, "table");
        var spec = AppToolHelpers.ParseQuerySpec(arguments);
        var n = await _rows.CountAsync(appRef, table, spec, ct);
        return new { count = n };
    }
}
