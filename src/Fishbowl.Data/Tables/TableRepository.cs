using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Tables;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Tables;

// A space's own tables (space-apps spec, phase 3). The definition lives in
// db_tables / db_columns (user/space schema v14); the rows in t_<name>, a
// "multiple" link column's targets in t_<table>__<column>. Links are foreign
// keys enforced here: a link must point at something that exists, and
// nothing that is linked to can be deleted (LinkGuard — the built-ins'
// deletes ask it too). Deleting a row or a whole table goes to the trash.
public class TableRepository : ITableRepository
{
    public const int MaxTables = 100;
    public const int MaxColumns = 100;
    public const int MaxTitle = 500;
    public const int MaxText = 2_000;
    public const int MaxLongText = 200_000;
    public const int MaxOptions = 100;
    public const int MaxLinks = 500;
    public const int MaxAdditionalBytes = 256 * 1024;

    private readonly DatabaseFactory _db;
    private readonly ISpaceRepository _spaces;
    private readonly ILogger<TableRepository> _logger;

    public TableRepository(DatabaseFactory db, ISpaceRepository spaces, ILogger<TableRepository>? logger = null)
    {
        _db = db;
        _spaces = spaces;
        _logger = logger ?? NullLogger<TableRepository>.Instance;
    }

    // ────────── Definitions ──────────

