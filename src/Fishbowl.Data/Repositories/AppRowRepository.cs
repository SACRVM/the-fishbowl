using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

// Row-level CRUD for owner-defined tables. The repository owns base-column
// management entirely: callers cannot pass any of them on insert/update; the
// repo fills them server-side. additional_data is the JSON ride-along escape
// hatch, capped at 256 KB UTF-8 bytes (ResourceValidationException → 413/
// InvalidParams). Type validation reads pragma_table_info on every call —
// cheap, the alternative is caching column metadata per-connection which
// breaks once ALTER lands mid-session.
public class AppRowRepository : IAppRowRepository
{
    private const int MaxAdditionalDataBytes = 256 * 1024;
    private const string Resource = "app_row";
    private const int BusyTimeoutMs = 5000;

    // Base columns the caller can never set directly. `title` is the only base
    // column the caller drives — it's NOT NULL on every app table, so insert
    // requires it. `author` is filled by the repo from `actorId`.
    private static readonly HashSet<string> CallerForbiddenBaseColumns = new(StringComparer.Ordinal)
    {
        BaseColumns.Id,
        BaseColumns.Author,
        BaseColumns.CreatedAt,
        BaseColumns.LastModified,
        BaseColumns.RowVersion,
        BaseColumns.IsDeleted,
        BaseColumns.DeletedAt,
    };

    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<AppRowRepository> _logger;

    public AppRowRepository(DatabaseFactory dbFactory, ILogger<AppRowRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<AppRowRepository>.Instance;
    }

    public async Task<IReadOnlyDictionary<string, object?>> InsertAsync(
        AppRef appRef,
        string tableName,
        IReadOnlyDictionary<string, object?> fields,
        string? actorId,
        CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");
        if (fields is null) throw new ArgumentNullException(nameof(fields));

        using var db = (SqliteConnection)_dbFactory.CreateAppConnection(appRef);
        await SetBusyTimeoutAsync(db, ct);
        var schema = await ReadColumnsAsync(db, tableName, ct);

        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        var now = DateTime.UtcNow.ToString("o");
        var id = Ulid.NewUlid().ToString();

        // Server-fills first; if the caller tries to overwrite any forbidden
        // base column we throw — covers `id`-spoofing and audit forgery.
        row[BaseColumns.Id] = id;
        row[BaseColumns.CreatedAt] = now;
        row[BaseColumns.LastModified] = now;
        row[BaseColumns.RowVersion] = 1L;
        row[BaseColumns.IsDeleted] = 0L;
        row[BaseColumns.DeletedAt] = null;
        row[BaseColumns.Author] = actorId;

        var titleSeen = false;
        foreach (var (key, value) in fields)
        {
            if (CallerForbiddenBaseColumns.Contains(key))
                throw new InvalidOperationException(
                    $"Caller cannot set base column '{key}' on insert.");
            if (!schema.TryGetValue(key, out var col))
                throw new InvalidOperationException(
                    $"Unknown column '{key}' on table '{tableName}'.");
            if (key == BaseColumns.Title) titleSeen = true;
            row[key] = CoerceForColumn(col, key, value);
        }

        if (!titleSeen)
            throw new InvalidOperationException(
                $"Base column '{BaseColumns.Title}' is required on insert.");

        EnforceAdditionalDataCap(row);

        await InsertRowAsync(db, tableName, row, ct);
        return row;
    }

    public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(
        AppRef appRef,
        string tableName,
        string id,
        bool includeDeleted = false,
        CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");

        using var db = (SqliteConnection)_dbFactory.CreateAppConnection(appRef);
        await SetBusyTimeoutAsync(db, ct);

        var sql = includeDeleted
            ? $"SELECT * FROM \"{tableName}\" WHERE id = @id"
            : $"SELECT * FROM \"{tableName}\" WHERE id = @id AND is_deleted = 0";
        return await SelectSingleAsync(db, sql, new { id }, ct);
    }

