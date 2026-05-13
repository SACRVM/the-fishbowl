using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Fishbowl.Core.Apps;

// MongoDB-style query DSL → parameterised SQL. Two contracts:
//   * Inputs are validated against the table's typed schema before any SQL is
//     emitted (no string interpolation of caller values, ever).
//   * Outputs are bounded — depth, leaf count, $in size, and limit all gated
//     so a pathological agent payload can't pin the SQLite planner.
//
// The DSL is intentionally read-only and table-scoped. JOINs, aggregates, and
// raw expressions are post-MVP; the wire layer (Phase B.5) translates a single
// app_query / app_count call into one Compile here.
public static class QueryDsl
{
    public const int MaxDepth = 5;
    public const int MaxLeaves = 100;
    public const int MaxInElements = 50;
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    public static CompiledQuery CompileSelect(
        string tableName,
        IReadOnlyList<AppColumn> schema,
        QuerySpec spec)
        => Compile(tableName, schema, spec, forCount: false);

    public static CompiledQuery CompileCount(
        string tableName,
        IReadOnlyList<AppColumn> schema,
        QuerySpec spec)
        => Compile(tableName, schema, spec, forCount: true);

    private static CompiledQuery Compile(
        string tableName,
        IReadOnlyList<AppColumn> schema,
        QuerySpec spec,
        bool forCount)
    {
        SqliteIdentifiers.Validate(tableName, "table");

        var byName = schema.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var leafCounter = new LeafCounter();

        var sb = new StringBuilder();
        sb.Append(forCount ? "SELECT COUNT(*)" : "SELECT *")
          .Append(" FROM \"").Append(tableName).Append('"');

        var wherePieces = new List<string>();
        if (!spec.IncludeDeleted)
            wherePieces.Add("\"is_deleted\" = 0");

        if (spec.Where.HasValue && spec.Where.Value.ValueKind != JsonValueKind.Null
            && spec.Where.Value.ValueKind != JsonValueKind.Undefined)
        {
            var compiled = CompileWhere(spec.Where.Value, byName, parameters, depth: 0, leafCounter);
            if (!string.IsNullOrEmpty(compiled))
                wherePieces.Add(compiled);
        }

        if (wherePieces.Count > 0)
            sb.Append(" WHERE ").Append(string.Join(" AND ", wherePieces));

        if (!forCount)
        {
            AppendOrderBy(sb, spec.OrderBy, byName);

            var limit = Math.Clamp(spec.Limit ?? DefaultLimit, 1, MaxLimit);
            sb.Append(" LIMIT ").Append(limit);

            if (spec.Offset is > 0)
                sb.Append(" OFFSET ").Append(spec.Offset.Value);
        }

        return new CompiledQuery(sb.ToString(), parameters);
    }

    private static void AppendOrderBy(
        StringBuilder sb,
        IReadOnlyList<OrderByClause>? orderBy,
        IReadOnlyDictionary<string, AppColumn> byName)
    {
        if (orderBy is not { Count: > 0 }) return;

        var parts = new List<string>();
        foreach (var ob in orderBy)
        {
            if (!byName.TryGetValue(ob.Field, out var col))
                throw new QueryDslException(QueryDslErrorCodes.UnknownColumn,
                    $"orderBy: column '{ob.Field}' does not exist.", ob.Field);
            if (col.Name == BaseColumns.AdditionalData)
                throw new QueryDslException(QueryDslErrorCodes.UnqueryableColumn,
                    "orderBy: 'additional_data' is a non-queryable field. Promote it to a typed column via `app_alter_table` if you need to order on it.",
                    BaseColumns.AdditionalData);
            string dir;
            if (ob.Direction.Equals("asc", StringComparison.OrdinalIgnoreCase)) dir = "ASC";
            else if (ob.Direction.Equals("desc", StringComparison.OrdinalIgnoreCase)) dir = "DESC";
            else throw new QueryDslException(QueryDslErrorCodes.BadDirection,
                $"orderBy direction must be 'asc' or 'desc'; got '{ob.Direction}'.");
            parts.Add($"\"{ob.Field}\" {dir}");
        }
        sb.Append(" ORDER BY ").Append(string.Join(", ", parts));
    }

    private static string CompileWhere(
        JsonElement where,
        IReadOnlyDictionary<string, AppColumn> byName,
        Dictionary<string, object?> parameters,
        int depth,
        LeafCounter leafs)
    {
        if (depth > MaxDepth)
            throw new QueryDslException(QueryDslErrorCodes.DepthExceeded,
                $"where depth exceeds {MaxDepth}.");
        if (where.ValueKind != JsonValueKind.Object)
            throw new QueryDslException(QueryDslErrorCodes.BadShape,
                "where must be a JSON object.");

        var pieces = new List<string>();
        foreach (var prop in where.EnumerateObject())
        {
            pieces.Add(prop.Name.StartsWith('$')
                ? CompileCombinator(prop.Name, prop.Value, byName, parameters, depth, leafs)
                : CompileColumnPredicate(prop.Name, prop.Value, byName, parameters, depth, leafs));
        }
        return pieces.Count switch
        {
            0 => "",
            1 => pieces[0],
            _ => "(" + string.Join(" AND ", pieces) + ")",
        };
    }

