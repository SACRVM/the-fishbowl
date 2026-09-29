using System.Text.Json;
using System.Text.RegularExpressions;
using Fishbowl.Core.Apps;

namespace Fishbowl.Core.Tables;

// Space tables (space-apps spec, phase 3): a space is a database, and these
// are its own tables beside notes, events, todos and contacts. Columns carry
// a type that says what is meant — the row layer validates values against it
// and the Tables app knows how to show them.
public enum ColumnKind
{
    Text,
    LongText,   // markdown
    Integer,
    Decimal,
    YesNo,
    Date,       // YYYY-MM-DD
    DateTime,   // ISO-8601, stored UTC
    Choice,     // one of the column's options (or several: Multiple)
    Member,     // a person of the space (user id)
    File,       // a path in the space's files
    Link,       // a row of a space table, or a note/event/todo/contact
}

public static class ColumnKinds
{
    private static readonly Dictionary<string, ColumnKind> ByWire = new(StringComparer.Ordinal)
    {
        ["text"] = ColumnKind.Text,
        ["longtext"] = ColumnKind.LongText,
        ["integer"] = ColumnKind.Integer,
        ["decimal"] = ColumnKind.Decimal,
        ["yesno"] = ColumnKind.YesNo,
        ["date"] = ColumnKind.Date,
        ["datetime"] = ColumnKind.DateTime,
        ["choice"] = ColumnKind.Choice,
        ["member"] = ColumnKind.Member,
        ["file"] = ColumnKind.File,
        ["link"] = ColumnKind.Link,
    };

    public static IReadOnlyCollection<string> WireNames => ByWire.Keys;

    public static string ToWire(this ColumnKind kind) => ByWire.First(kv => kv.Value == kind).Key;

    public static ColumnKind? TryParse(string? wire) =>
        wire is not null && ByWire.TryGetValue(wire.Trim().ToLowerInvariant(), out var k) ? k : null;

    public static string SqlType(this ColumnKind kind) => kind switch
    {
        ColumnKind.Integer or ColumnKind.YesNo => "INTEGER",
        ColumnKind.Decimal => "REAL",
        _ => "TEXT",
    };

    // How the query DSL sees the column (its operators and value coercion).
    public static AppColumnType QueryType(this ColumnDef c) => c.Multiple
        ? AppColumnType.Json
        : c.Kind switch
        {
            ColumnKind.Integer => AppColumnType.Integer,
            ColumnKind.Decimal => AppColumnType.Real,
            ColumnKind.YesNo => AppColumnType.Boolean,
            ColumnKind.DateTime => AppColumnType.DateTime,
            _ => AppColumnType.Text,
        };
}

public sealed record ColumnDef(
    string Name,
    ColumnKind Kind,
    string? Description = null,
    bool Required = false,
    JsonElement? Default = null,
    bool Unique = false,
    IReadOnlyList<string>? Options = null,
    bool Multiple = false,
    string? LinkTarget = null);

public sealed record TableDef(
    string Name,
    string? Description,
    bool OwnRows,
    IReadOnlyList<ColumnDef> Columns,
    DateTime CreatedAt,
    string? CreatedBy);

// The row columns every table has, server-set except title.
public static class TableBaseColumns
{
    public const string Id = "id";
    public const string Title = "title";
    public const string Author = "author";
    public const string CreatedAt = "created_at";
    public const string LastModified = "last_modified";
    public const string RowVersion = "row_version";
    public const string AdditionalData = "additional_data";

    public static readonly IReadOnlyList<string> All =
        new[] { Id, Title, Author, CreatedAt, LastModified, RowVersion, AdditionalData };

    public static bool Is(string name) => All.Contains(name, StringComparer.Ordinal);

    public const string Ddl =
        "id TEXT PRIMARY KEY, title TEXT NOT NULL, author TEXT, created_at TEXT NOT NULL, " +
        "last_modified TEXT NOT NULL, row_version INTEGER NOT NULL DEFAULT 1, additional_data TEXT";

    // For the query DSL: the base columns with their types.
    public static readonly IReadOnlyList<AppColumn> Query = new[]
    {
        new AppColumn(Id, AppColumnType.Text, Nullable: false, IsBaseColumn: true),
        new AppColumn(Title, AppColumnType.Text, Nullable: false, IsBaseColumn: true),
        new AppColumn(Author, AppColumnType.Text, IsBaseColumn: true),
        new AppColumn(CreatedAt, AppColumnType.DateTime, Nullable: false, IsBaseColumn: true),
        new AppColumn(LastModified, AppColumnType.DateTime, Nullable: false, IsBaseColumn: true),
        new AppColumn(RowVersion, AppColumnType.Integer, Nullable: false, IsBaseColumn: true),
        new AppColumn(AdditionalData, AppColumnType.Json, IsBaseColumn: true),
    };
}

public static class TableNames
{
    // What a link may point at besides the space's own tables: the space's
    // built-ins, by their table names.
    public static readonly IReadOnlyList<string> BuiltIns = new[] { "notes", "events", "todos", "contacts" };

    private static readonly Regex Valid = new("^[a-z][a-z0-9_]{0,39}$", RegexOptions.Compiled);

    public static bool IsValid(string? name) =>
        name is not null && Valid.IsMatch(name) && !name.Contains("__") && !name.EndsWith('_');

    // Where a table's rows live — the prefix keeps them apart from Fishbowl's
    // own tables in space.db, whatever a space calls its tables.
    public static string Physical(string table) => "t_" + table;

    // A "multiple" link column's join table.
    public static string Join(string table, string column) => $"t_{table}__{column}";

    public static string UniqueIndex(string table, string column) => $"u_{table}__{column}";
    public static string LinkIndex(string table, string column) => $"x_{table}__{column}";

    // The physical table behind a link target.
    public static string TargetTable(string target) =>
        BuiltIns.Contains(target, StringComparer.Ordinal) ? target : Physical(target);
}

// Every refusal of the table layer: a code the client translates, the HTTP
// status it means, and the arguments that fill the message. Derives from
// InvalidOperationException so MCP reports it as fixable input.
public sealed class TableException : InvalidOperationException
{
    public string Code { get; }
    public int Status { get; }
    public object? Args { get; }

    public TableException(string code, string message, int status = 400, object? args = null) : base(message)
    {
        Code = code;
        Status = status;
        Args = args;
    }

    public static TableException NotFound(string code, string message, object? args = null) => new(code, message, 404, args);
    public static TableException Conflict(string code, string message, object? args = null) => new(code, message, 409, args);
    public static TableException Forbidden(string code, string message, object? args = null) => new(code, message, 403, args);
}

// Who is acting on a space's tables, and what their role lets them do.
public sealed record TableActor(string UserId, bool CanWrite, bool CanDesign)
{
    // Designer and above may change anyone's rows even in an own-rows table.
    public bool EditsAllRows => CanDesign;
}

public sealed record AggregateResult(object? Group, double? Value);
