using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

public class AppCreateTableTool : IMcpTool
{
    private readonly IAppSchemaRepository _schema;
    public AppCreateTableTool(IAppSchemaRepository schema) { _schema = schema; }

    public string Name => "app_create_table";
    public string Description =>
        "Creates an owner-defined table in the current app. Base columns (id, title, author, created_at, last_modified, row_version, is_deleted, deleted_at, additional_data) are server-injected.";
    public string RequiredScope => ScopeCatalog.AppAdmin;
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            table = new { type = "string" },
            columns = new
            {
                type = "array",
                description = "User-defined columns. Server adds base columns automatically.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string" },
                        type = new { type = "string", description = "text | integer | real | boolean | datetime | json" },
                        nullable = new { type = "boolean", @default = true },
                        unique = new { type = "boolean", @default = false },
                        @default = new { description = "Optional default value matching the declared type." },
                    },
                    required = new[] { "name", "type" },
                },
            },
        },
        required = new[] { "table", "columns" },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var appRef = AppToolHelpers.ResolveAppRef(ctx, principal);
        var name = AppToolHelpers.RequireString(arguments, "table");
        var cols = AppToolHelpers.ParseColumns(arguments, "columns");
        await _schema.CreateTableAsync(appRef, name, cols, ct);
        return new { created = true, table = name, userColumnCount = cols.Count };
    }
}