    public async Task<IReadOnlyDictionary<string, object?>> UpdateAsync(
        AppRef appRef,
        string tableName,
        string id,
        IReadOnlyDictionary<string, object?> fields,
        string? actorId,
        CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");
        if (fields is null) throw new ArgumentNullException(nameof(fields));
        if (fields.Count == 0)
            throw new InvalidOperationException("PATCH requires at least one field.");

        using var db = (SqliteConnection)_dbFactory.CreateAppConnection(appRef);
        await SetBusyTimeoutAsync(db, ct);
        var schema = await ReadColumnsAsync(db, tableName, ct);

        // Caller cannot touch any base column except title.
        var assigns = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            if (CallerForbiddenBaseColumns.Contains(key))
                throw new InvalidOperationException(
                    $"Caller cannot patch base column '{key}'.");
            if (!schema.TryGetValue(key, out var col))
                throw new InvalidOperationException(
                    $"Unknown column '{key}' on table '{tableName}'.");
            assigns[key] = CoerceForColumn(col, key, value);
        }

        EnforceAdditionalDataCap(assigns);

        // Bump row_version + last_modified atomically with the assignments.
        // row_version is read-then-write so two concurrent patches don't
        // collapse to the same number; the bump is computed in SQL using
        // `row_version + 1` which is safe inside a single statement.
        var setClauses = string.Join(", ", assigns.Keys.Select((k, i) => $"\"{k}\" = @p{i}"))
            + ", \"last_modified\" = @last_modified, \"row_version\" = \"row_version\" + 1";
        var sql = $"UPDATE \"{tableName}\" SET {setClauses} WHERE id = @id AND is_deleted = 0";

        var dp = new DynamicParameters();
        var idx = 0;
        foreach (var (_, v) in assigns) dp.Add($"p{idx++}", v);
        dp.Add("last_modified", DateTime.UtcNow.ToString("o"));
        dp.Add("id", id);

        int affected;
        try
        {
            affected = await db.ExecuteAsync(new CommandDefinition(sql, dp, cancellationToken: ct));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */)
        {
            throw new InvalidOperationException(
                $"Update violates a constraint on '{tableName}': {ex.Message}", ex);
        }
        if (affected == 0)
            throw new InvalidOperationException(
                $"Row '{id}' not found on table '{tableName}' (or already soft-deleted; call Restore first).");

