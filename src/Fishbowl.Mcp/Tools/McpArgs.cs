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
}
