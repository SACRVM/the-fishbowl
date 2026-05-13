using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

public class AppDeleteTool : IMcpTool
{
    private readonly IAppRowRepository _rows;
    public AppDeleteTool(IAppRowRepository rows) { _rows = rows; }

    public string Name => "app_delete";
    public string Description =>
        "Deletes a row. Soft-delete by default (is_deleted=1, restorable via app_restore); hard=true removes the row outright.";
    public string RequiredScope => ScopeCatalog.AppWrite;
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            table = new { type = "string" },
            id = new { type = "string" },
            hard = new { type = "boolean", @default = false },
        },
        required = new[] { "table", "id" },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var appRef = AppToolHelpers.ResolveAppRef(ctx, principal);
        var table = AppToolHelpers.RequireString(arguments, "table");
        var id = AppToolHelpers.RequireString(arguments, "id");
        var hard = AppToolHelpers.OptionalBool(arguments, "hard");
        var ok = await _rows.DeleteAsync(appRef, table, id, hard,
            actorId: string.IsNullOrEmpty(actor) ? null : actor, ct);
        return new { deleted = ok, hard };
    }
}
