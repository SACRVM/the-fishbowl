using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

public class AppUpdateTool : IMcpTool
{
    private readonly IAppRowRepository _rows;
    public AppUpdateTool(IAppRowRepository rows) { _rows = rows; }

    public string Name => "app_update";
    public string Description =>
        "PATCH-updates a row by id. Server bumps row_version + last_modified; caller cannot set id, author, audit, or delete columns. Soft-deleted rows are skipped — call app_restore first.";
    public string RequiredScope => ScopeCatalog.AppWrite;
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            table = new { type = "string" },
            id = new { type = "string" },
            patch = new { type = "object" },
        },
        required = new[] { "table", "id", "patch" },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var appRef = AppToolHelpers.ResolveAppRef(ctx, principal);
        var table = AppToolHelpers.RequireString(arguments, "table");
        var id = AppToolHelpers.RequireString(arguments, "id");
        var patch = AppToolHelpers.ParseObject(arguments, "patch");
        var row = await _rows.UpdateAsync(appRef, table, id, patch,
            actorId: string.IsNullOrEmpty(actor) ? null : actor, ct);
        return new { updated = true, row };
    }
}
