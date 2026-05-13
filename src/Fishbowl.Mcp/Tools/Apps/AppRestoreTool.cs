using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

public class AppRestoreTool : IMcpTool
{
    private readonly IAppRowRepository _rows;
    public AppRestoreTool(IAppRowRepository rows) { _rows = rows; }

    public string Name => "app_restore";
    public string Description =>
        "Restores a soft-deleted row (is_deleted=0). No-ops on rows that are already active.";
    public string RequiredScope => ScopeCatalog.AppWrite;
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            table = new { type = "string" },
            id = new { type = "string" },
        },
        required = new[] { "table", "id" },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var appRef = AppToolHelpers.ResolveAppRef(ctx, principal);
        var table = AppToolHelpers.RequireString(arguments, "table");
        var id = AppToolHelpers.RequireString(arguments, "id");
        var ok = await _rows.RestoreAsync(appRef, table, id,
            actorId: string.IsNullOrEmpty(actor) ? null : actor, ct);
        return new { restored = ok };
    }
}
