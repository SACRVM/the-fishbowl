using System.Text.Json;

namespace Fishbowl.Mcp.Tools;

// Argument reads that turn a caller's mistake into an ArgumentException
// (→ InvalidParams), never an InternalError.
internal static class McpArgs
{
    public static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)
            ? n : throw new ArgumentException($"`{name}` must be a whole number.");

    public static string RequiredString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : throw new ArgumentException($"`{name}` is required.");

    // A value of the wrong JSON kind throws what JsonElement throws
    // (InvalidOperationException) — the dispatcher's fixed sentence for it.

    /// <summary>A string argument, or null when it is absent or null.</summary>
    public static string? String(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return null;
        return v.GetString();
    }

    /// <summary>A list of strings (blanks dropped), or null when it is absent.</summary>
    public static List<string>? Strings(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return null;
        return v.EnumerateArray().Select(e => e.GetString()!).Where(s => s.Trim().Length > 0).ToList();
    }

    /// <summary>A boolean argument, or null when it is absent or null.</summary>
    public static bool? Bool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return null;
        return v.GetBoolean();
    }
}
