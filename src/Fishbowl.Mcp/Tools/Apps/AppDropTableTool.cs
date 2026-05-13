using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

public class AppDropTableTool : IMcpTool
{
    private readonly IAppSchemaRepository _schema;
    public AppDropTableTool(IAppSchemaRepository schema) { _schema = schema; }

    public string Name => "app_drop_table";
    public string Description =>
        "Drops an owner-defined table and all its rows. Terminal — pair with a soft-delete dance if recovery matters.";
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
        await _schema.DropTableAsync(appRef, name, ct);
        return new { dropped = true, table = name };
    }
}
