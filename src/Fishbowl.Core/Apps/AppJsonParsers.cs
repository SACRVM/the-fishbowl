using System.Text.Json;

namespace Fishbowl.Core.Apps;

// JSON → typed DSL value plumbing shared by the MCP tools and the REST mirror.
// Both surfaces accept the same wire shapes (columns, query specs, row dicts);
// keeping the conversion here means a tweak to default/limit/orderBy parsing
// only has to land once.
public static class AppJsonParsers
{
    public static string RequireString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"'{name}' is required and must be a string", nameof(args));
        var s = v.GetString();
        if (string.IsNullOrWhiteSpace(s))
            throw new ArgumentException($"'{name}' is required", nameof(args));
        return s!;
    }

    public static string? OptionalString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        return v.GetString();
    }

    public static bool OptionalBool(JsonElement args, string name, bool fallback = false)
    {
        if (!args.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    public static int? OptionalInt(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        return v.TryGetInt32(out var i) ? i : null;
    }

    public static IReadOnlyDictionary<string, object?> ParseObject(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"'{name}' is required and must be an object", nameof(args));
        return ToDict(v);
    }

    public static IReadOnlyDictionary<string, object?> ToDict(JsonElement obj)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (obj.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in obj.EnumerateObject())
            dict[prop.Name] = ToClrValue(prop.Value);
        return dict;
    }

    // Lossy by design: JSON has fewer numeric types than .NET. We pick the
    // canonical CLR type each repository validates against (long for integers,
    // double for fractional, bool for booleans, string for everything else).
    public static object? ToClrValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        // Objects / arrays survive as raw JSON text — agents pushing nested
        // structures into a column today route through additional_data; future
        // typed JSON columns will likely want the raw text intact too.
        _ => el.GetRawText(),
    };

    public static List<AppColumn> ParseColumns(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"'{name}' must be an array of column descriptors", nameof(args));

        var result = new List<AppColumn>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("each column must be an object", nameof(args));
            result.Add(ParseColumn(el));
        }
        return result;
    }

    public static AppColumn ParseColumn(JsonElement el)
    {
        var name = RequireString(el, "name");
        var type = AppColumnTypeExtensions.Parse(RequireString(el, "type"));
        var nullable = OptionalBool(el, "nullable", fallback: true);
        var unique = OptionalBool(el, "unique", fallback: false);
        object? def = null;
        if (el.TryGetProperty("default", out var defEl) && defEl.ValueKind != JsonValueKind.Null)
            def = ToClrValue(defEl);
        return new AppColumn(name, type, nullable, def, unique);
    }

    public static List<OrderByClause>? ParseOrderBy(JsonElement args)
    {
        if (!args.TryGetProperty("orderBy", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<OrderByClause>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            list.Add(new OrderByClause(
                Field: RequireString(el, "field"),
                Direction: OptionalString(el, "dir") ?? "asc"));
        }
        return list.Count == 0 ? null : list;
    }

    public static QuerySpec ParseQuerySpec(JsonElement args)
    {
        JsonElement? where = null;
        if (args.TryGetProperty("where", out var w) && w.ValueKind != JsonValueKind.Null
            && w.ValueKind != JsonValueKind.Undefined)
            where = w;
        return new QuerySpec(
            Where: where,
            OrderBy: ParseOrderBy(args),
            Limit: OptionalInt(args, "limit"),
            Offset: OptionalInt(args, "offset"),
            IncludeDeleted: OptionalBool(args, "includeDeleted"));
    }
}
