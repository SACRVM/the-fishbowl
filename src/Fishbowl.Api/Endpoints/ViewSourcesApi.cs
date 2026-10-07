using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// Which workspaces the personal Calendar and Contacts show (spaces as sources,
// docs/superpowers/specs/2026-10-07-space-sources-design.md): per user, in
// system_config `View:Sources:<userId>` — the Messages:Muted pattern. A source
// is "personal" or a space id; only spaces the caller is a member of count, on
// read and on write, so leaving a space drops it. Unset, an app shows
// personal alone. Cookie only — a person's view, not a key's.
//
//   GET /api/v1/me/sources                        { calendar: [...], contacts: [...] }
//   PUT /api/v1/me/sources  { app, source, on }   the same, after the change
public static class ViewSourcesApi
{
    public const string KeyPrefix = "View:Sources:";
    public const string Personal = "personal";
    public static readonly string[] Apps = { "calendar", "contacts" };

    public sealed record SourceRequest(string? App, string? Source, bool On);

    public static IEndpointRouteBuilder MapViewSourcesApi(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/me/sources").RequireAuthorization();
        g.MapGet("/", async (ClaimsPrincipal user, ISystemRepository system, ISpaceRepository spaces, CancellationToken ct) =>
        {
            if (Me(user) is not { } me) return Results.Forbid();
            return Results.Ok(await ReadAsync(system, me, await MemberOfAsync(spaces, me, ct), ct));
        }).WithName("GetViewSources").WithSummary("Which workspaces the caller's personal Calendar and Contacts show. Cookie only.");

        g.MapPut("/", async (SourceRequest body, ClaimsPrincipal user, ISystemRepository system, ISpaceRepository spaces, CancellationToken ct) =>
        {
            if (Me(user) is not { } me) return Results.Forbid();
            var member = await MemberOfAsync(spaces, me, ct);
            if (body.App is null || !Apps.Contains(body.App) || body.Source is null
                || (body.Source != Personal && !member.Contains(body.Source)))
                return ApiErrors.BadRequest("invalid_source", "Switch \"personal\" or one of your spaces (its id) for \"calendar\" or \"contacts\".");
            var sources = await ReadAsync(system, me, member, ct);
            var set = new HashSet<string>(sources[body.App], StringComparer.Ordinal);
            if (body.On) set.Add(body.Source); else set.Remove(body.Source);
            sources[body.App] = Ordered(set);
            await system.SetConfigAsync(KeyPrefix + me, JsonSerializer.Serialize(sources), ct);
            return Results.Ok(sources);
        }).WithName("SetViewSource").WithSummary("Switches one source of the caller's personal Calendar or Contacts on or off. Cookie only.");
        return routes;
    }

    private static string? Me(ClaimsPrincipal user) =>
        user.Identity?.AuthenticationType == McpContextClaims.BearerScheme ? null : user.FindFirst(McpContextClaims.UserId)?.Value;

    private static async Task<HashSet<string>> MemberOfAsync(ISpaceRepository spaces, string userId, CancellationToken ct) =>
        (await spaces.ListByMemberAsync(userId, ct)).Select(m => m.Space.Id).ToHashSet(StringComparer.Ordinal);

    // The stored lists, kept to personal and the caller's spaces; an app
    // without a list shows personal alone.
    private static async Task<Dictionary<string, string[]>> ReadAsync(ISystemRepository system, string userId, HashSet<string> member, CancellationToken ct)
    {
        Dictionary<string, string[]>? stored = null;
        var raw = await system.GetConfigAsync(KeyPrefix + userId, ct);
        if (!string.IsNullOrEmpty(raw))
        {
            try { stored = JsonSerializer.Deserialize<Dictionary<string, string[]>>(raw); }
            catch (JsonException) { /* unreadable: the defaults */ }
        }
        var result = new Dictionary<string, string[]>();
        foreach (var app in Apps)
        {
            result[app] = stored is not null && stored.TryGetValue(app, out var list) && list is not null
                ? Ordered(list.Where(s => s == Personal || member.Contains(s)))
                : new[] { Personal };
        }
        return result;
    }

    private static string[] Ordered(IEnumerable<string> sources) =>
        sources.Distinct(StringComparer.Ordinal).OrderBy(s => s == Personal ? 0 : 1).ThenBy(s => s, StringComparer.Ordinal).ToArray();
}
