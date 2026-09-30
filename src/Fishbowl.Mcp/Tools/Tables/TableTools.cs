using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Tables;

namespace Fishbowl.Mcp.Tools.Tables;

// The space's own tables over MCP (space-apps spec, phase 3) — the same
// repository as REST. A space key's context is its space; the key owner's
// role there decides what they may do (Member writes rows, Designer changes
// tables), on top of the key's read:/write:/design:tables scope. Refusals
// are TableExceptions → InvalidParams with a message the agent can act on.
public abstract class TableToolBase : IMcpTool
{
    protected readonly ITableRepository Tables;
    private readonly ISpaceRepository _spaces;

    protected TableToolBase(ITableRepository tables, ISpaceRepository spaces)
    {
        Tables = tables;
        _spaces = spaces;
    }

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string RequiredScope { get; }
    public abstract object InputSchema { get; }

    public async Task<object> InvokeAsync(ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (ctx.Type != ContextType.Space)
            throw new TableException("tables_space_only", "Tables live in spaces — use a key made for a space.");
        var role = await _spaces.GetMembershipAsync(ctx.Id, actor, ct)
            ?? throw new TableException("not_member", "This key's owner is no longer a member of the space.");
        return await RunAsync(ctx, new TableActor(actor, role.CanWrite(), role.CanDesign()), arguments, ct);
    }

    protected abstract Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct);

    protected static string Table(JsonElement args) => AppJsonParsers.RequireString(args, "table");

    protected static JsonElement Obj(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v : throw new ArgumentException($"`{name}` must be an object.");

    protected static readonly object TableProp = new { type = "string", description = "The table's name." };
    protected static readonly object WhereProp = new
    {
        type = "object",
        description = "MongoDB-style filter: { column: value } or { column: { $eq $ne $lt $lte $gt $gte $in $like $isNull $isNotNull } }, combined with $and $or $not.",
    };

    protected static readonly object ColumnSchema = new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string", description = "lowercase, digits, _" },
            type = new { type = "string", @enum = ColumnKinds.WireNames },
            description = new { type = "string", description = "What the column means — say it; people and agents read it." },
            required = new { type = "boolean" },
            @default = new { description = "Value for new rows (not for link/member/file)." },
            unique = new { type = "boolean" },
            options = new { type = "array", items = new { type = "string" }, description = "choice columns: the allowed values" },
            multiple = new { type = "boolean", description = "choice/link: several values" },
            link = new { type = "string", description = "link columns: a table of this space, or notes, events, todos, contacts" },
            searchable = new { type = "boolean", description = "text/longtext/choice: rows are found by row_search on this column" },
        },
        required = new[] { "name", "type" },
    };
}

public sealed class TableListTool(ITableRepository t, ISpaceRepository s) : TableToolBase(t, s)
{
    public override string Name => "table_list";
    public override string Description => "Lists this space's own tables: name, description, row count. Start here, then table_describe one.";
    public override string RequiredScope => ScopeCatalog.ReadTables;
    public override object InputSchema => new { type = "object", properties = new { } };

    protected override async Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct)
    {
        var list = new List<object>();
        foreach (var d in await Tables.ListAsync(ctx, ct))
            list.Add(new { name = d.Name, description = d.Description, rows = await Tables.CountRowsAsync(ctx, d.Name, ct), columns = d.Columns.Count });
        return new { tables = list, canWrite = actor.CanWrite, canDesign = actor.CanDesign };
    }
}

public sealed class TableDescribeTool(ITableRepository t, ISpaceRepository s) : TableToolBase(t, s)
{
    public override string Name => "table_describe";
    public override string Description => "One table as a short markdown block: its columns, types, rules and descriptions.";
    public override string RequiredScope => ScopeCatalog.ReadTables;
    public override object InputSchema => new { type = "object", properties = new { table = TableProp }, required = new[] { "table" } };

    protected override async Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct)
    {
        var name = Table(args);
        var def = await Tables.DescribeAsync(ctx, name, ct)
            ?? throw new TableException("table_missing", $"There is no table '{name}' in this space.");
        return new { markdown = TableJson.Markdown(def, await Tables.CountRowsAsync(ctx, name, ct)), table = TableJson.Describe(def) };
    }
}

