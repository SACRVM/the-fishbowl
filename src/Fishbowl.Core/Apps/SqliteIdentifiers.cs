using System.Text.RegularExpressions;

namespace Fishbowl.Core.Apps;

// Identifier validation for app-DB table/column names. The narrow charset
// (^[a-z_][a-z0-9_]*$) keeps DDL safe to interpolate without quoting in
// generated SQL and predictable across casing-sensitive filesystems / clients.
// Reserved keywords are rejected even though we'd technically quote them —
// allowing 'select' as a column name is a footgun, not a feature.
public static class SqliteIdentifiers
{
    private static readonly Regex NameRegex = new(
        "^[a-z_][a-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const int MaxLength = 63;

    // Reserved-in-some-context SQLite keywords. List drawn from
    // https://www.sqlite.org/lang_keywords.html — comparison is
    // case-insensitive because the regex already pins lower-case input.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "abort", "action", "add", "after", "all", "alter", "analyze", "and", "as", "asc",
        "attach", "autoincrement", "before", "begin", "between", "by", "cascade", "case",
        "cast", "check", "collate", "column", "commit", "conflict", "constraint", "create",
        "cross", "current_date", "current_time", "current_timestamp", "database", "default",
        "deferrable", "deferred", "delete", "desc", "detach", "distinct", "drop", "each",
        "else", "end", "escape", "except", "exclusive", "exists", "explain", "fail", "for",
        "foreign", "from", "full", "glob", "group", "having", "if", "ignore", "immediate",
        "in", "index", "indexed", "initially", "inner", "insert", "instead", "intersect",
        "into", "is", "isnull", "join", "key", "left", "like", "limit", "match", "natural",
        "no", "not", "notnull", "null", "of", "offset", "on", "or", "order", "outer", "plan",
        "pragma", "primary", "query", "raise", "references", "regexp", "reindex", "release",
        "rename", "replace", "restrict", "right", "rollback", "row", "savepoint", "select",
        "set", "table", "temp", "temporary", "then", "to", "transaction", "trigger", "union",
        "unique", "update", "using", "vacuum", "values", "view", "virtual", "when", "where",
        "with", "without",
    };

    public static void Validate(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"{kind} name is required.", nameof(name));
        if (name.Length > MaxLength)
            throw new ArgumentException(
                $"{kind} name '{name}' exceeds {MaxLength} chars.", nameof(name));
        if (!NameRegex.IsMatch(name))
            throw new ArgumentException(
                $"{kind} name '{name}' must match {NameRegex} (lower-case ASCII, digits, underscores; cannot start with a digit).",
                nameof(name));
        if (Reserved.Contains(name))
            throw new ArgumentException(
                $"{kind} name '{name}' is a reserved SQL keyword.", nameof(name));
    }

    public static bool IsValid(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxLength) return false;
        return NameRegex.IsMatch(name) && !Reserved.Contains(name);
    }
}
