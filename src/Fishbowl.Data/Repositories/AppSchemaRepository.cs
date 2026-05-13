using System.Data;
using System.Globalization;
using System.Text;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

// DDL generator for owner-defined tables inside an app DB. Every public method
// validates identifiers + base-column overlap, opens the app DB via the shared
// factory, sets PRAGMA busy_timeout = 5000, and wraps the work in a single
// transaction. ALTERs run under a 30s timeout (caught via linked CTS) so an
// agent cannot hold the file under a long migration; on timeout we throw
// TimeoutException, which the MCP/REST layer translates to 408.
public class AppSchemaRepository : IAppSchemaRepository
{
    private const int BusyTimeoutMs = 5000;
    private static readonly TimeSpan AlterTimeout = TimeSpan.FromSeconds(30);

    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<AppSchemaRepository> _logger;

    public AppSchemaRepository(DatabaseFactory dbFactory, ILogger<AppSchemaRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<AppSchemaRepository>.Instance;
    }

    public async Task<IReadOnlyList<string>> ListTablesAsync(AppRef appRef, CancellationToken ct = default)
    {
        using var db = (SqliteConnection)_dbFactory.CreateAppConnection(appRef);
        await SetBusyTimeoutAsync(db, ct);
        var rows = await db.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name",
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<AppTable?> DescribeTableAsync(AppRef appRef, string tableName, CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");
        using var db = (SqliteConnection)_dbFactory.CreateAppConnection(appRef);
        await SetBusyTimeoutAsync(db, ct);

        var exists = await db.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = @name",
            new { name = tableName }, cancellationToken: ct));
        if (exists == 0) return null;

        var info = (await db.QueryAsync<PragmaTableInfo>(new CommandDefinition(
            $"PRAGMA table_info(\"{tableName}\")", cancellationToken: ct))).ToList();

        // Single-column UNIQUE indexes created by the column constraint show
        // up in pragma_index_list with origin='u'. Pull them so a round-trip
        // describe sees the same column.IsUnique it was created with.
        var uniqueCols = await ReadSingleColumnUniqueColumnsAsync(db, tableName, ct);

        var columns = new List<AppColumn>();
        foreach (var row in info)
        {
            if (BaseColumns.IsBase(row.Name))
            {
                columns.Add(BaseColumns.All.First(c => c.Name == row.Name));
                continue;
            }

            if (!TryReverseSqlType(row.Type, out var type))
            {
                _logger.LogWarning(
                    "Skipping column '{Col}' in table '{Table}' — unknown SQL type '{Type}'",
                    row.Name, tableName, row.Type);
                continue;
            }

            columns.Add(new AppColumn(
                Name: row.Name,
                Type: type,
                Nullable: row.Notnull == 0,
                DefaultValue: row.DefaultText,
                IsUnique: uniqueCols.Contains(row.Name),
                IsBaseColumn: false));
        }