public sealed class TableCreateTool(ITableRepository t, ISpaceRepository s) : TableToolBase(t, s)
{
    public override string Name => "table_create";
    public override string Description =>
        "Creates a table in this space (Designer role). Every row has a required title plus your columns. " +
        "Types: text, longtext (markdown), integer, decimal, yesno, date, datetime, choice (options, multiple), member, file, link (link = a table or notes/events/todos/contacts; multiple for n:m). " +
        "Describe each column — people read it. ownRows: members may only change their own rows.";
    public override string RequiredScope => ScopeCatalog.DesignTables;
    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string", description = "lowercase letters, digits, _" },
            description = new { type = "string" },
            ownRows = new { type = "boolean" },
            columns = new { type = "array", items = ColumnSchema },
        },
        required = new[] { "name", "columns" },
    };

    protected override async Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct)
    {
        var def = new TableDef(AppJsonParsers.RequireString(args, "name"), AppJsonParsers.OptionalString(args, "description"),
            AppJsonParsers.OptionalBool(args, "ownRows"),
            args.TryGetProperty("columns", out var c) ? TableJson.ParseColumns(c) : new List<ColumnDef>(), DateTime.UtcNow, null);
        var created = await Tables.CreateAsync(ctx, actor, def, ct);
        return new { markdown = TableJson.Markdown(created, 0) };
    }
}

public sealed class TableAlterTool(ITableRepository t, ISpaceRepository s) : TableToolBase(t, s)
{
    public override string Name => "table_alter";
    public override string Description =>
        "Changes a table (Designer role), all or nothing: description, ownRows, addColumns (a required column on a table with rows needs a default), " +
        "dropColumns (final — the values are gone), renameColumns { old: new }, updateColumns { name: { description, addOptions, required } }.";
    public override string RequiredScope => ScopeCatalog.DesignTables;
    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            table = TableProp,
            description = new { type = "string" },
            ownRows = new { type = "boolean" },
            addColumns = new { type = "array", items = ColumnSchema },
            dropColumns = new { type = "array", items = new { type = "string" } },
            renameColumns = new { type = "object" },
            updateColumns = new { type = "object" },
        },
        required = new[] { "table" },
    };

    protected override async Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct)
    {
        var name = Table(args);
        var def = await Tables.AlterAsync(ctx, actor, name, TableJson.ParseChange(args), ct);
        return new { markdown = TableJson.Markdown(def, await Tables.CountRowsAsync(ctx, name, ct)) };
    }
}

public sealed class TableDropTool(ITableRepository t, ISpaceRepository s) : TableToolBase(t, s)
{
    public override string Name => "table_drop";
    public override string Description => "Moves a whole table with its rows to the space's trash (Designer role). Refused while another table links to it.";
    public override string RequiredScope => ScopeCatalog.DesignTables;
    public override object InputSchema => new { type = "object", properties = new { table = TableProp }, required = new[] { "table" } };

    protected override async Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct)
    {
        await Tables.DropAsync(ctx, actor, Table(args), ct);
        return new { dropped = true, restore = "From the Trash app, within the retention period." };
    }
}

public sealed class RowInsertTool(ITableRepository t, ISpaceRepository s) : TableToolBase(t, s)
{
    public override string Name => "row_insert";
    public override string Description => "Adds a row: { title, <column>: value, … }. Links take row ids (a list when multiple); dates YYYY-MM-DD; datetimes ISO 8601.";
    public override string RequiredScope => ScopeCatalog.WriteTables;
    public override object InputSchema => new { type = "object", properties = new { table = TableProp, values = new { type = "object" } }, required = new[] { "table", "values" } };

    protected override async Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct) =>
        await Tables.InsertAsync(ctx, actor, Table(args), Obj(args, "values"), ct);
}

public sealed class RowGetTool(ITableRepository t, ISpaceRepository s) : TableToolBase(t, s)
{
    public override string Name => "row_get";
    public override string Description => "One row by id.";
    public override string RequiredScope => ScopeCatalog.ReadTables;
    public override object InputSchema => new { type = "object", properties = new { table = TableProp, id = new { type = "string" } }, required = new[] { "table", "id" } };

    protected override async Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct)
    {
        var id = AppJsonParsers.RequireString(args, "id");
        return await Tables.GetAsync(ctx, Table(args), id, ct)
            ?? throw new TableException("row_missing", $"No row '{id}'.");
    }
}

public sealed class RowUpdateTool(ITableRepository t, ISpaceRepository s) : TableToolBase(t, s)
{
    public override string Name => "row_update";
    public override string Description => "Changes some of a row's values. Pass rowVersion from your last read to avoid overwriting someone else's change.";
    public override string RequiredScope => ScopeCatalog.WriteTables;
    public override object InputSchema => new
    {
        type = "object",
        properties = new { table = TableProp, id = new { type = "string" }, values = new { type = "object" }, rowVersion = new { type = "integer" } },
        required = new[] { "table", "id", "values" },
    };

