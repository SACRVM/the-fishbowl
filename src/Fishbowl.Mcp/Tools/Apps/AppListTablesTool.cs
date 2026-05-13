using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

public class AppListTablesTool : IMcpTool
{
    private readonly IAppSchemaRepository _schema;
    public AppListTablesTool(IAppSchemaRepository schema) { _schema = schema; }

    public string Name => "app_list_tables";
    public string Description => "Lists the owner-defined tables in the current app DB.";
    public string RequiredScope => ScopeCatalog.AppAdmin;
    public object InputSchema => new { type = "object", properties = new { } };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var appRef = AppToolHelpers.ResolveAppRef(ctx, principal);
        var tables = await _schema.ListTablesAsync(appRef, ct);
        return new { tables };
    }
}