        return new AppTable(tableName, columns);
    }

    public async Task CreateTableAsync(
        AppRef appRef,
        string tableName,
        IReadOnlyList<AppColumn> userColumns,
        CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");
        ValidateUserColumns(userColumns);

        var ddl = BuildCreateTableDdl(tableName, userColumns);
        var idxDdl =
            $"CREATE INDEX \"{BaseColumns.SoftDeleteIndexName(tableName)}\" " +
            $"ON \"{tableName}\"({BaseColumns.IsDeleted});";

        await WithDdlAsync(appRef, async (db, tx) =>
        {
            // Pre-flight existence check — SQLite would throw a generic SqliteException;
            // a typed error is friendlier for the MCP/REST surface.
            var exists = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = @name",
                new { name = tableName }, transaction: tx, cancellationToken: ct));
            if (exists > 0)
                throw new InvalidOperationException(
                    $"Table '{tableName}' already exists in this app.");

            await ExecuteWithTimeoutAsync(db, tx, ddl, AlterTimeout, ct);
            await ExecuteWithTimeoutAsync(db, tx, idxDdl, AlterTimeout, ct);
        }, ct);

        _logger.LogInformation(
            "Created app table {Table} ({UserColumnCount} user columns) in app {AppId}",
            tableName, userColumns.Count, appRef.AppId);
    }

    public async Task AddColumnAsync(
        AppRef appRef, string tableName, AppColumn column, CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");
        SqliteIdentifiers.Validate(column.Name, "column");
        if (BaseColumns.IsBase(column.Name))
            throw new InvalidOperationException(
                $"Column name '{column.Name}' overlaps a base column and is reserved.");

        // Spec pin: ADD COLUMN is always nullable in MVP. Existing rows would
        // need a value for NOT NULL columns; the MVP refuses to guess and
        // forces the caller to design schemas where late additions are
        // optional from the start.
        if (!column.Nullable)
            throw new InvalidOperationException(
                "ALTER TABLE ADD COLUMN must be Nullable=true in MVP.");
        // SQLite refuses inline UNIQUE in ALTER. The DSL maps UNIQUE to
        // CREATE UNIQUE INDEX on the existing table instead.
        if (column.IsUnique)
            throw new InvalidOperationException(
                "UNIQUE constraints on ADD COLUMN are unsupported. Use CreateIndexAsync after add.");

        var def = BuildColumnDefinition(column);
        var sql = $"ALTER TABLE \"{tableName}\" ADD COLUMN {def};";

        await WithDdlAsync(appRef, async (db, tx) =>
        {
            await EnsureTableExistsAsync(db, tx, tableName, ct);
            await ExecuteWithTimeoutAsync(db, tx, sql, AlterTimeout, ct);
        }, ct);
    }

    public async Task RenameColumnAsync(
        AppRef appRef, string tableName, string fromName, string toName, CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");
        SqliteIdentifiers.Validate(fromName, "column");
        SqliteIdentifiers.Validate(toName, "column");
        if (BaseColumns.IsBase(fromName))
            throw new InvalidOperationException(
                $"Base column '{fromName}' is protected; rename rejected.");
        if (BaseColumns.IsBase(toName))
            throw new InvalidOperationException(
                $"Target name '{toName}' overlaps a base column and is reserved.");

        var sql = $"ALTER TABLE \"{tableName}\" RENAME COLUMN \"{fromName}\" TO \"{toName}\";";
        await WithDdlAsync(appRef, async (db, tx) =>
        {
            await EnsureTableExistsAsync(db, tx, tableName, ct);
            await ExecuteWithTimeoutAsync(db, tx, sql, AlterTimeout, ct);
        }, ct);
    }

    public async Task DropColumnAsync(
        AppRef appRef, string tableName, string columnName, CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");
        SqliteIdentifiers.Validate(columnName, "column");
        if (BaseColumns.IsBase(columnName))
            throw new InvalidOperationException(
                $"Base column '{columnName}' is protected; drop rejected.");

        var sql = $"ALTER TABLE \"{tableName}\" DROP COLUMN \"{columnName}\";";
        await WithDdlAsync(appRef, async (db, tx) =>
        {
            await EnsureTableExistsAsync(db, tx, tableName, ct);
            await ExecuteWithTimeoutAsync(db, tx, sql, AlterTimeout, ct);
        }, ct);
    }

    public async Task DropTableAsync(AppRef appRef, string tableName, CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");
        var sql = $"DROP TABLE \"{tableName}\";";
        await WithDdlAsync(appRef, async (db, tx) =>
        {
            await EnsureTableExistsAsync(db, tx, tableName, ct);
            await ExecuteWithTimeoutAsync(db, tx, sql, AlterTimeout, ct);
        }, ct);
        _logger.LogInformation("Dropped app table {Table} in app {AppId}", tableName, appRef.AppId);
    }

    public async Task CreateIndexAsync(
        AppRef appRef, string tableName, string columnName, CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");
        SqliteIdentifiers.Validate(columnName, "column");
        if (BaseColumns.IsBase(columnName))
            throw new InvalidOperationException(
                $"Base column '{columnName}' has its own server-managed index; refusing.");

        var idxName = $"idx_{tableName}_{columnName}";
        if (idxName.Length > SqliteIdentifiers.MaxLength)
            throw new InvalidOperationException(
                $"Index name '{idxName}' exceeds {SqliteIdentifiers.MaxLength} chars. Shorten the table or column.");

        var sql = $"CREATE INDEX IF NOT EXISTS \"{idxName}\" ON \"{tableName}\"(\"{columnName}\");";
        await WithDdlAsync(appRef, async (db, tx) =>
        {
            await EnsureTableExistsAsync(db, tx, tableName, ct);
            await EnsureColumnExistsAsync(db, tx, tableName, columnName, ct);
            await ExecuteWithTimeoutAsync(db, tx, sql, AlterTimeout, ct);
        }, ct);
    }

    private static void ValidateUserColumns(IReadOnlyList<AppColumn> userColumns)
    {
        if (userColumns is null) throw new ArgumentNullException(nameof(userColumns));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var col in userColumns)
        {
            SqliteIdentifiers.Validate(col.Name, "column");
            if (BaseColumns.IsBase(col.Name))
                throw new InvalidOperationException(
                    $"Column name '{col.Name}' overlaps a base column and is reserved.");
            if (!seen.Add(col.Name))
                throw new InvalidOperationException(
                    $"Duplicate column name '{col.Name}' in CREATE TABLE.");
            if (col.IsBaseColumn)
                throw new InvalidOperationException(
                    "User columns must not set IsBaseColumn = true; base columns are server-injected.");
            ValidateDefaultValue(col);
        }
    }

    private static void ValidateDefaultValue(AppColumn col)
    {
        if (col.DefaultValue is null) return;
        switch (col.Type)
        {
            case AppColumnType.Text:
            case AppColumnType.DateTime:
            case AppColumnType.Json:
                if (col.DefaultValue is not string)
                    throw new InvalidOperationException(
                        $"Default value for '{col.Name}' must be a string for type {col.Type.ToWireName()}.");
                break;
            case AppColumnType.Integer:
                if (col.DefaultValue is not (long or int or short or byte))
                    throw new InvalidOperationException(
                        $"Default value for '{col.Name}' must be an integer.");
                break;
            case AppColumnType.Real:
                if (col.DefaultValue is not (double or float or decimal or long or int))
                    throw new InvalidOperationException(
                        $"Default value for '{col.Name}' must be a number.");
                break;
            case AppColumnType.Boolean:
                if (col.DefaultValue is not bool)
                    throw new InvalidOperationException(
                        $"Default value for '{col.Name}' must be boolean.");
                break;
        }
    }

    private static string BuildCreateTableDdl(string tableName, IReadOnlyList<AppColumn> userColumns)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE \"").Append(tableName).AppendLine("\" (");
        sb.Append("    ").Append(BaseColumns.DdlFragment);
        foreach (var col in userColumns)
        {
            sb.AppendLine(",");
            sb.Append("    ").Append(BuildColumnDefinition(col));
        }
        sb.AppendLine();
        sb.Append(");");
        return sb.ToString();
    }

    private static string BuildColumnDefinition(AppColumn col)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(col.Name).Append("\" ").Append(col.Type.ToSqlType());
        if (!col.Nullable) sb.Append(" NOT NULL");
        if (col.DefaultValue is not null) sb.Append(" DEFAULT ").Append(SqlLiteral(col.Type, col.DefaultValue));
        if (col.IsUnique) sb.Append(" UNIQUE");
        return sb.ToString();
    }

    private static string SqlLiteral(AppColumnType type, object value) => type switch
    {
        AppColumnType.Text or AppColumnType.DateTime or AppColumnType.Json
            => "'" + ((string)value).Replace("'", "''") + "'",
        AppColumnType.Integer => Convert.ToInt64(value, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture),
        AppColumnType.Real => Convert.ToDouble(value, CultureInfo.InvariantCulture)
            .ToString("R", CultureInfo.InvariantCulture),
        AppColumnType.Boolean => ((bool)value) ? "1" : "0",
        _ => throw new InvalidOperationException($"Unsupported type for default literal: {type}"),
    };

    private static bool TryReverseSqlType(string sqlType, out AppColumnType type)
    {
        switch (sqlType?.Trim().ToUpperInvariant())
        {
            case "TEXT": type = AppColumnType.Text; return true;
            case "INTEGER": type = AppColumnType.Integer; return true;
            case "REAL": type = AppColumnType.Real; return true;
            default: type = default; return false;
        }
        // Boolean → INTEGER, DateTime → TEXT, Json → TEXT all collapse on read.
        // The repository surface can't distinguish them without metadata; we
        // surface them as their underlying SQL types here. Phase B.5 may add a
        // sidecar metadata table if the wire surface needs round-trip fidelity.
    }

    private static async Task EnsureTableExistsAsync(
        SqliteConnection db, IDbTransaction tx, string tableName, CancellationToken ct)
    {
        var count = await db.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = @name",
            new { name = tableName }, transaction: tx, cancellationToken: ct));
        if (count == 0)
            throw new InvalidOperationException($"Table '{tableName}' does not exist in this app.");
    }

    private static async Task EnsureColumnExistsAsync(
        SqliteConnection db, IDbTransaction tx, string tableName, string columnName, CancellationToken ct)
    {
        var count = await db.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = @col",
            new { table = tableName, col = columnName }, transaction: tx, cancellationToken: ct));
        if (count == 0)
            throw new InvalidOperationException(
                $"Column '{columnName}' does not exist on table '{tableName}'.");
    }

    private static async Task<HashSet<string>> ReadSingleColumnUniqueColumnsAsync(
        SqliteConnection db, string tableName, CancellationToken ct)
    {
        var indexes = (await db.QueryAsync<PragmaIndexEntry>(new CommandDefinition(
            $"PRAGMA index_list(\"{tableName}\")", cancellationToken: ct))).ToList();

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var idx in indexes)
        {
            if (idx.Unique == 0 || !string.Equals(idx.Origin, "u", StringComparison.Ordinal))
                continue;

            var cols = (await db.QueryAsync<PragmaIndexColumn>(new CommandDefinition(
                $"PRAGMA index_info(\"{idx.Name}\")", cancellationToken: ct))).ToList();
            if (cols.Count == 1 && cols[0].Name is { } colName)
                result.Add(colName);
        }
        return result;
    }

    private async Task WithDdlAsync(
        AppRef appRef,
        Func<SqliteConnection, SqliteTransaction, Task> work,
        CancellationToken ct)
    {
        using var db = (SqliteConnection)_dbFactory.CreateAppConnection(appRef);
        await SetBusyTimeoutAsync(db, ct);
        using var tx = (SqliteTransaction)db.BeginTransaction();
        try
        {
            await work(db, tx);
            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { /* nothing to do */ }
            throw;
        }
    }

    private static async Task SetBusyTimeoutAsync(SqliteConnection db, CancellationToken ct)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMs};";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ALTER TABLE on a busy DB is the only realistic stall path we have
    // pre-Sync. Cap each DDL statement at 30s — anything longer is symptom of
    // a held lock or an enormous backfill, and we'd rather 408 the agent than
    // pin the worker.
    private static async Task ExecuteWithTimeoutAsync(
        SqliteConnection db,
        SqliteTransaction tx,
        string sql,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"App DDL exceeded {timeout.TotalSeconds:F0}s timeout.");
        }
    }

    // POCOs (not records) for pragma_* readouts. Dapper picks columns by name
    // against settable properties, sidestepping its "exact constructor signature
    // match" rule — pragma_table_info returns dflt_value as TEXT but Dapper's
    // type-binding may surface it as byte[] depending on column type, which
    // a positional record can't accommodate.
    private sealed class PragmaTableInfo
    {
        public long Cid { get; set; }
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public long Notnull { get; set; }
        public object? Dflt_Value { get; set; }
        public long Pk { get; set; }

        public string? DefaultText => Dflt_Value switch
        {
            null => null,
            string s => s,
            byte[] b => System.Text.Encoding.UTF8.GetString(b),
            _ => Convert.ToString(Dflt_Value, CultureInfo.InvariantCulture),
        };
    }

    private sealed class PragmaIndexEntry
    {
        public long Seq { get; set; }
        public string Name { get; set; } = "";
        public long Unique { get; set; }
        public string? Origin { get; set; }
        public long Partial { get; set; }
    }

    private sealed class PragmaIndexColumn
    {
        public long Seqno { get; set; }
        public long Cid { get; set; }
        public string? Name { get; set; }
    }
}