    private sealed class TableRow
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public long OwnRows { get; set; }
        public string? CreatedBy { get; set; }
        public string CreatedAt { get; set; } = "";
    }

    private sealed class ColumnRow
    {
        public string TableName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Description { get; set; }
        public long Required { get; set; }
        public string? DefaultValue { get; set; }
        public long IsUnique { get; set; }
        public string? Options { get; set; }
        public long Multiple { get; set; }
        public string? LinkTarget { get; set; }
        public long Position { get; set; }
    }

    private static ColumnDef FromRow(ColumnRow r) => new(
        r.Name,
        ColumnKinds.TryParse(r.Kind) ?? ColumnKind.Text,
        r.Description,
        r.Required != 0,
        r.DefaultValue is null ? null : JsonDocument.Parse(r.DefaultValue).RootElement.Clone(),
        r.IsUnique != 0,
        r.Options is null ? null : JsonSerializer.Deserialize<List<string>>(r.Options),
        r.Multiple != 0,
        r.LinkTarget);

    internal static async Task<TableDef?> LoadAsync(IDbConnection db, IDbTransaction? tx, string name, CancellationToken ct)
    {
        var t = await db.QuerySingleOrDefaultAsync<TableRow>(new CommandDefinition(
            "SELECT * FROM db_tables WHERE name = @name", new { name }, transaction: tx, cancellationToken: ct));
        if (t is null) return null;
        var cols = await db.QueryAsync<ColumnRow>(new CommandDefinition(
            "SELECT * FROM db_columns WHERE table_name = @name ORDER BY position", new { name }, transaction: tx, cancellationToken: ct));
        return new TableDef(t.Name, t.Description, t.OwnRows != 0, cols.Select(FromRow).ToList(),
            DateTime.Parse(t.CreatedAt, null, DateTimeStyles.RoundtripKind), t.CreatedBy);
    }

    private static async Task<TableDef> RequireAsync(IDbConnection db, IDbTransaction? tx, string name, CancellationToken ct) =>
        await LoadAsync(db, tx, name, ct)
        ?? throw TableException.NotFound("table_missing", $"There is no table '{name}' in this space.", new { table = name });

    private static void RequireSpace(ContextRef ctx)
    {
        if (ctx.Type != ContextType.Space)
            throw new TableException("tables_space_only", "Tables live in spaces — the personal workspace has none.");
    }

    private static void RequireDesign(TableActor actor)
    {
        if (!actor.CanDesign)
            throw TableException.Forbidden("role_design", "Changing tables needs the Designer role or above.");
    }

    private static void RequireWrite(TableActor actor)
    {
        if (!actor.CanWrite)
            throw TableException.Forbidden("role_write", "Changing rows needs the Member role or above.");
    }

    public async Task<IReadOnlyList<TableDef>> ListAsync(ContextRef ctx, CancellationToken ct = default)
    {
        RequireSpace(ctx);
        using var db = _db.CreateContextConnection(ctx);
        var names = await db.QueryAsync<string>(new CommandDefinition("SELECT name FROM db_tables ORDER BY name", cancellationToken: ct));
        var list = new List<TableDef>();
        foreach (var n in names) list.Add((await LoadAsync(db, null, n, ct))!);
        return list;
    }

    public async Task<TableDef?> DescribeAsync(ContextRef ctx, string table, CancellationToken ct = default)
    {
        RequireSpace(ctx);
        using var db = _db.CreateContextConnection(ctx);
        return await LoadAsync(db, null, table, ct);
    }

    public async Task<long> CountRowsAsync(ContextRef ctx, string table, CancellationToken ct = default)
    {
        RequireSpace(ctx);
        using var db = _db.CreateContextConnection(ctx);
        await RequireAsync(db, null, table, ct);
        return await db.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT COUNT(*) FROM \"{TableNames.Physical(table)}\"", cancellationToken: ct));
    }

    public async Task<TableDef> CreateAsync(ContextRef ctx, TableActor actor, TableDef def, CancellationToken ct = default)
    {
        RequireSpace(ctx);
        RequireDesign(actor);
        return await _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            await CreateInAsync(db, tx, def with { CreatedBy = actor.UserId, CreatedAt = DateTime.UtcNow }, token);
            _logger.LogInformation("Table created in space {SpaceId}", ctx.Id);
            return (await LoadAsync(db, tx, def.Name, token))!;
        }, ct);
    }

    // Validates and creates a table inside a transaction (also the trash's
    // way back for a dropped table).
    internal static async Task CreateInAsync(IDbConnection db, IDbTransaction tx, TableDef def, CancellationToken ct)
    {
        if (!TableNames.IsValid(def.Name) || TableNames.BuiltIns.Contains(def.Name))
            throw new TableException("table_name", $"'{def.Name}' can't be a table name: lowercase letters, digits and _, starting with a letter, up to 40.", args: new { table = def.Name });
        if (await LoadAsync(db, tx, def.Name, ct) is not null)
            throw TableException.Conflict("table_exists", $"There is already a table '{def.Name}'.", new { table = def.Name });
        if (await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM db_tables", transaction: tx, cancellationToken: ct)) >= MaxTables)
            throw new TableException("table_limit", $"A space holds at most {MaxTables} tables.", args: new { max = MaxTables });
        if (def.Columns.Count > MaxColumns)
            throw new TableException("column_limit", $"A table has at most {MaxColumns} columns.", args: new { max = MaxColumns });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in def.Columns) await ValidateColumnAsync(db, tx, def.Name, c, seen, rowsExist: false, ct);

        var physical = TableNames.Physical(def.Name);
        var cols = string.Concat(def.Columns.Where(c => !(c.Kind == ColumnKind.Link && c.Multiple))
            .Select(c => $", \"{c.Name}\" {c.Kind.SqlType()}"));
        await db.ExecuteAsync(new CommandDefinition($"CREATE TABLE \"{physical}\" ({TableBaseColumns.Ddl}{cols})", transaction: tx, cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition(
            "INSERT INTO db_tables(name, description, own_rows, created_by, created_at) VALUES (@Name, @Description, @own, @CreatedBy, @at)",
            new { def.Name, def.Description, own = def.OwnRows ? 1 : 0, def.CreatedBy, at = def.CreatedAt.ToUniversalTime().ToString("o") },
            transaction: tx, cancellationToken: ct));
        var position = 0;
        foreach (var c in def.Columns) await AddColumnMetaAsync(db, tx, def.Name, c, ++position, ct);
    }

    private static async Task AddColumnMetaAsync(IDbConnection db, IDbTransaction tx, string table, ColumnDef c, int position, CancellationToken ct)
    {
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO db_columns(table_name, name, kind, description, required, default_value, is_unique, options, multiple, link_target, position)
            VALUES (@table, @Name, @kind, @Description, @req, @dflt, @uniq, @opts, @multi, @LinkTarget, @position)",
            new
            {
                table,
                c.Name,
                kind = c.Kind.ToWire(),
                c.Description,
                req = c.Required ? 1 : 0,
                dflt = c.Default?.GetRawText(),
                uniq = c.Unique ? 1 : 0,
                opts = c.Options is null ? null : JsonSerializer.Serialize(c.Options),
                multi = c.Multiple ? 1 : 0,
                c.LinkTarget,
                position,
            }, transaction: tx, cancellationToken: ct));
        await CreateColumnStructuresAsync(db, tx, table, c, ct);
    }

    private static async Task CreateColumnStructuresAsync(IDbConnection db, IDbTransaction tx, string table, ColumnDef c, CancellationToken ct)
    {
        var physical = TableNames.Physical(table);
        if (c.Kind == ColumnKind.Link && c.Multiple)
        {
            var join = TableNames.Join(table, c.Name);
            await db.ExecuteAsync(new CommandDefinition(
                $"CREATE TABLE \"{join}\" (row_id TEXT NOT NULL, target_id TEXT NOT NULL, PRIMARY KEY (row_id, target_id)); " +
                $"CREATE INDEX \"{join}__target\" ON \"{join}\"(target_id);", transaction: tx, cancellationToken: ct));
            return;
        }
        if (c.Unique)
            await db.ExecuteAsync(new CommandDefinition(
                $"CREATE UNIQUE INDEX \"{TableNames.UniqueIndex(table, c.Name)}\" ON \"{physical}\"(\"{c.Name}\")", transaction: tx, cancellationToken: ct));
        else if (c.Kind == ColumnKind.Link)
            await db.ExecuteAsync(new CommandDefinition(
                $"CREATE INDEX \"{TableNames.LinkIndex(table, c.Name)}\" ON \"{physical}\"(\"{c.Name}\")", transaction: tx, cancellationToken: ct));
    }

    private static async Task DropColumnStructuresAsync(IDbConnection db, IDbTransaction tx, string table, ColumnDef c, CancellationToken ct)
    {
        if (c.Kind == ColumnKind.Link && c.Multiple)
        {
            await db.ExecuteAsync(new CommandDefinition($"DROP TABLE IF EXISTS \"{TableNames.Join(table, c.Name)}\"", transaction: tx, cancellationToken: ct));
            return;
        }
        await db.ExecuteAsync(new CommandDefinition(
            $"DROP INDEX IF EXISTS \"{TableNames.UniqueIndex(table, c.Name)}\"; DROP INDEX IF EXISTS \"{TableNames.LinkIndex(table, c.Name)}\";",
            transaction: tx, cancellationToken: ct));
    }

    private static async Task ValidateColumnAsync(
        IDbConnection db, IDbTransaction tx, string table, ColumnDef c, HashSet<string> seen, bool rowsExist, CancellationToken ct)
    {
        object Args(object? more = null) => new { table, column = c.Name };
        if (!TableNames.IsValid(c.Name) || TableBaseColumns.Is(c.Name))
            throw new TableException("column_name", $"'{c.Name}' can't be a column name: lowercase letters, digits and _, starting with a letter, and not a base column (id, title, author, created_at, last_modified, row_version, additional_data).", args: Args());
        if (!seen.Add(c.Name))
            throw TableException.Conflict("column_exists", $"Column '{c.Name}' is there twice.", Args());
        if (c.Kind == ColumnKind.Choice)
        {
            if (c.Options is not { Count: > 0 } || c.Options.Count > MaxOptions || c.Options.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > 100)
                || c.Options.Distinct(StringComparer.Ordinal).Count() != c.Options.Count)
                throw new TableException("column_options", $"Choice column '{c.Name}' needs 1 to {MaxOptions} different options.", args: new { table, column = c.Name, max = MaxOptions });
        }
        else if (c.Options is { Count: > 0 })
            throw new TableException("column_options", $"Only choice columns have options ('{c.Name}').", args: new { table, column = c.Name, max = MaxOptions });
        if (c.Multiple && c.Kind is not (ColumnKind.Choice or ColumnKind.Link))
            throw new TableException("column_multiple", $"Only choice and link columns can hold several values ('{c.Name}').", args: Args());
        if (c.Kind == ColumnKind.Link)
        {
            var target = c.LinkTarget;
            var ok = target is not null && (TableNames.BuiltIns.Contains(target) || target == table || await LoadAsync(db, tx, target, ct) is not null);
            if (!ok)
                throw new TableException("column_link", $"Link column '{c.Name}' needs `link`: a table of this space or notes, events, todos, contacts.", args: Args());
        }
        else if (c.LinkTarget is not null)
            throw new TableException("column_link", $"Only link columns have a `link` target ('{c.Name}').", args: Args());
        if (c.Unique && (c.Multiple || c.Kind is ColumnKind.LongText or ColumnKind.YesNo))
            throw new TableException("column_unique", $"Column '{c.Name}' can't be unique (several values, long text or yes/no).", args: Args());
        if (c.Default is { } d)
        {
            if (c.Kind is ColumnKind.Link or ColumnKind.Member or ColumnKind.File)
                throw new TableException("column_default", $"Link, member and file columns have no default ('{c.Name}').", args: Args());
            CoerceScalar(table, c, d);
            if (c.Unique && rowsExist)
                throw new TableException("column_default", $"A unique column added to a table with rows can't have a default ('{c.Name}').", args: Args());
        }
        if (c.Required && rowsExist && c.Default is null)
            throw new TableException("column_required", $"A required column added to a table with rows needs a default ('{c.Name}').", args: Args());
    }

    public async Task<TableDef> AlterAsync(ContextRef ctx, TableActor actor, string table, TableChange change, CancellationToken ct = default)
    {
        RequireSpace(ctx);
        RequireDesign(actor);
        return await _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var def = await RequireAsync(db, tx, table, token);
            var physical = TableNames.Physical(table);
            var rowsExist = await db.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT COUNT(*) FROM \"{physical}\"", transaction: tx, cancellationToken: token)) > 0;

            if (change.Description is not null || change.OwnRows is not null)
                await db.ExecuteAsync(new CommandDefinition(
                    "UPDATE db_tables SET description = COALESCE(@d, description), own_rows = COALESCE(@o, own_rows) WHERE name = @table",
                    new { d = change.Description, o = change.OwnRows is { } o ? (o ? 1 : 0) : (int?)null, table }, transaction: tx, cancellationToken: token));

            var names = new HashSet<string>(def.Columns.Select(c => c.Name), StringComparer.Ordinal);
            if (change.AddColumns is { Count: > 0 } add)
            {
                if (def.Columns.Count + add.Count > MaxColumns)
                    throw new TableException("column_limit", $"A table has at most {MaxColumns} columns.", args: new { max = MaxColumns });
                var position = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COALESCE(MAX(position), 0) FROM db_columns WHERE table_name = @table", new { table }, transaction: tx, cancellationToken: token));
                foreach (var c in add)
                {
                    await ValidateColumnAsync(db, tx, table, c, names, rowsExist, token);
                    if (!(c.Kind == ColumnKind.Link && c.Multiple))
                    {
                        await db.ExecuteAsync(new CommandDefinition($"ALTER TABLE \"{physical}\" ADD COLUMN \"{c.Name}\" {c.Kind.SqlType()}", transaction: tx, cancellationToken: token));
                        if (c.Default is { } d)
                            await db.ExecuteAsync(new CommandDefinition($"UPDATE \"{physical}\" SET \"{c.Name}\" = @v",
                                new { v = CoerceScalar(table, c, d) }, transaction: tx, cancellationToken: token));
                    }
                    await AddColumnMetaAsync(db, tx, table, c, (int)++position, token);
                }
            }

            foreach (var drop in change.DropColumns ?? Array.Empty<string>())
            {
                var c = def.Columns.FirstOrDefault(x => x.Name == drop)
                    ?? throw TableException.NotFound("column_missing", $"Table '{table}' has no column '{drop}'.", new { table, column = drop });
                await DropColumnStructuresAsync(db, tx, table, c, token);
                if (!(c.Kind == ColumnKind.Link && c.Multiple))
                    await db.ExecuteAsync(new CommandDefinition($"ALTER TABLE \"{physical}\" DROP COLUMN \"{c.Name}\"", transaction: tx, cancellationToken: token));
                await db.ExecuteAsync(new CommandDefinition("DELETE FROM db_columns WHERE table_name = @table AND name = @drop",
                    new { table, drop }, transaction: tx, cancellationToken: token));
                names.Remove(drop);
            }

            foreach (var (from, to) in change.RenameColumns ?? new Dictionary<string, string>())
            {
                var c = (await LoadAsync(db, tx, table, token))!.Columns.FirstOrDefault(x => x.Name == from)
                    ?? throw TableException.NotFound("column_missing", $"Table '{table}' has no column '{from}'.", new { table, column = from });
                if (!TableNames.IsValid(to) || TableBaseColumns.Is(to))
                    throw new TableException("column_name", $"'{to}' can't be a column name.", args: new { table, column = to });
                if (names.Contains(to))
                    throw TableException.Conflict("column_exists", $"Table '{table}' already has a column '{to}'.", new { table, column = to });
                await DropColumnStructuresAsync(db, tx, table, c, token);
                if (c.Kind == ColumnKind.Link && c.Multiple)
                    await db.ExecuteAsync(new CommandDefinition(
                        $"ALTER TABLE \"{TableNames.Join(table, from)}\" RENAME TO \"{TableNames.Join(table, to)}\"", transaction: tx, cancellationToken: token));
                else
                    await db.ExecuteAsync(new CommandDefinition(
                        $"ALTER TABLE \"{physical}\" RENAME COLUMN \"{from}\" TO \"{to}\"", transaction: tx, cancellationToken: token));
                await db.ExecuteAsync(new CommandDefinition("UPDATE db_columns SET name = @to WHERE table_name = @table AND name = @from",
                    new { table, from, to }, transaction: tx, cancellationToken: token));
                var renamed = c with { Name = to };
                if (c.Kind == ColumnKind.Link && c.Multiple)
                    await db.ExecuteAsync(new CommandDefinition(
                        $"DROP INDEX IF EXISTS \"{TableNames.Join(table, from)}__target\"; CREATE INDEX \"{TableNames.Join(table, to)}__target\" ON \"{TableNames.Join(table, to)}\"(target_id);",
                        transaction: tx, cancellationToken: token));
                else
                    await CreateColumnStructuresAsync(db, tx, table, renamed, token);
                names.Remove(from);
                names.Add(to);
            }

            foreach (var (name, update) in change.UpdateColumns ?? new Dictionary<string, ColumnUpdate>())
            {
                var c = (await LoadAsync(db, tx, table, token))!.Columns.FirstOrDefault(x => x.Name == name)
                    ?? throw TableException.NotFound("column_missing", $"Table '{table}' has no column '{name}'.", new { table, column = name });
                if (update.Description is not null)
                    await db.ExecuteAsync(new CommandDefinition("UPDATE db_columns SET description = @d WHERE table_name = @table AND name = @name",
                        new { d = update.Description, table, name }, transaction: tx, cancellationToken: token));
                if (update.AddOptions is { Count: > 0 } more)
                {
                    if (c.Kind != ColumnKind.Choice)
                        throw new TableException("column_options", $"Only choice columns have options ('{name}').", args: new { table, column = name, max = MaxOptions });
                    var options = c.Options!.Concat(more.Where(o => !c.Options!.Contains(o))).ToList();
                    if (options.Count > MaxOptions || options.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > 100))
                        throw new TableException("column_options", $"Choice column '{name}' needs 1 to {MaxOptions} different options.", args: new { table, column = name, max = MaxOptions });
                    await db.ExecuteAsync(new CommandDefinition("UPDATE db_columns SET options = @o WHERE table_name = @table AND name = @name",
                        new { o = JsonSerializer.Serialize(options), table, name }, transaction: tx, cancellationToken: token));
                }
                if (update.Required is { } req)
                {
                    if (req && !(c.Kind == ColumnKind.Link && c.Multiple) && await db.ExecuteScalarAsync<long>(new CommandDefinition(
                            $"SELECT COUNT(*) FROM \"{physical}\" WHERE \"{name}\" IS NULL OR \"{name}\" = ''", transaction: tx, cancellationToken: token)) > 0)
                        throw new TableException("column_required", $"Rows without a value in '{name}' exist — fill them first.", args: new { table, column = name });
                    await db.ExecuteAsync(new CommandDefinition("UPDATE db_columns SET required = @r WHERE table_name = @table AND name = @name",
                        new { r = req ? 1 : 0, table, name }, transaction: tx, cancellationToken: token));
                }
            }
            return (await LoadAsync(db, tx, table, token))!;
        }, ct);
    }

    public async Task DropAsync(ContextRef ctx, TableActor actor, string table, CancellationToken ct = default)
    {
        RequireSpace(ctx);
        RequireDesign(actor);
        await _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var def = await RequireAsync(db, tx, table, token);
            var from = (await db.QueryAsync<string>(new CommandDefinition(
                "SELECT DISTINCT table_name FROM db_columns WHERE kind = 'link' AND link_target = @table AND table_name <> @table",
                new { table }, transaction: tx, cancellationToken: token))).ToList();
            if (from.Count > 0)
                throw TableException.Conflict("still_linked", $"Table '{table}' is linked from {string.Join(", ", from)} — remove those links first.",
                    new { table, links = string.Join(", ", from) });

            var rows = new JsonArray();
            foreach (var row in await ReadRowsAsync(db, tx, $"SELECT * FROM \"{TableNames.Physical(table)}\"", null, token))
                rows.Add(await SnapshotRowAsync(db, tx, def, row, token));
            var data = new JsonObject
            {
                ["$def"] = JsonSerializer.SerializeToNode(DefToJson(def)),
                ["rows"] = rows,
            };
            await TrashSnapshots.PutAsync(db, tx, ctx, "table", table, table, data, actor.UserId, token);

            foreach (var c in def.Columns) await DropColumnStructuresAsync(db, tx, table, c, token);
            await db.ExecuteAsync(new CommandDefinition(
                $"DROP TABLE \"{TableNames.Physical(table)}\"; DELETE FROM db_columns WHERE table_name = @table; DELETE FROM db_tables WHERE name = @table;",
                new { table }, transaction: tx, cancellationToken: token));
            _logger.LogInformation("Table dropped to the trash in space {SpaceId}", ctx.Id);
        }, ct);
    }

    private static object DefToJson(TableDef def) => new
    {
        name = def.Name,
        description = def.Description,
        ownRows = def.OwnRows,
        createdAt = def.CreatedAt,
        createdBy = def.CreatedBy,
        columns = def.Columns.Select(TableJson.DescribeColumn),
    };

    // ────────── Rows ──────────

    public async Task<JsonElement> InsertAsync(ContextRef ctx, TableActor actor, string table, JsonElement values, CancellationToken ct = default)
    {
        RequireSpace(ctx);
        RequireWrite(actor);
        var id = Ulid.NewUlid().ToString();
        await _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var def = await RequireAsync(db, tx, table, token);
            var parsed = await ParseValuesAsync(ctx, db, tx, def, values, insert: true, id, token);
            var now = DateTime.UtcNow.ToString("o");
            var p = new DynamicParameters();
            p.Add("id", id); p.Add("title", parsed.Title); p.Add("author", actor.UserId); p.Add("now", now); p.Add("extra", parsed.Additional);
            var cols = new List<string>();
            foreach (var (name, value) in parsed.Scalars) { cols.Add(name); p.Add("c" + cols.Count, value); }
            await db.ExecuteAsync(new CommandDefinition(
                $"INSERT INTO \"{TableNames.Physical(table)}\" (id, title, author, created_at, last_modified, row_version, additional_data" +
                string.Concat(cols.Select(c => $", \"{c}\"")) + ") VALUES (@id, @title, @author, @now, @now, 1, @extra" +
                string.Concat(cols.Select((_, i) => ", @c" + (i + 1))) + ")", p, transaction: tx, cancellationToken: token));
            await WriteLinksAsync(db, tx, table, id, parsed.Links, token);
        }, ct);
        return (await GetAsync(ctx, table, id, ct))!.Value;
    }

    public async Task<JsonElement?> GetAsync(ContextRef ctx, string table, string id, CancellationToken ct = default)
    {
        RequireSpace(ctx);
        using var db = _db.CreateContextConnection(ctx);
        var def = await RequireAsync(db, null, table, ct);
        var rows = await ReadRowsAsync(db, null, $"SELECT * FROM \"{TableNames.Physical(table)}\" WHERE id = @id", new { id }, ct);
        if (rows.Count == 0) return null;
        return (await ToJsonAsync(db, null, def, rows, ct))[0];
    }

    public async Task<JsonElement> UpdateAsync(ContextRef ctx, TableActor actor, string table, string id, JsonElement values, long? rowVersion, CancellationToken ct = default)
    {
        RequireSpace(ctx);
        RequireWrite(actor);
        await _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var def = await RequireAsync(db, tx, table, token);
            var current = await RequireRowAsync(db, tx, def, id, token);
            CheckOwnRow(def, actor, current);
            if (rowVersion is { } v && Convert.ToInt64(current["row_version"]) != v)
                throw TableException.Conflict("version_conflict", "Someone changed this row in the meantime — read it again.",
                    new { table, id, rowVersion = current["row_version"] });
            var parsed = await ParseValuesAsync(ctx, db, tx, def, values, insert: false, id, token);
            var sets = new List<string> { "last_modified = @now", "row_version = row_version + 1" };
            var p = new DynamicParameters();
            p.Add("id", id); p.Add("now", DateTime.UtcNow.ToString("o"));
            if (parsed.Title is not null) { sets.Add("title = @title"); p.Add("title", parsed.Title); }
            if (parsed.AdditionalGiven) { sets.Add("additional_data = @extra"); p.Add("extra", parsed.Additional); }
            var i = 0;
            foreach (var (name, value) in parsed.Scalars) { sets.Add($"\"{name}\" = @c{++i}"); p.Add("c" + i, value); }
            await db.ExecuteAsync(new CommandDefinition(
                $"UPDATE \"{TableNames.Physical(table)}\" SET {string.Join(", ", sets)} WHERE id = @id", p, transaction: tx, cancellationToken: token));
            await WriteLinksAsync(db, tx, table, id, parsed.Links, token);
        }, ct);
        return (await GetAsync(ctx, table, id, ct))!.Value;
    }

    public async Task DeleteAsync(ContextRef ctx, TableActor actor, string table, string id, CancellationToken ct = default)
    {
        RequireSpace(ctx);
        RequireWrite(actor);
        await _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var def = await RequireAsync(db, tx, table, token);
            var row = await RequireRowAsync(db, tx, def, id, token);
            CheckOwnRow(def, actor, row);
            await LinkGuard.EnsureUnlinkedAsync(db, tx, table, id, token);
            var data = await SnapshotRowAsync(db, tx, def, row, token);
            data["$table"] = table;
            await TrashSnapshots.PutAsync(db, tx, ctx, "row", id, row["title"]?.ToString(), data, actor.UserId, token);
            foreach (var c in def.Columns.Where(c => c.Kind == ColumnKind.Link && c.Multiple))
                await db.ExecuteAsync(new CommandDefinition($"DELETE FROM \"{TableNames.Join(table, c.Name)}\" WHERE row_id = @id",
                    new { id }, transaction: tx, cancellationToken: token));
            await db.ExecuteAsync(new CommandDefinition($"DELETE FROM \"{TableNames.Physical(table)}\" WHERE id = @id",
                new { id }, transaction: tx, cancellationToken: token));
        }, ct);
    }

    private static void CheckOwnRow(TableDef def, TableActor actor, Dictionary<string, object?> row)
    {
        if (def.OwnRows && !actor.EditsAllRows && !Equals(row["author"], actor.UserId))
            throw TableException.Forbidden("not_your_row", $"In '{def.Name}' members change only their own rows.", new { table = def.Name });
    }

    private static async Task<Dictionary<string, object?>> RequireRowAsync(IDbConnection db, IDbTransaction tx, TableDef def, string id, CancellationToken ct)
    {
        var rows = await ReadRowsAsync(db, tx, $"SELECT * FROM \"{TableNames.Physical(def.Name)}\" WHERE id = @id", new { id }, ct);
        return rows.Count > 0 ? rows[0]
            : throw TableException.NotFound("row_missing", $"Table '{def.Name}' has no row '{id}'.", new { table = def.Name, id });
    }

    public async Task<IReadOnlyList<JsonElement>> QueryAsync(ContextRef ctx, string table, QuerySpec spec, CancellationToken ct = default)
    {
        RequireSpace(ctx);
        using var db = _db.CreateContextConnection(ctx);
        var def = await RequireAsync(db, null, table, ct);
        var q = QueryDsl.CompileSelect(TableNames.Physical(table), QuerySchema(def), spec);
        var rows = await ReadRowsAsync(db, null, q.Sql, new DynamicParameters(q.Parameters), ct);
        return await ToJsonAsync(db, null, def, rows, ct);
    }

    public async Task<long> CountAsync(ContextRef ctx, string table, QuerySpec spec, CancellationToken ct = default)
    {
        RequireSpace(ctx);
        using var db = _db.CreateContextConnection(ctx);
        var def = await RequireAsync(db, null, table, ct);
        var q = QueryDsl.CompileCount(TableNames.Physical(table), QuerySchema(def), spec);
        return await db.ExecuteScalarAsync<long>(new CommandDefinition(q.Sql, new DynamicParameters(q.Parameters), cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AggregateResult>> AggregateAsync(
        ContextRef ctx, string table, string fn, string? column, string? groupBy, JsonElement? where, CancellationToken ct = default)
    {
        RequireSpace(ctx);
        using var db = _db.CreateContextConnection(ctx);
        var def = await RequireAsync(db, null, table, ct);
        fn = (fn ?? "").ToLowerInvariant();
        if (fn is not ("count" or "sum" or "min" or "max" or "avg"))
            throw new TableException("aggregate_fn", "fn is count, sum, min, max or avg.");
        ColumnDef? Col(string? name, string role)
        {
            if (name is null) return null;
            if (TableBaseColumns.Is(name)) return new ColumnDef(name, name is "row_version" ? ColumnKind.Integer : ColumnKind.Text);
            var c = def.Columns.FirstOrDefault(x => x.Name == name)
                ?? throw TableException.NotFound("column_missing", $"Table '{table}' has no column '{name}'.", new { table, column = name });
            if (c.Multiple) throw new TableException("aggregate_column", $"'{name}' holds several values and can't be used as {role}.", args: new { table, column = name });
            return c;
        }
        var target = Col(column, "a value");
        var group = Col(groupBy, "a group");
        if (fn != "count" && target is null)
            throw new TableException("aggregate_column", $"{fn} needs a column.", args: new { table, column = "" });
        if (fn is "sum" or "avg" && target!.Kind is not (ColumnKind.Integer or ColumnKind.Decimal))
            throw new TableException("aggregate_column", $"{fn} needs a number column; '{target.Name}' isn't one.", args: new { table, column = target.Name });

        var count = QueryDsl.CompileCount(TableNames.Physical(table), QuerySchema(def), new QuerySpec(Where: where));
        var fromWhere = count.Sql.Substring("SELECT COUNT(*)".Length);
        var value = fn == "count" ? (target is null ? "COUNT(*)" : $"COUNT(\"{target.Name}\")") : $"{fn.ToUpperInvariant()}(\"{target!.Name}\")";
        var sql = group is null
            ? $"SELECT NULL AS g, {value} AS v{fromWhere}"
            : $"SELECT \"{group.Name}\" AS g, {value} AS v{fromWhere} GROUP BY \"{group.Name}\" ORDER BY v DESC LIMIT 500";
        var rows = await ReadRowsAsync(db, null, sql, new DynamicParameters(count.Parameters), ct);
        return rows.Select(r => new AggregateResult(
            group is null ? null : Present(group, r["g"]),
            r["v"] is null ? null : Convert.ToDouble(r["v"], CultureInfo.InvariantCulture))).ToList();
    }

    private static IReadOnlyList<AppColumn> QuerySchema(TableDef def) =>
        TableBaseColumns.Query.Concat(def.Columns
            .Where(c => !(c.Kind == ColumnKind.Link && c.Multiple))
            .Select(c => new AppColumn(c.Name, c.QueryType(), !c.Required))).ToList();

    // ────────── Values ──────────

    private sealed class ParsedValues
    {
        public string? Title;
        public string? Additional;
        public bool AdditionalGiven;
        public List<(string Name, object? Value)> Scalars = new();
        public List<(string Column, List<string> Ids)> Links = new();
    }

    private async Task<ParsedValues> ParseValuesAsync(
        ContextRef ctx, IDbConnection db, IDbTransaction tx, TableDef def, JsonElement values, bool insert, string rowId, CancellationToken ct)
    {
        if (values.ValueKind != JsonValueKind.Object)
            throw new TableException("row_invalid", "A row is a JSON object of column → value.");
        var result = new ParsedValues();
        var given = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in values.EnumerateObject())
        {
            given.Add(prop.Name);
            if (prop.Name == TableBaseColumns.Title)
            {
                var t = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()!.Trim() : "";
                if (t.Length == 0 || t.Length > MaxTitle)
                    throw new TableException("value_title", $"title is required text of up to {MaxTitle} characters.", args: new { table = def.Name, column = "title", max = MaxTitle });
                result.Title = t;
            }
            else if (prop.Name == TableBaseColumns.AdditionalData)
            {
                result.AdditionalGiven = true;
                if (prop.Value.ValueKind == JsonValueKind.Null) continue;
                if (prop.Value.ValueKind != JsonValueKind.Object)
                    throw new TableException("value_invalid", "additional_data is a JSON object.", args: new { table = def.Name, column = "additional_data" });
                result.Additional = prop.Value.GetRawText();
                if (System.Text.Encoding.UTF8.GetByteCount(result.Additional) > MaxAdditionalBytes)
                    throw new TableException("value_too_large", "additional_data is limited to 256 KB.", args: new { table = def.Name, column = "additional_data" });
            }
            else if (TableBaseColumns.Is(prop.Name))
                throw new TableException("value_readonly", $"'{prop.Name}' is set by Fishbowl.", args: new { table = def.Name, column = prop.Name });
            else if (def.Columns.FirstOrDefault(c => c.Name == prop.Name) is not { } col)
                throw TableException.NotFound("column_missing", $"Table '{def.Name}' has no column '{prop.Name}'.", new { table = def.Name, column = prop.Name });
            else
                await ParseColumnValueAsync(ctx, db, tx, def, col, prop.Value, rowId, result, ct);
        }
        if (insert && result.Title is null)
            throw new TableException("value_title", $"title is required text of up to {MaxTitle} characters.", args: new { table = def.Name, column = "title", max = MaxTitle });
        if (insert)
            foreach (var col in def.Columns.Where(c => !given.Contains(c.Name)))
            {
                if (col.Default is { } d) result.Scalars.Add((col.Name, col.Multiple ? JsonSerializer.Serialize(new[] { d.GetString() }) : CoerceScalar(def.Name, col, d)));
                else if (col.Required)
                    throw new TableException("value_required", $"'{col.Name}' is required.", args: new { table = def.Name, column = col.Name });
            }
        return result;
    }

    private async Task ParseColumnValueAsync(
        ContextRef ctx, IDbConnection db, IDbTransaction tx, TableDef def, ColumnDef col, JsonElement v, string rowId, ParsedValues result, CancellationToken ct)
    {
        var empty = v.ValueKind == JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && v.GetString()!.Length == 0)
            || (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0);
        if (empty && col.Required)
            throw new TableException("value_required", $"'{col.Name}' is required.", args: new { table = def.Name, column = col.Name });

        if (col.Multiple)
        {
            var items = empty ? new List<string>() : v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw Invalid(def, col, "a list of text")).ToList()
                : throw Invalid(def, col, "a list");
            items = items.Distinct(StringComparer.Ordinal).ToList();
            if (col.Kind == ColumnKind.Choice)
            {
                var bad = items.FirstOrDefault(i => !col.Options!.Contains(i));
                if (bad is not null) throw Invalid(def, col, "values from: " + string.Join(", ", col.Options!));
                result.Scalars.Add((col.Name, empty ? null : JsonSerializer.Serialize(items)));
            }
            else
            {
                if (items.Count > MaxLinks) throw Invalid(def, col, $"at most {MaxLinks} links");
                foreach (var target in items) await EnsureTargetAsync(db, tx, def, col, target, ct);
                result.Links.Add((col.Name, items));
            }
            return;
        }

        object? value = null;
        if (!empty)
        {
            switch (col.Kind)
            {
                case ColumnKind.Link:
                    if (v.ValueKind != JsonValueKind.String) throw Invalid(def, col, "the id of a row");
                    value = v.GetString();
                    await EnsureTargetAsync(db, tx, def, col, (string)value!, ct);
                    break;
                case ColumnKind.Member:
                    if (v.ValueKind != JsonValueKind.String || await _spaces.GetMembershipAsync(ctx.Id, v.GetString()!, ct) is null)
                        throw Invalid(def, col, "the user id of a member of this space");
                    value = v.GetString();
                    break;
                case ColumnKind.File:
                    var path = v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                    if (path.Length > 1024 || path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(s => s is "" or "." or ".."))
                        throw Invalid(def, col, "a path in this space's files, like Folder/file.pdf");
                    value = path;
                    break;
                default:
                    value = CoerceScalar(def.Name, col, v);
                    break;
            }
            if (col.Unique && value is not null && await db.ExecuteScalarAsync<long>(new CommandDefinition(
                    $"SELECT COUNT(*) FROM \"{TableNames.Physical(def.Name)}\" WHERE \"{col.Name}\" = @value AND id <> @rowId",
                    new { value, rowId }, transaction: tx, cancellationToken: ct)) > 0)
                throw TableException.Conflict("value_unique", $"'{col.Name}' must be unique — another row already has that value.", new { table = def.Name, column = col.Name });
        }
        result.Scalars.Add((col.Name, value));
    }

    private static TableException Invalid(TableDef def, ColumnDef col, string expected) =>
        new("value_invalid", $"'{col.Name}' expects {expected}.", args: new { table = def.Name, column = col.Name, expected });

    private static async Task EnsureTargetAsync(IDbConnection db, IDbTransaction tx, TableDef def, ColumnDef col, string target, CancellationToken ct)
    {
        var physical = TableNames.TargetTable(col.LinkTarget!);
        if (await db.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT COUNT(*) FROM \"{physical}\" WHERE id = @target",
                new { target }, transaction: tx, cancellationToken: ct)) == 0)
            throw new TableException("link_missing", $"'{col.Name}' links to {col.LinkTarget}, which has no '{target}'.",
                args: new { table = def.Name, column = col.Name, target = col.LinkTarget, id = target });
    }

    // A single value of a non-link column, as it is stored.
    internal static object? CoerceScalar(string table, ColumnDef col, JsonElement v)
    {
        TableException Bad(string expected) => new("value_invalid", $"'{col.Name}' expects {expected}.", args: new { table, column = col.Name, expected });
        if (v.ValueKind == JsonValueKind.Null) return null;
        switch (col.Kind)
        {
            case ColumnKind.Text:
            case ColumnKind.LongText:
                var max = col.Kind == ColumnKind.Text ? MaxText : MaxLongText;
                if (v.ValueKind != JsonValueKind.String || v.GetString()!.Length > max) throw Bad($"text of up to {max} characters");
                return v.GetString();
            case ColumnKind.Integer:
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
                throw Bad("a whole number");
            case ColumnKind.Decimal:
                if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
                throw Bad("a number");
            case ColumnKind.YesNo:
                if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean() ? 1L : 0L;
                throw Bad("true or false");
            case ColumnKind.Date:
                if (v.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                throw Bad("a date, YYYY-MM-DD");
            case ColumnKind.DateTime:
                if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                    return dt.UtcDateTime.ToString("o");
                throw Bad("a date and time, ISO 8601");
            case ColumnKind.Choice:
                if (v.ValueKind == JsonValueKind.String && col.Options!.Contains(v.GetString()!)) return v.GetString();
                throw Bad("one of: " + string.Join(", ", col.Options!));
            default:
                throw Bad(col.Kind.ToWire());
        }
    }

    private static async Task WriteLinksAsync(IDbConnection db, IDbTransaction tx, string table, string id, List<(string Column, List<string> Ids)> links, CancellationToken ct)
    {
        foreach (var (column, ids) in links)
        {
            var join = TableNames.Join(table, column);
            await db.ExecuteAsync(new CommandDefinition($"DELETE FROM \"{join}\" WHERE row_id = @id", new { id }, transaction: tx, cancellationToken: ct));
            foreach (var target in ids)
                await db.ExecuteAsync(new CommandDefinition($"INSERT INTO \"{join}\"(row_id, target_id) VALUES (@id, @target)",
                    new { id, target }, transaction: tx, cancellationToken: ct));
        }
    }

    // ────────── Reading ──────────

    internal static async Task<List<Dictionary<string, object?>>> ReadRowsAsync(IDbConnection db, IDbTransaction? tx, string sql, object? param, CancellationToken ct)
    {
        using var reader = await ((SqliteConnection)db).ExecuteReaderAsync(new CommandDefinition(sql, param, transaction: tx, cancellationToken: ct));
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<Dictionary<string, List<string>>> LinksOfAsync(IDbConnection db, IDbTransaction? tx, TableDef def, ColumnDef col, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (ids.Count == 0) return map;
        var rows = await db.QueryAsync<(string RowId, string TargetId)>(new CommandDefinition(
            $"SELECT row_id, target_id FROM \"{TableNames.Join(def.Name, col.Name)}\" WHERE row_id IN @ids ORDER BY target_id",
            new { ids }, transaction: tx, cancellationToken: ct));
        foreach (var (rowId, target) in rows)
            (map.TryGetValue(rowId, out var l) ? l : map[rowId] = new List<string>()).Add(target);
        return map;
    }

    private static async Task<List<JsonElement>> ToJsonAsync(IDbConnection db, IDbTransaction? tx, TableDef def, List<Dictionary<string, object?>> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => (string)r["id"]!).ToList();
        var links = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        foreach (var c in def.Columns.Where(c => c.Kind == ColumnKind.Link && c.Multiple))
            links[c.Name] = await LinksOfAsync(db, tx, def, c, ids, ct);
        return rows.Select(r =>
        {
            var o = new JsonObject
            {
                ["id"] = (string?)r["id"],
                ["title"] = (string?)r["title"],
                ["author"] = (string?)r["author"],
                ["created_at"] = (string?)r["created_at"],
                ["last_modified"] = (string?)r["last_modified"],
                ["row_version"] = Convert.ToInt64(r["row_version"]),
            };
            foreach (var c in def.Columns)
            {
                if (c.Kind == ColumnKind.Link && c.Multiple)
                    o[c.Name] = new JsonArray((links[c.Name].TryGetValue((string)r["id"]!, out var l) ? l : new List<string>())
                        .Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                else
                    o[c.Name] = ToNode(c, r.GetValueOrDefault(c.Name));
            }
            o["additional_data"] = r["additional_data"] is string extra ? JsonNode.Parse(extra) : null;
            return TableJson.ToElement(o);
        }).ToList();
    }

    private static JsonNode? ToNode(ColumnDef c, object? v)
    {
        if (v is null) return c.Multiple ? new JsonArray() : null;
        return c.Kind switch
        {
            ColumnKind.YesNo => JsonValue.Create(Convert.ToInt64(v) != 0),
            ColumnKind.Integer => JsonValue.Create(Convert.ToInt64(v)),
            ColumnKind.Decimal => JsonValue.Create(Convert.ToDouble(v, CultureInfo.InvariantCulture)),
            ColumnKind.Choice when c.Multiple => JsonNode.Parse((string)v),
            _ => JsonValue.Create(v.ToString()),
        };
    }

    private static object? Present(ColumnDef c, object? v) => v is null ? null : c.Kind switch
    {
        ColumnKind.YesNo => Convert.ToInt64(v) != 0,
        ColumnKind.Integer => Convert.ToInt64(v),
        ColumnKind.Decimal => Convert.ToDouble(v, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    // ────────── Trash ──────────

    // A row as it is stored, plus its "multiple" links.
    private static async Task<JsonObject> SnapshotRowAsync(IDbConnection db, IDbTransaction tx, TableDef def, Dictionary<string, object?> row, CancellationToken ct)
    {
        var stored = new JsonObject();
        foreach (var (k, v) in row) stored[k] = v switch { null => null, long l => l, double d => d, _ => v.ToString() };
        var links = new JsonObject();
        foreach (var c in def.Columns.Where(c => c.Kind == ColumnKind.Link && c.Multiple))
        {
            var map = await LinksOfAsync(db, tx, def, c, new[] { (string)row["id"]! }, ct);
            links[c.Name] = new JsonArray(map.Values.SelectMany(x => x).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        }
        return new JsonObject { ["row"] = stored, ["links"] = links };
    }

    // The trash puts a row back: works or doesn't (false = conflict).
    internal static async Task<bool> RestoreRowAsync(IDbConnection db, IDbTransaction tx, string table, JsonObject data, CancellationToken ct)
    {
        var def = await LoadAsync(db, tx, table, ct);
        if (def is null) return false;
        var row = data["row"]!.AsObject();
        var id = row["id"]!.GetValue<string>();
        var physical = TableNames.Physical(table);
        if (await db.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT COUNT(*) FROM \"{physical}\" WHERE id = @id", new { id }, transaction: tx, cancellationToken: ct)) > 0)
            return false;
        var columns = (await db.QueryAsync<string>(new CommandDefinition($"SELECT name FROM pragma_table_info('{physical}')", transaction: tx, cancellationToken: ct))).ToHashSet();
        foreach (var c in def.Columns.Where(c => c.Kind == ColumnKind.Link && !c.Multiple))
            if (row[c.Name] is JsonValue target && await db.ExecuteScalarAsync<long>(new CommandDefinition(
                    $"SELECT COUNT(*) FROM \"{TableNames.TargetTable(c.LinkTarget!)}\" WHERE id = @t", new { t = target.ToString() }, transaction: tx, cancellationToken: ct)) == 0)
                return false;
        var p = new DynamicParameters();
        var names = new List<string>();
        foreach (var (k, v) in row)
        {
            if (!columns.Contains(k)) continue;
            names.Add(k);
            p.Add("p" + names.Count, v switch
            {
                null => null,
                JsonValue jv when jv.TryGetValue<long>(out var l) => l,
                JsonValue jv when jv.TryGetValue<double>(out var d) => d,
                _ => v.GetValue<string>(),
            });
        }
        try
        {
            await db.ExecuteAsync(new CommandDefinition(
                $"INSERT INTO \"{physical}\" ({string.Join(", ", names.Select(n => $"\"{n}\""))}) VALUES ({string.Join(", ", names.Select((_, i) => "@p" + (i + 1)))})",
                p, transaction: tx, cancellationToken: ct));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return false; }
        foreach (var c in def.Columns.Where(c => c.Kind == ColumnKind.Link && c.Multiple))
        {
            var ids = (data["links"]?[c.Name] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? new List<string>();
            foreach (var target in ids)
                if (await db.ExecuteScalarAsync<long>(new CommandDefinition(
                        $"SELECT COUNT(*) FROM \"{TableNames.TargetTable(c.LinkTarget!)}\" WHERE id = @target", new { target }, transaction: tx, cancellationToken: ct)) == 0)
                    return false;
            await WriteLinksAsync(db, tx, table, id, new List<(string, List<string>)> { (c.Name, ids) }, ct);
        }
        return true;
    }

    // The trash puts a dropped table back with its rows.
    internal static async Task<bool> RestoreTableAsync(IDbConnection db, IDbTransaction tx, JsonObject data, CancellationToken ct)
    {
        var d = data["$def"]!.AsObject();
        var name = d["name"]!.GetValue<string>();
        if (await LoadAsync(db, tx, name, ct) is not null) return false;
        var columns = d["columns"]!.AsArray().Select(c =>
        {
            var e = TableJson.ToElement(c!);
            return TableJson.ParseColumn(e);
        }).ToList();
        var def = new TableDef(name, d["description"]?.GetValue<string>(), d["ownRows"]?.GetValue<bool>() ?? false, columns,
            d["createdAt"] is { } at ? at.GetValue<DateTime>() : DateTime.UtcNow, d["createdBy"]?.GetValue<string>());
        try { await CreateInAsync(db, tx, def, ct); }
        catch (TableException) { return false; }   // a link target is gone, or a limit
        foreach (var row in data["rows"]!.AsArray())
            if (!await RestoreRowAsync(db, tx, name, row!.AsObject(), ct)) return false;
        return true;
    }
}