    private static string CompileCombinator(
        string op,
        JsonElement value,
        IReadOnlyDictionary<string, AppColumn> byName,
        Dictionary<string, object?> parameters,
        int depth,
        LeafCounter leafs)
    {
        switch (op)
        {
            case "$and":
            case "$or":
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                    throw new QueryDslException(QueryDslErrorCodes.BadCombinator,
                        $"{op} requires a non-empty array of where-clauses.");
                var joiner = op == "$and" ? " AND " : " OR ";
                var parts = new List<string>();
                foreach (var el in value.EnumerateArray())
                {
                    var inner = CompileWhere(el, byName, parameters, depth + 1, leafs);
                    if (!string.IsNullOrEmpty(inner)) parts.Add(inner);
                }
                return parts.Count switch
                {
                    0 => "",
                    1 => parts[0],
                    _ => "(" + string.Join(joiner, parts) + ")",
                };

            case "$not":
                if (value.ValueKind != JsonValueKind.Object)
                    throw new QueryDslException(QueryDslErrorCodes.BadCombinator,
                        "$not requires a where-object.");
                var notInner = CompileWhere(value, byName, parameters, depth + 1, leafs);
                return string.IsNullOrEmpty(notInner) ? "" : $"NOT ({notInner})";

            default:
                throw new QueryDslException(QueryDslErrorCodes.UnknownOperator,
                    $"Unknown top-level operator '{op}'. Use $and / $or / $not.");
        }
    }

    private static string CompileColumnPredicate(
        string columnName,
        JsonElement value,
        IReadOnlyDictionary<string, AppColumn> byName,
        Dictionary<string, object?> parameters,
        int depth,
        LeafCounter leafs)
    {
        if (!byName.TryGetValue(columnName, out var col))
            throw new QueryDslException(QueryDslErrorCodes.UnknownColumn,
                $"Column '{columnName}' does not exist on this table.", columnName);
        if (col.Name == BaseColumns.AdditionalData)
            throw new QueryDslException(QueryDslErrorCodes.UnqueryableColumn,
                "'additional_data' is a non-queryable field. Promote it to a typed column via `app_alter_table` if you need to filter on it.",
                BaseColumns.AdditionalData);

        // Sugar: `{ col: <scalar> }` desugars to `{ col: { $eq: <scalar> } }`.
        if (value.ValueKind != JsonValueKind.Object)
        {
            leafs.Bump();
            var pName = AddParam(parameters, col.Name, CoerceValue(col, value));
            return $"\"{col.Name}\" = @{pName}";
        }

        var clauses = new List<string>();
        foreach (var prop in value.EnumerateObject())
            clauses.Add(CompileOperator(col, prop.Name, prop.Value, parameters, leafs));

        return clauses.Count switch
        {
            0 => "",
            1 => clauses[0],
            _ => "(" + string.Join(" AND ", clauses) + ")",
        };
    }

    private static string CompileOperator(
        AppColumn col, string op, JsonElement value,
        Dictionary<string, object?> parameters, LeafCounter leafs)
    {
        leafs.Bump();
        switch (op)
        {
            case "$eq":
                return $"\"{col.Name}\" = @{AddParam(parameters, col.Name, CoerceValue(col, value))}";
            case "$ne":
                return $"\"{col.Name}\" <> @{AddParam(parameters, col.Name, CoerceValue(col, value))}";
            case "$lt":
            case "$lte":
            case "$gt":
            case "$gte":
                EnsureComparable(col, op);
                var sqlOp = op switch
                {
                    "$lt" => "<",
                    "$lte" => "<=",
                    "$gt" => ">",
                    "$gte" => ">=",
                    _ => throw new InvalidOperationException(),
                };
                return $"\"{col.Name}\" {sqlOp} @{AddParam(parameters, col.Name, CoerceValue(col, value))}";
            case "$like":
                if (col.Type != AppColumnType.Text)
                    throw new QueryDslException(QueryDslErrorCodes.OperatorNotAllowed,
                        $"$like is allowed on text columns only; '{col.Name}' is {col.Type.ToWireName()}.",
                        col.Name);
                if (value.ValueKind != JsonValueKind.String)
                    throw new QueryDslException(QueryDslErrorCodes.TypeMismatch,
                        $"$like value must be a string.", col.Name);
                return $"\"{col.Name}\" LIKE @{AddParam(parameters, col.Name, value.GetString())}";
            case "$in":
                if (value.ValueKind != JsonValueKind.Array)
                    throw new QueryDslException(QueryDslErrorCodes.BadShape,
                        "$in value must be an array.", col.Name);
                var n = value.GetArrayLength();
                if (n == 0)
                    throw new QueryDslException(QueryDslErrorCodes.BadShape,
                        "$in array must not be empty.", col.Name);
                if (n > MaxInElements)
                    throw new QueryDslException(QueryDslErrorCodes.InTooLarge,
                        $"$in array length {n} exceeds {MaxInElements}.", col.Name);
                var placeholders = new List<string>(n);
                foreach (var el in value.EnumerateArray())
                {
                    placeholders.Add("@" + AddParam(parameters, col.Name, CoerceValue(col, el)));
                }
                return $"\"{col.Name}\" IN (" + string.Join(", ", placeholders) + ")";
            case "$isNull":
                if (value.ValueKind != JsonValueKind.True)
                    throw new QueryDslException(QueryDslErrorCodes.BadShape,
                        "$isNull value must be literal true.", col.Name);
                return $"\"{col.Name}\" IS NULL";
            case "$isNotNull":
                if (value.ValueKind != JsonValueKind.True)
                    throw new QueryDslException(QueryDslErrorCodes.BadShape,
                        "$isNotNull value must be literal true.", col.Name);
                return $"\"{col.Name}\" IS NOT NULL";
            default:
                throw new QueryDslException(QueryDslErrorCodes.UnknownOperator,
                    $"Unknown operator '{op}' on column '{col.Name}'.", col.Name);
        }
    }