    protected override async Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct) =>
        await Tables.UpdateAsync(ctx, actor, Table(args), AppJsonParsers.RequireString(args, "id"), Obj(args, "values"),
            AppJsonParsers.OptionalInt(args, "rowVersion"), ct);
}

public sealed class RowDeleteTool(ITableRepository t, ISpaceRepository s) : TableToolBase(t, s)
{
    public override string Name => "row_delete";
    public override string Description => "Moves a row to the space's trash. Refused while another row links to it — delete in the right order.";
    public override string RequiredScope => ScopeCatalog.WriteTables;
    public override object InputSchema => new { type = "object", properties = new { table = TableProp, id = new { type = "string" } }, required = new[] { "table", "id" } };

    protected override async Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct)
    {
        await Tables.DeleteAsync(ctx, actor, Table(args), AppJsonParsers.RequireString(args, "id"), ct);
        return new { deleted = true };
    }
}

public sealed class RowQueryTool(ITableRepository t, ISpaceRepository s) : TableToolBase(t, s)
{
    public override string Name => "row_query";
    public override string Description => "Rows matching a filter, with orderBy [{ field, direction }], limit (≤ 500, default 100) and offset.";
    public override string RequiredScope => ScopeCatalog.ReadTables;
    public override object InputSchema => new
    {
        type = "object",
        properties = new { table = TableProp, where = WhereProp, orderBy = new { type = "array" }, limit = new { type = "integer" }, offset = new { type = "integer" } },
        required = new[] { "table" },
    };

    protected override async Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct) =>
        new { rows = await Tables.QueryAsync(ctx, Table(args), AppJsonParsers.ParseQuerySpec(args), ct) };
}

public sealed class RowCountTool(ITableRepository t, ISpaceRepository s) : TableToolBase(t, s)
{
    public override string Name => "row_count";
    public override string Description => "How many rows match a filter.";
    public override string RequiredScope => ScopeCatalog.ReadTables;
    public override object InputSchema => new { type = "object", properties = new { table = TableProp, where = WhereProp }, required = new[] { "table" } };

    protected override async Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct) =>
        new { count = await Tables.CountAsync(ctx, Table(args), AppJsonParsers.ParseQuerySpec(args), ct) };
}

public sealed class RowSearchTool(ITableRepository t, ISpaceRepository s) : TableToolBase(t, s)
{
    public override string Name => "row_search";
    public override string Description =>
        "Finds rows by meaning and words across the space's tables (or one table) — only tables with searchable columns take part (title + those columns). " +
        "degraded: true means the embedding model isn't ready and only words matched.";
    public override string RequiredScope => ScopeCatalog.ReadTables;
    public override object InputSchema => new
    {
        type = "object",
        properties = new { query = new { type = "string" }, table = new { type = "string", description = "Optional: search one table." }, limit = new { type = "integer" } },
        required = new[] { "query" },
    };

    protected override async Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct)
    {
        var (hits, degraded) = await Tables.SearchAsync(ctx, AppJsonParsers.RequireString(args, "query"),
            AppJsonParsers.OptionalString(args, "table"), AppJsonParsers.OptionalInt(args, "limit") ?? 10, ct);
        return new { hits = hits.Select(h => new { table = h.Table, row = h.Row, score = h.Score }), degraded };
    }
}

public sealed class RowAggregateTool(ITableRepository t, ISpaceRepository s) : TableToolBase(t, s)
{
    public override string Name => "row_aggregate";
    public override string Description => "count / sum / min / max / avg over a column, optionally grouped by one column and filtered.";
    public override string RequiredScope => ScopeCatalog.ReadTables;
    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            table = TableProp,
            fn = new { type = "string", @enum = new[] { "count", "sum", "min", "max", "avg" } },
            column = new { type = "string" },
            groupBy = new { type = "string" },
            where = WhereProp,
        },
        required = new[] { "table", "fn" },
    };

    protected override async Task<object> RunAsync(ContextRef ctx, TableActor actor, JsonElement args, CancellationToken ct)
    {
        JsonElement? where = args.TryGetProperty("where", out var w) && w.ValueKind == JsonValueKind.Object ? w : null;
        var result = await Tables.AggregateAsync(ctx, Table(args), AppJsonParsers.RequireString(args, "fn"),
            AppJsonParsers.OptionalString(args, "column"), AppJsonParsers.OptionalString(args, "groupBy"), where, ct);
        return new { results = result.Select(r => new { group = r.Group, value = r.Value }) };
    }
}
