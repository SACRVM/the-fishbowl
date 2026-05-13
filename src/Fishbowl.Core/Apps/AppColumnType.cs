namespace Fishbowl.Core.Apps;

// The six user-defined column types Apps may declare. Booleans + datetimes
// store in SQLite-native shapes (INTEGER 0/1, ISO-8601 TEXT) to match the
// rest of the codebase; the typed surface stays explicit so the row layer
// can validate values before write and the DSL knows which operators apply.
public enum AppColumnType
{
    Text,
    Integer,
    Real,
    Boolean,
    DateTime,
    Json,
}

public static class AppColumnTypeExtensions
{
    public static string ToSqlType(this AppColumnType t) => t switch
    {
        AppColumnType.Text => "TEXT",
        AppColumnType.Integer => "INTEGER",
        AppColumnType.Real => "REAL",
        AppColumnType.Boolean => "INTEGER",
        AppColumnType.DateTime => "TEXT",
        AppColumnType.Json => "TEXT",
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, null),
    };

    public static string ToWireName(this AppColumnType t) => t switch
    {
        AppColumnType.Text => "text",
        AppColumnType.Integer => "integer",
        AppColumnType.Real => "real",
        AppColumnType.Boolean => "boolean",
        AppColumnType.DateTime => "datetime",
        AppColumnType.Json => "json",
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, null),
    };

    public static bool TryParse(string? raw, out AppColumnType type)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "text": type = AppColumnType.Text; return true;
            case "integer": type = AppColumnType.Integer; return true;
            case "real": type = AppColumnType.Real; return true;
            case "boolean": type = AppColumnType.Boolean; return true;
            case "datetime": type = AppColumnType.DateTime; return true;
            case "json": type = AppColumnType.Json; return true;
            default: type = default; return false;
        }
    }

    public static AppColumnType Parse(string raw)
        => TryParse(raw, out var t)
            ? t
            : throw new ArgumentException($"Unknown column type '{raw}'.", nameof(raw));
}
