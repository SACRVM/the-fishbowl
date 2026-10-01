using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fishbowl.Core.Tables;

// The wire shape of a table definition, shared by REST and MCP: parsing a
// column someone declared, and describing a table back.
public static class TableJson
{
    public static ColumnDef ParseColumn(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new TableException("column_invalid", "A column is an object: { name, type, … }.");
        string? Str(string name) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(string name) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var name = Str("name") ?? throw new TableException("column_invalid", "A column needs a name.");
        var type = Str("type");
        var kind = ColumnKinds.TryParse(type) ?? throw new TableException("column_type",
            $"Column '{name}': type must be one of {string.Join(", ", ColumnKinds.WireNames)}.", args: new { column = name, types = string.Join(", ", ColumnKinds.WireNames) });
        List<string>? options = null;
        if (el.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array)
            options = o.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.ToString()).ToList();
        JsonElement? dflt = el.TryGetProperty("default", out var d) && d.ValueKind != JsonValueKind.Null ? d.Clone() : null;
        return new ColumnDef(name, kind, Str("description"), Bool("required"), dflt, Bool("unique"), options, Bool("multiple"), Str("link"), Bool("searchable"));
    }

    public static List<ColumnDef> ParseColumns(JsonElement arr) =>
        arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Select(ParseColumn).ToList() : new List<ColumnDef>();

    public static object Describe(TableDef t, long? rows = null) => new
    {
        name = t.Name,
        description = t.Description,
        ownRows = t.OwnRows,
        rows,
        createdAt = t.CreatedAt,
        columns = t.Columns.Select(DescribeColumn),
    };

    public static object DescribeColumn(ColumnDef c) => new
    {
        name = c.Name,
        type = c.Kind.ToWire(),
        description = c.Description,
        required = c.Required,
        @default = c.Default,
        unique = c.Unique,
        options = c.Options,
        multiple = c.Multiple,
        link = c.LinkTarget,
        searchable = c.Searchable,
    };

    // One table as a short markdown block — what a small agent reads first.
    public static string Markdown(TableDef t, long rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("## ").Append(t.Name).Append(" (").Append(rows).AppendLine(" rows)");
        if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine(t.Description);
        if (t.OwnRows) sb.AppendLine("Members edit only their own rows.");
        sb.AppendLine().AppendLine("| column | type | notes |").AppendLine("| --- | --- | --- |");
        sb.AppendLine("| title | text | required, names the row |");
        foreach (var c in t.Columns)
        {
            var type = c.Kind.ToWire() + (c.Multiple ? " (multiple)" : "") + (c.LinkTarget is { } l ? " → " + l : "");
            var notes = new List<string>();
            if (c.Required) notes.Add("required");
            if (c.Unique) notes.Add("unique");
            if (c.Searchable) notes.Add("searchable");
            if (c.Options is { Count: > 0 }) notes.Add("one of: " + string.Join(", ", c.Options));
            if (c.Default is { } d) notes.Add("default " + d.GetRawText());
            if (!string.IsNullOrWhiteSpace(c.Description)) notes.Add(c.Description!);
            sb.Append("| ").Append(c.Name).Append(" | ").Append(type).Append(" | ").Append(string.Join("; ", notes).Replace("|", "/")).AppendLine(" |");
        }
        sb.AppendLine("Every row also has id, author, created_at, last_modified, row_version (read-only) and additional_data (free JSON).");
        return sb.ToString();
    }

    public static JsonElement ToElement(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // An ALTER as REST and MCP send it.
    public static Fishbowl.Core.Repositories.TableChange ParseChange(JsonElement b)
    {
        TableException Bad(string what) => new("change_invalid", $"The change is malformed: {what}.", args: new { reason = what });
        if (b.ValueKind != JsonValueKind.Object) throw Bad("send a JSON object");
        if (b.TryGetProperty("dropColumns", out var dc) && (dc.ValueKind != JsonValueKind.Array || dc.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)))
            throw Bad("dropColumns is a list of column names");
        if (b.TryGetProperty("renameColumns", out var rc) && (rc.ValueKind != JsonValueKind.Object || rc.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String)))
            throw Bad("renameColumns maps old names to new names");
        if (b.TryGetProperty("updateColumns", out var uc) && (uc.ValueKind != JsonValueKind.Object || uc.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.Object)))
            throw Bad("updateColumns maps column names to objects");
        if (b.TryGetProperty("addColumns", out var ac) && ac.ValueKind != JsonValueKind.Array)
            throw Bad("addColumns is a list of columns");
        bool? ownRows = b.TryGetProperty("ownRows", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False ? o.GetBoolean() : null;
        var add = b.TryGetProperty("addColumns", out var a) ? TableJson.ParseColumns(a) : null;
        var drop = b.TryGetProperty("dropColumns", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : null;
        var rename = b.TryGetProperty("renameColumns", out var r) && r.ValueKind == JsonValueKind.Object
            ? r.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "") : null;
        Dictionary<string, Fishbowl.Core.Repositories.ColumnUpdate>? update = null;
        if (b.TryGetProperty("updateColumns", out var u) && u.ValueKind == JsonValueKind.Object)
            update = u.EnumerateObject().ToDictionary(p => p.Name, p => new Fishbowl.Core.Repositories.ColumnUpdate(
                Str(p.Value, "description"),
                p.Value.TryGetProperty("addOptions", out var ao) && ao.ValueKind == JsonValueKind.Array
                    ? ao.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : null,
                p.Value.TryGetProperty("required", out var rq) && rq.ValueKind is JsonValueKind.True or JsonValueKind.False ? rq.GetBoolean() : null,
                p.Value.TryGetProperty("searchable", out var sr) && sr.ValueKind is JsonValueKind.True or JsonValueKind.False ? sr.GetBoolean() : null));
        return new Fishbowl.Core.Repositories.TableChange(Str(b, "description"), ownRows, add, drop, rename, update);
    }

}
