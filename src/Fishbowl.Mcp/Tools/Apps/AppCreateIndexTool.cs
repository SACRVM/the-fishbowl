using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

public class AppCreateIndexTool : IMcpTool
{
    private readonly IAppSchemaRepository _schema;
    public AppCreateIndexTool(IAppSchemaRepository schema) { _schema = schema; }

    public string Name => "app_create_index";
    public string Description =>
        "Creates a single-column index on an owner-defined column. Base columns already have server-managed indexes.";
    public string RequiredScope => ScopeCatalog.AppAdmin;
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            table = new { type = "string" },
            column = new { type = "string" },
        },
        required = new[] { "table", "column" },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var appRef = AppToolHelpers.ResolveAppRef(ctx, principal);
        var table = AppToolHelpers.RequireString(arguments, "table");
        var column = AppToolHelpers.RequireString(arguments, "column");
        await _schema.CreateIndexAsync(appRef, table, column, ct);
        return new { indexed = true, table, column };
    }
}
