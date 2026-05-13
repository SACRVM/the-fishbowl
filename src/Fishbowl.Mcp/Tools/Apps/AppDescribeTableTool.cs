using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

public class AppDescribeTableTool : IMcpTool
{
    private readonly IAppSchemaRepository _schema;
    public AppDescribeTableTool(IAppSchemaRepository schema) { _schema = schema; }

    public string Name => "app_describe_table";
    public string Description =>
        "Returns the column list for an owner-defined table, base columns included.";
    public string RequiredScope => ScopeCatalog.AppAdmin;
    public object InputSchema => new
    {
        type = "object",
        properties = new { table = new { type = "string" } },
        required = new[] { "table" },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var appRef = AppToolHelpers.ResolveAppRef(ctx, principal);
        var name = AppToolHelpers.RequireString(arguments, "table");
        var desc = await _schema.DescribeTableAsync(appRef, name, ct);
        if (desc is null) return new { found = false };

        return new
        {
            found = true,
            table = desc.Name,
            columns = desc.Columns.Select(c => new
            {
                name = c.Name,
                type = c.Type.ToWireName(),
                nullable = c.Nullable,
                unique = c.IsUnique,
                isBase = c.IsBaseColumn,
                @default = c.DefaultValue,
            }),
        };
    }
}