// Links are foreign keys: nothing that is linked to can be deleted. Asked by
// every delete — a table row's, and (through TrashSnapshots) a note's,
// event's, todo's or contact's.
public static class LinkGuard
{
    private sealed class LinkColumn
    {
        public string TableName { get; set; } = "";
        public string Name { get; set; } = "";
        public long Multiple { get; set; }
    }

    // `target` is a table name or a built-in (notes, events, todos, contacts).
    public static async Task EnsureUnlinkedAsync(IDbConnection db, IDbTransaction tx, string target, string id, CancellationToken ct)
    {
        var columns = await db.QueryAsync<LinkColumn>(new CommandDefinition(
            "SELECT table_name, name, multiple FROM db_columns WHERE kind = 'link' AND link_target = @target",
            new { target }, transaction: tx, cancellationToken: ct));
        var from = new List<string>();
        foreach (var c in columns)
        {
            var sql = c.Multiple != 0
                ? $"SELECT COUNT(*) FROM \"{TableNames.Join(c.TableName, c.Name)}\" WHERE target_id = @id"
                : $"SELECT COUNT(*) FROM \"{TableNames.Physical(c.TableName)}\" WHERE \"{c.Name}\" = @id";
            var n = await db.ExecuteScalarAsync<long>(new CommandDefinition(sql, new { id }, transaction: tx, cancellationToken: ct));
            if (n > 0) from.Add($"{c.TableName}.{c.Name} ({n})");
        }
        if (from.Count > 0)
            throw TableException.Conflict("still_linked",
                $"It can't be deleted while it is linked from {string.Join(", ", from)} — remove those links first.",
                new { links = string.Join(", ", from) });
    }
}
