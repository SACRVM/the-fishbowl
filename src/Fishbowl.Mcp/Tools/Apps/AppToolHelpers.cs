using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Mcp;

namespace Fishbowl.Mcp.Tools.Apps;

// MCP-tool-side helper: resolves the AppRef from the principal and re-exports
// AppJsonParsers methods at this namespace so the 13 tool files don't need to
// import two static classes. The real parsing logic lives in
// Fishbowl.Core.Apps.AppJsonParsers (shared with AppsApi.cs).
internal static class AppToolHelpers
{
    // Each tool double-checks the AppRef triple matches the ctx tag — guards
    // against a token where the owner_* and context_id claims drifted apart
    // (only possible from a bad mint).
    public static AppRef ResolveAppRef(ContextRef ctx, ClaimsPrincipal principal)
    {
        var appRef = McpContextClaims.ResolveApp(principal);
        if (ctx.Type != ContextType.App || ctx.Id != appRef.AppId)
            throw new InvalidOperationException(
                "Token context (app id) and routing claims (owner+app) do not agree. Re-mint the key.");
        return appRef;
    }

    public static string RequireString(JsonElement args, string name) => AppJsonParsers.RequireString(args, name);
    public static string? OptionalString(JsonElement args, string name) => AppJsonParsers.OptionalString(args, name);
    public static bool OptionalBool(JsonElement args, string name, bool fallback = false)
        => AppJsonParsers.OptionalBool(args, name, fallback);
    public static int? OptionalInt(JsonElement args, string name) => AppJsonParsers.OptionalInt(args, name);
    public static IReadOnlyDictionary<string, object?> ParseObject(JsonElement args, string name)
        => AppJsonParsers.ParseObject(args, name);
    public static IReadOnlyDictionary<string, object?> ToDict(JsonElement obj) => AppJsonParsers.ToDict(obj);
    public static object? ToClrValue(JsonElement el) => AppJsonParsers.ToClrValue(el);
    public static List<AppColumn> ParseColumns(JsonElement args, string name)
        => AppJsonParsers.ParseColumns(args, name);
    public static List<OrderByClause>? ParseOrderBy(JsonElement args) => AppJsonParsers.ParseOrderBy(args);
    public static QuerySpec ParseQuerySpec(JsonElement args) => AppJsonParsers.ParseQuerySpec(args);
}
