using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Desktop;
using Fishbowl.Core.Files;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// Messages from a space's own apps (space-apps spec, decision 18): an app
// tells members of its space something — never anyone else. The server sets
// the sender (space · app), skips whoever muted that app or space, and holds
// each app to a rate limit. The message is an ordinary system message
// (kind app.message), so the inbox, the badge and the chat line come along.
//
//   POST /api/v1/spaces/<slug>/apps/<folder>/notify  { to: [ids] | "all", text }
//        cookie only (the browser, for the app), Member+
//   GET  /api/v1/messages/muted                       what the caller muted
//   PUT  /api/v1/messages/muted  { space, app?, muted }
public static class AppMessagesApi
{
    public const int MaxText = 500;
    public const int PerHour = 30;
    public const string MutedKeyPrefix = "Messages:Muted:";

    public sealed record NotifyRequest(JsonElement To, string? Text);
    public sealed record MuteRequest(string? Space, string? App, bool Muted);

    public static IEndpointRouteBuilder MapAppMessagesApi(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/spaces/{slug}/apps/{folder}/notify", NotifyAsync)
            .RequireAuthorization()
            .WithName("NotifyFromSpaceApp")
            .WithSummary("A space app's message to members of its space ({ to: [ids] | \"all\", text }). Cookie only, Member+.");

        var g = routes.MapGroup("/api/v1/messages/muted").RequireAuthorization();
        g.MapGet("/", async (ClaimsPrincipal user, ISystemRepository system, ISpaceRepository spaces, CancellationToken ct) =>
        {
            if (Me(user) is not { } me) return Results.Forbid();
            var list = new List<object>();
            foreach (var key in await MutedAsync(system, me, ct))
            {
                var parts = key.Split('/', 2);
                var space = await spaces.GetByIdAsync(parts[0], ct);
                list.Add(new { space = parts[0], spaceName = space?.Name, app = parts.Length > 1 ? parts[1] : null });
            }
            return Results.Ok(new { muted = list });
        }).WithName("ListMutedApps").WithSummary("The spaces and space apps whose messages the caller muted.");

        g.MapPut("/", async (MuteRequest body, ClaimsPrincipal user, ISystemRepository system, CancellationToken ct) =>
        {
            if (Me(user) is not { } me) return Results.Forbid();
            if (string.IsNullOrEmpty(body.Space) || body.Space.Length > 40 || !body.Space.All(char.IsAsciiLetterOrDigit)
                || (body.App is not null && !SpaceApps.IsFolder(body.App)))
                return ApiErrors.BadRequest("invalid_mute", "Mute a space (its id) or one of its apps (space id + app folder).");
            var key = body.App is null ? body.Space : $"{body.Space}/{body.App}";
            var set = (await MutedAsync(system, me, ct)).ToHashSet(StringComparer.Ordinal);
            if (body.Muted) set.Add(key); else set.Remove(key);
            await system.SetConfigAsync(MutedKeyPrefix + me, JsonSerializer.Serialize(set.OrderBy(k => k, StringComparer.Ordinal)), ct);
            return Results.NoContent();
        }).WithName("SetMutedApp").WithSummary("Mutes or unmutes a space's or a space app's messages for the caller.");
        return routes;
    }

    private static string? Me(ClaimsPrincipal user) =>
        user.Identity?.AuthenticationType == McpContextClaims.BearerScheme ? null : user.FindFirst(McpContextClaims.UserId)?.Value;

    private static async Task<IReadOnlyList<string>> MutedAsync(ISystemRepository system, string userId, CancellationToken ct)
    {
        var raw = await system.GetConfigAsync(MutedKeyPrefix + userId, ct);
        if (string.IsNullOrEmpty(raw)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(raw) ?? Array.Empty<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    // Per app (space id + folder), the send times of the last hour.
    private static readonly HourlyLimit Sent = new(PerHour);

    private static async Task<IResult> NotifyAsync(string slug, string folder, NotifyRequest body, HttpContext http,
        ISpaceRepository spaces, ISystemRepository system, IFileService files, IMessageRepository messages, CancellationToken ct)
    {
        if (Me(http.User) is not { } me) return Results.Forbid();
        var resolved = await SpacesApi.ResolveSpaceAsync(slug, http.User, spaces, ct);
        if (resolved.Error is not null) return resolved.Error;
        if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();
        var space = resolved.Space!;
        var ctx = ContextRef.Space(space.Id);

        var text = body.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxText)
            return ApiErrors.BadRequest("invalid_app_message", $"A message needs text, at most {MaxText} characters.");
        if (!SpaceApps.IsFolder(folder) || await SpaceAppCatalog.ReadAsync(files, ctx, folder, ct) is not { } app)
            return ApiErrors.Json(404, "app_missing", "There is no such app in this space.");

        // Members only — "all" is everyone but the sender.
        var members = (await spaces.ListMembersAsync(space.Id, ct)).Select(m => m.UserId).ToHashSet(StringComparer.Ordinal);
        List<string> to;
        if (body.To.ValueKind == JsonValueKind.String && body.To.GetString() == "all")
            to = members.Where(id => id != me).ToList();
        else if (body.To.ValueKind == JsonValueKind.Array && body.To.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String))
        {
            to = body.To.EnumerateArray().Select(e => e.GetString()!).Distinct(StringComparer.Ordinal).ToList();
            var strangers = to.Where(id => !members.Contains(id)).ToList();
            if (strangers.Count > 0)
                return ApiErrors.BadRequest("not_members", "An app can only message members of its own space.", new { ids = strangers });
        }
        else
            return ApiErrors.BadRequest("invalid_app_message", "`to` is a list of member ids or \"all\".");

        if (!Sent.TryTake($"{space.Id}/{folder}"))
            return ApiErrors.Json(429, "rate_limited", $"This app has sent {PerHour} messages in the last hour — try again later.");

        // Who muted the space or this app hears nothing.
        var recipients = new List<string>();
        foreach (var id in to)
        {
            var muted = await MutedAsync(system, id, ct);
            if (!muted.Contains(space.Id) && !muted.Contains($"{space.Id}/{folder}")) recipients.Add(id);
        }
        if (recipients.Count > 0)
        {
            var data = JsonSerializer.Serialize(new { app = folder, appName = app.Manifest.Name, text, from = me });
            await messages.CreateAsync(recipients, MessageKinds.AppMessage, "space", space.Id, data, ct);
        }
        return Results.Ok(new { sent = recipients.Count });
    }
}

// A budget per key over the last hour, in memory — a restart forgets it,
// which is fine for a brake: app messages per app, error reports per person.
internal sealed class HourlyLimit
{
    private readonly int _perHour;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _taken = new(StringComparer.Ordinal);

    public HourlyLimit(int perHour) => _perHour = perHour;

    public bool TryTake(string key)
    {
        var now = DateTime.UtcNow;
        var q = _taken.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && q.Peek() < now.AddHours(-1)) q.Dequeue();
            if (q.Count >= _perHour) return false;
            q.Enqueue(now);
            return true;
        }
    }
}