    private static void EnsureComparable(AppColumn col, string op)
    {
        if (col.Type is AppColumnType.Integer or AppColumnType.Real or AppColumnType.DateTime
            or AppColumnType.Text)
            return; // text/datetime sort lexically, which is right for ISO-8601
        throw new QueryDslException(QueryDslErrorCodes.OperatorNotAllowed,
            $"{op} is not allowed on {col.Type.ToWireName()} columns.", col.Name);
    }

    // CLR value the parameter dictionary should hold. Booleans collapse to 0/1
    // for Integer/Boolean columns; numbers stay as long/double; strings pass
    // through (DateTime affinity is ISO-8601 text in SQLite).
    private static object? CoerceValue(AppColumn col, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        switch (col.Type)
        {
            case AppColumnType.Text:
            case AppColumnType.DateTime:
            case AppColumnType.Json:
                if (value.ValueKind == JsonValueKind.String) return value.GetString();
                throw new QueryDslException(QueryDslErrorCodes.TypeMismatch,
                    $"Column '{col.Name}' expects {col.Type.ToWireName()}; got {value.ValueKind}.",
                    col.Name);
            case AppColumnType.Integer:
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l)) return l;
                if (value.ValueKind == JsonValueKind.True) return 1L;
                if (value.ValueKind == JsonValueKind.False) return 0L;
                throw new QueryDslException(QueryDslErrorCodes.TypeMismatch,
                    $"Column '{col.Name}' expects integer; got {value.ValueKind}.", col.Name);
            case AppColumnType.Real:
                if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
                throw new QueryDslException(QueryDslErrorCodes.TypeMismatch,
                    $"Column '{col.Name}' expects real; got {value.ValueKind}.", col.Name);
            case AppColumnType.Boolean:
                if (value.ValueKind == JsonValueKind.True) return 1L;
                if (value.ValueKind == JsonValueKind.False) return 0L;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var bi)
                    && (bi == 0 || bi == 1)) return bi;
                throw new QueryDslException(QueryDslErrorCodes.TypeMismatch,
                    $"Column '{col.Name}' expects boolean; got {value.ValueKind}.", col.Name);
            default:
                throw new InvalidOperationException($"Unsupported column type {col.Type}");
        }
    }

    // Dapper expects unique parameter names per CommandDefinition. We mint
    // monotonically by column name + a per-parameter index to keep generated
    // SQL readable in logs without colliding when the same column is filtered
    // twice (e.g. `amount: { $gt: 0, $lt: 100 }`).
    private static string AddParam(Dictionary<string, object?> parameters, string baseName, object? value)
    {
        var name = $"{baseName}_{parameters.Count}";
        parameters[name] = value;
        return name;
    }

    private sealed class LeafCounter
    {
        public int Count { get; private set; }
        public void Bump()
        {
            Count++;
            if (Count > MaxLeaves)
                throw new QueryDslException(QueryDslErrorCodes.LeavesExceeded,
                    $"where tree has more than {MaxLeaves} predicates.");
        }
    }
}

public sealed record CompiledQuery(string Sql, IReadOnlyDictionary<string, object?> Parameters);

public sealed record OrderByClause(string Field, string Direction);

public sealed record QuerySpec(
    JsonElement? Where = null,
    IReadOnlyList<OrderByClause>? OrderBy = null,
    int? Limit = null,
    int? Offset = null,
    bool IncludeDeleted = false);