        var fresh = await SelectSingleAsync(db,
            $"SELECT * FROM \"{tableName}\" WHERE id = @id", new { id }, ct);
        return fresh!;
    }

    public async Task<bool> DeleteAsync(
        AppRef appRef,
        string tableName,
        string id,
        bool hard,
        string? actorId,
        CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");

        using var db = (SqliteConnection)_dbFactory.CreateAppConnection(appRef);
        await SetBusyTimeoutAsync(db, ct);

        if (hard)
        {
            var sql = $"DELETE FROM \"{tableName}\" WHERE id = @id";
            var n = await db.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
            if (n > 0) _logger.LogInformation(
                "Hard-deleted row {RowId} from {Table} (app {AppId})", id, tableName, appRef.AppId);
            return n > 0;
        }

        // Soft delete: bump row_version (a delete is a state change like any
        // other write) and stamp deleted_at. Idempotent on already-deleted
        // rows — second soft-delete just no-ops the WHERE.
        var softSql =
            $"UPDATE \"{tableName}\" SET is_deleted = 1, deleted_at = @t, last_modified = @t, " +
            $"row_version = row_version + 1 WHERE id = @id AND is_deleted = 0";
        var softN = await db.ExecuteAsync(new CommandDefinition(
            softSql, new { id, t = DateTime.UtcNow.ToString("o") }, cancellationToken: ct));
        if (softN > 0) _logger.LogInformation(
            "Soft-deleted row {RowId} from {Table} (app {AppId})", id, tableName, appRef.AppId);
        return softN > 0;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        AppRef appRef,
        string tableName,
        QuerySpec spec,
        CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");

        using var db = (SqliteConnection)_dbFactory.CreateAppConnection(appRef);
        await SetBusyTimeoutAsync(db, ct);

        var schema = await ReadAppColumnsAsync(db, tableName, ct);
        var compiled = QueryDsl.CompileSelect(tableName, schema, spec);

        var dp = new DynamicParameters();
        foreach (var (k, v) in compiled.Parameters) dp.Add(k, v);

        using var reader = await db.ExecuteReaderAsync(new CommandDefinition(
            compiled.Sql, dp, cancellationToken: ct));
        var dr = (DbDataReader)reader;

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await dr.ReadAsync(ct)) rows.Add(ReadRowAsync(dr));
        return rows;
    }

    public async Task<long> CountAsync(
        AppRef appRef,
        string tableName,
        QuerySpec spec,
        CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");

        using var db = (SqliteConnection)_dbFactory.CreateAppConnection(appRef);
        await SetBusyTimeoutAsync(db, ct);

        var schema = await ReadAppColumnsAsync(db, tableName, ct);
        var compiled = QueryDsl.CompileCount(tableName, schema, spec);

        var dp = new DynamicParameters();
        foreach (var (k, v) in compiled.Parameters) dp.Add(k, v);

        return await db.ExecuteScalarAsync<long>(new CommandDefinition(
            compiled.Sql, dp, cancellationToken: ct));
    }

    public async Task<bool> RestoreAsync(
        AppRef appRef,
        string tableName,
        string id,
        string? actorId,
        CancellationToken ct = default)
    {
        SqliteIdentifiers.Validate(tableName, "table");

        using var db = (SqliteConnection)_dbFactory.CreateAppConnection(appRef);
        await SetBusyTimeoutAsync(db, ct);

        var sql =
            $"UPDATE \"{tableName}\" SET is_deleted = 0, deleted_at = NULL, last_modified = @t, " +
            $"row_version = row_version + 1 WHERE id = @id AND is_deleted = 1";
        var n = await db.ExecuteAsync(new CommandDefinition(
            sql, new { id, t = DateTime.UtcNow.ToString("o") }, cancellationToken: ct));
        if (n > 0) _logger.LogInformation(
            "Restored row {RowId} on {Table} (app {AppId})", id, tableName, appRef.AppId);
        return n > 0;
    }

    private static async Task InsertRowAsync(
        SqliteConnection db, string tableName,
        IReadOnlyDictionary<string, object?> row,
        CancellationToken ct)
    {
        var cols = string.Join(", ", row.Keys.Select(k => $"\"{k}\""));
        var placeholders = string.Join(", ", row.Keys.Select((_, i) => $"@p{i}"));
        var sql = $"INSERT INTO \"{tableName}\" ({cols}) VALUES ({placeholders})";

        var dp = new DynamicParameters();
        var i = 0;
        foreach (var (_, v) in row) dp.Add($"p{i++}", v);

        try
        {
            await db.ExecuteAsync(new CommandDefinition(sql, dp, cancellationToken: ct));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */)
        {
            throw new InvalidOperationException(
                $"Insert violates a constraint on '{tableName}': {ex.Message}", ex);
        }
    }

    private static async Task<IReadOnlyDictionary<string, object?>?> SelectSingleAsync(
        SqliteConnection db, string sql, object parameters, CancellationToken ct)
    {
        var reader = await db.ExecuteReaderAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        try
        {
            var dr = (DbDataReader)reader;
            if (!await dr.ReadAsync(ct)) return null;
            return ReadRowAsync(dr);
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static Dictionary<string, object?> ReadRowAsync(DbDataReader reader)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
            row[name] = value;
        }
        return row;
    }

    // pragma_table_info gives us name + SQL affinity per column. We can't
    // recover logical types (boolean/datetime/json) without a sidecar
    // metadata table — out of scope for MVP — so validation falls back to
    // SQL-level affinity. Good enough: the agent driving the wire has the
    // typed schema from describe_table.
    private static async Task<Dictionary<string, ColumnInfo>> ReadColumnsAsync(
        SqliteConnection db, string tableName, CancellationToken ct)
    {
        var rows = await db.QueryAsync<PragmaTableInfo>(new CommandDefinition(
            $"PRAGMA table_info(\"{tableName}\")", cancellationToken: ct));
        var list = rows.ToList();
        if (list.Count == 0)
            throw new InvalidOperationException(
                $"Table '{tableName}' does not exist in this app.");

        var map = new Dictionary<string, ColumnInfo>(StringComparer.Ordinal);
        foreach (var r in list)
        {
            var affinity = (r.Type ?? "").Trim().ToUpperInvariant() switch
            {
                "INTEGER" => SqlAffinity.Integer,
                "REAL" => SqlAffinity.Real,
                _ => SqlAffinity.Text, // TEXT, BLOB, NUMERIC all collapse here
            };
            map[r.Name] = new ColumnInfo(r.Name, affinity, r.Notnull != 0);
        }
        return map;
    }

    // Map the caller's CLR value onto the column's SQL affinity. The set of
    // accepted source types is deliberately narrow — wire-layer JSON
    // deserialization is expected to land on long/double/bool/string before
    // reaching here.
    private static object? CoerceForColumn(ColumnInfo col, string key, object? value)
    {
        // additional_data has its own validation path (ResourceValidationException
        // → MCP InvalidParams / REST 413). Skip the generic coercion so the
        // shape/size errors reach the right error envelope.
        if (key == BaseColumns.AdditionalData)
        {
            if (value is null) return null;
            if (value is string) return value;
            throw new ResourceValidationException(new ResourceValidationError(
                Resource, BaseColumns.AdditionalData, "must be a JSON string"));
        }

        if (value is null)
        {
            if (col.NotNull && !BaseColumns.IsBase(key))
                throw new InvalidOperationException(
                    $"Column '{col.Name}' is NOT NULL; null value rejected.");
            return null;
        }

        switch (col.Affinity)
        {
            case SqlAffinity.Integer:
                if (value is bool b) return b ? 1L : 0L;
                if (value is long l) return l;
                if (value is int or short or byte) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
                throw TypeError(col.Name, "integer", value);

            case SqlAffinity.Real:
                if (value is double d) return d;
                if (value is float or decimal) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (value is long or int or short or byte) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                throw TypeError(col.Name, "real", value);

            case SqlAffinity.Text:
            default:
                if (value is string s) return s;
                // Accept DateTime CLR values by formatting ISO-8601 — handy
                // for tests + simplifies callers that pass DateTime.UtcNow.
                if (value is DateTime dt) return dt.ToString("o", CultureInfo.InvariantCulture);
                throw TypeError(col.Name, "text", value);
        }
    }

    private static void EnforceAdditionalDataCap(IReadOnlyDictionary<string, object?> row)
    {
        if (!row.TryGetValue(BaseColumns.AdditionalData, out var raw) || raw is null) return;
        if (raw is not string s)
            throw new ResourceValidationException(new ResourceValidationError(
                Resource, BaseColumns.AdditionalData, "must be a JSON string"));
        var bytes = Encoding.UTF8.GetByteCount(s);
        if (bytes > MaxAdditionalDataBytes)
            throw new ResourceValidationException(new ResourceValidationError(
                Resource, BaseColumns.AdditionalData,
                $"exceeds {MaxAdditionalDataBytes} bytes ({bytes} bytes supplied)"));
    }

    private static InvalidOperationException TypeError(string column, string expected, object value)
        => new($"Column '{column}' expects {expected}; got {value.GetType().Name}.");

    private static async Task SetBusyTimeoutAsync(SqliteConnection db, CancellationToken ct)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMs};";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Build the AppColumn list used by QueryDsl. Round-trips through
    // pragma_table_info, so logical types (Boolean / DateTime / Json) collapse
    // to their SQL affinity (Integer / Text). The DSL still validates correctly
    // for the common $like/$gt cases — the lost fidelity affects only the
    // describe surface, which Phase B.2 already flagged as best-effort.
    private static async Task<IReadOnlyList<AppColumn>> ReadAppColumnsAsync(
        SqliteConnection db, string tableName, CancellationToken ct)
    {
        var rows = await db.QueryAsync<PragmaTableInfo>(new CommandDefinition(
            $"PRAGMA table_info(\"{tableName}\")", cancellationToken: ct));
        var list = rows.ToList();
        if (list.Count == 0)
            throw new InvalidOperationException(
                $"Table '{tableName}' does not exist in this app.");

        var cols = new List<AppColumn>(list.Count);
        foreach (var r in list)
        {
            if (BaseColumns.IsBase(r.Name))
            {
                cols.Add(BaseColumns.All.First(c => c.Name == r.Name));
                continue;
            }
            var t = (r.Type ?? "").Trim().ToUpperInvariant() switch
            {
                "INTEGER" => AppColumnType.Integer,
                "REAL" => AppColumnType.Real,
                _ => AppColumnType.Text,
            };
            cols.Add(new AppColumn(r.Name, t, Nullable: r.Notnull == 0));
        }
        return cols;
    }

    private enum SqlAffinity { Text, Integer, Real }
    private sealed record ColumnInfo(string Name, SqlAffinity Affinity, bool NotNull);

    private sealed class PragmaTableInfo
    {
        public long Cid { get; set; }
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public long Notnull { get; set; }
        public object? Dflt_Value { get; set; }
        public long Pk { get; set; }
    }
}
