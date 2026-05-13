using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

// Composite tool that branches on `operation`. Add/rename/drop are all
// "ALTER TABLE …" in SQL; presenting them under one tool name keeps the
// MCP surface compact for agents that don't need separate vocab.
public class AppAlterTableTool : IMcpTool
{
    private readonly IAppSchemaRepository _schema;
    public AppAlterTableTool(IAppSchemaRepository schema) { _schema = schema; }

    public string Name => "app_alter_table";
    public string Description =>
        "Alters an owner-defined table: add_column (always nullable in MVP), rename_column, drop_column. Base columns are protected.";
    public string RequiredScope => ScopeCatalog.AppAdmin;
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            table = new { type = "string" },
            operation = new
            {
                type = "string",
                description = "add_column | rename_column | drop_column",
            },
            // add_column
            column = new
            {
                type = "object",
                description = "For add_column. { name, type, nullable?, default? }",
            },
            // rename_column
            from = new { type = "string", description = "For rename_column." },
            to = new { type = "string", description = "For rename_column." },
            // drop_column
            name = new { type = "string", description = "For drop_column." },
        },
        required = new[] { "table", "operation" },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var appRef = AppToolHelpers.ResolveAppRef(ctx, principal);
        var table = AppToolHelpers.RequireString(arguments, "table");
        var op = AppToolHelpers.RequireString(arguments, "operation").ToLowerInvariant();

        switch (op)
        {
            case "add_column":
                {
                    if (!arguments.TryGetProperty("column", out var col)
                        || col.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("add_column requires 'column' object", nameof(arguments));
                    var c = ParseSingleColumn(col);
                    await _schema.AddColumnAsync(appRef, table, c, ct);
                    return new { altered = true, operation = op, column = c.Name };
                }
            case "rename_column":
                {
                    var from = AppToolHelpers.RequireString(arguments, "from");
                    var to = AppToolHelpers.RequireString(arguments, "to");
                    await _schema.RenameColumnAsync(appRef, table, from, to, ct);
                    return new { altered = true, operation = op, from, to };
                }
            case "drop_column":
                {
                    var n = AppToolHelpers.RequireString(arguments, "name");
                    await _schema.DropColumnAsync(appRef, table, n, ct);
                    return new { altered = true, operation = op, column = n };
                }
            default:
                throw new ArgumentException(
                    $"unknown operation '{op}'. Use add_column | rename_column | drop_column.",
                    nameof(arguments));
        }
    }

    private static AppColumn ParseSingleColumn(JsonElement col)
    {
        var name = AppToolHelpers.RequireString(col, "name");
        var typeRaw = AppToolHelpers.RequireString(col, "type");
        var type = AppColumnTypeExtensions.Parse(typeRaw);
        var nullable = AppToolHelpers.OptionalBool(col, "nullable", fallback: true);
        var unique = AppToolHelpers.OptionalBool(col, "unique", fallback: false);
        object? def = null;
        if (col.TryGetProperty("default", out var defEl) && defEl.ValueKind != JsonValueKind.Null)
            def = AppToolHelpers.ToClrValue(defEl);
        return new AppColumn(name, type, nullable, def, unique);
    }
}
