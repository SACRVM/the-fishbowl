using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

public class AppQueryTool : IMcpTool
{
    private readonly IAppRowRepository _rows;
    public AppQueryTool(IAppRowRepository rows) { _rows = rows; }

    public string Name => "app_query";
    public string Description =>
        "Queries rows with a MongoDB-style filter. Operators: $eq $ne $lt $lte $gt $gte $in $like $isNull $isNotNull. Combinators: $and $or $not. Caps: depth ≤ 5, leaves ≤ 100, $in ≤ 50, limit ≤ 500. Filters on additional_data are rejected.";
    public string RequiredScope => ScopeCatalog.AppRead;
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            table = new { type = "string" },
            where = new { type = "object" },
            orderBy = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        field = new { type = "string" },
                        dir = new { type = "string", description = "asc | desc" },
                    },
                    required = new[] { "field" },
                },
            },
            limit = new { type = "integer", @default = 100 },
            offset = new { type = "integer", @default = 0 },
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
        var rows = await _rows.QueryAsync(appRef, table, spec, ct);
        return new { rows, count = rows.Count };
    }
}
