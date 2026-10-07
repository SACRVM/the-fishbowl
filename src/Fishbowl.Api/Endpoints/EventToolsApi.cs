using System.Text;
using Fishbowl.Core;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// The calendar's tools beside its CRUD (sync spec, phase 1), both workspaces
// (/api/v1/events/… and /api/v1/spaces/<slug>/events/…):
//   GET    …/export                      every event as one .ics file (a series once, with its rule)
//   POST   …/import?tz=<IANA>            an .ics file as the body → { created, skipped, failed };
//                                        an event whose UID is already here is skipped, so a file
//                                        imports once; tz is the zone for floating times
//   GET    …/birthdays?from=&to=         the contacts' birthdays in a date range (never stored events)
//   GET    …/feeds, POST …/feeds, DELETE …/feeds/{id}
//                                        the caller's subscription links (cookie only)
// and the links themselves: GET /feeds/<token>.ics — no sign-in, the token is
// the key; it works while its maker is active and, in a space, a member.
public static class EventToolsApi
{
    public const int MaxImportBytes = 10 * 1024 * 1024;

    public static IEndpointRouteBuilder MapEventToolsApi(this IEndpointRouteBuilder routes)
    {
        Map(routes.MapGroup("/api/v1/events").RequireAuthorization(), space: false);
        Map(routes.MapGroup("/api/v1/spaces/{slug}/events").RequireAuthorization(), space: true);

        routes.MapGet("/feeds/{file}", FeedAsync).AllowAnonymous().ExcludeFromDescription();
        return routes;
    }

    private sealed record Target(ContextRef Ctx, string UserId, string Name);

    private static async Task<(Target? T, IResult? Error)> ResolveAsync(HttpContext http, bool space, string scope, CancellationToken ct, bool write = false)
    {
        var user = http.User;
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (string.IsNullOrEmpty(userId)) return (null, Results.Unauthorized());
        var bearer = user.Identity?.AuthenticationType == McpContextClaims.BearerScheme;
        if (bearer && !user.HasClaim(McpContextClaims.Scope, scope)) return (null, Results.Forbid());
        if (!space)
        {
            if (bearer && McpContextClaims.Resolve(user).Type != ContextType.User) return (null, Results.Forbid());
            return (new Target(ContextRef.User(userId), userId, "Personal"), null);
        }
        var spaces = http.RequestServices.GetService(typeof(ISpaceRepository)) as ISpaceRepository;
        var resolved = await SpacesApi.ResolveSpaceAsync((string)http.Request.RouteValues["slug"]!, user, spaces!, ct);
        if (resolved.Error is not null) return (null, resolved.Error);
        if (write && !resolved.Role!.Value.CanWrite()) return (null, Results.Forbid());
        return (new Target(ContextRef.Space(resolved.Space!.Id), userId, resolved.Space.Name), null);
    }

    private static void Map(RouteGroupBuilder g, bool space)
    {
        var tag = space ? "Space" : "";

        g.MapGet("/export", async (HttpContext http, IEventRepository events, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, ScopeCatalog.ReadEvents, ct);
            if (err is not null) return err;
            var all = await events.GetAllAsync(t!.Ctx, ct);
            return Results.File(Encoding.UTF8.GetBytes(EventFormats.ToICalendar(all, t.Name)), "text/calendar; charset=utf-8", "calendar.ics");
        }).WithName($"Export{tag}Events").WithSummary("Every event as one iCalendar (.ics) file — a series once, with its rule.");

        g.MapPost("/import", async (string? tz, HttpContext http, IEventRepository events, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, ScopeCatalog.WriteEvents, ct, write: true);
            if (err is not null) return err;
            if (http.Request.ContentLength is > MaxImportBytes) return TooLarge();
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(ct);
            if (text.Length > MaxImportBytes) return TooLarge();

            EventFormats.Parsed parsed;
            try { parsed = EventFormats.ParseICalendar(text, tz); }
            catch (FormatException) { return ApiErrors.BadRequest("invalid_file", "The file isn't iCalendar (.ics)."); }

            // An event already here (the same UID) is skipped, so a file — or
            // the app's own export — imports once.
            var uids = new HashSet<string>((await events.GetAllAsync(t!.Ctx, ct)).Select(e => e.Uid ?? ""), StringComparer.Ordinal);
            int created = 0, skipped = parsed.Skipped, failed = 0;
            foreach (var e in parsed.Events)
            {
                if (e.Uid is { } uid && !uids.Add(uid)) { skipped++; continue; }
                try { await events.CreateAsync(t.Ctx, t.UserId, e, ct); created++; }
                catch (Exception ex) when (ex is ResourceValidationException or ArgumentException or InvalidOperationException) { failed++; }
            }
            return Results.Ok(new { created, skipped, failed });
        }).WithName($"Import{tag}Events").WithSummary("Imports an iCalendar (.ics) file sent as the body; events already here (same UID) are skipped. tz = the zone for floating times.");

        g.MapGet("/birthdays", async (string? from, string? to, HttpContext http, IContactRepository contacts, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, ScopeCatalog.ReadContacts, ct);
            if (err is not null) return err;
            if (!AllDayDates.TryParse(from, out var f) || !AllDayDates.TryParse(to, out var u) || u <= f)
                return ApiErrors.BadRequest("range_invalid", "from and to are dates (YYYY-MM-DD), from before to.");
            if (u.DayNumber - f.DayNumber > EventLimits.MaxRangeDays)
                return ApiErrors.BadRequest("range_too_long", $"A range is at most {EventLimits.MaxRangeDays} days.", new { maxDays = EventLimits.MaxRangeDays });
            var all = await contacts.GetAllAsync(t!.Ctx, includeArchived: false, ct);
            return Results.Ok(new { birthdays = Birthdays.Between(all, f, u) });
        }).WithName($"List{tag}Birthdays").WithSummary("The contacts' birthdays between two dates (to exclusive), from the contacts — never stored events.");

        // Subscription links: making a secret URL is a person's act — cookie only.
        g.MapGet("/feeds", async (HttpContext http, ICalendarFeedRepository feeds, CancellationToken ct) =>
        {
            if (http.User.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return Results.Forbid();
            var (t, err) = await ResolveAsync(http, space, ScopeCatalog.ReadEvents, ct);
            if (err is not null) return err;
            var list = await feeds.ListAsync(t!.Ctx, t.UserId, ct);
            return Results.Ok(new { feeds = list.Select(x => new { x.Id, x.CreatedAt }) });
        }).WithName($"List{tag}CalendarFeeds").WithSummary("The caller's subscription links of this workspace. Cookie only.");

        g.MapPost("/feeds", async (HttpContext http, ICalendarFeedRepository feeds, CancellationToken ct) =>
        {
            if (http.User.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return Results.Forbid();
            var (t, err) = await ResolveAsync(http, space, ScopeCatalog.ReadEvents, ct);
            if (err is not null) return err;
            var (feed, token) = await feeds.CreateAsync(t!.Ctx, t.UserId, ct);
            var url = $"{http.Request.Scheme}://{http.Request.Host}/feeds/{token}.ics";
            return Results.Ok(new { feed.Id, feed.CreatedAt, url });
        }).WithName($"Create{tag}CalendarFeed").WithSummary("A new subscription link — its URL is shown once. Cookie only.");

        g.MapDelete("/feeds/{id}", async (string id, HttpContext http, ICalendarFeedRepository feeds, CancellationToken ct) =>
        {
            if (http.User.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return Results.Forbid();
            var (t, err) = await ResolveAsync(http, space, ScopeCatalog.ReadEvents, ct);
            if (err is not null) return err;
            return await feeds.DeleteAsync(t!.Ctx, t.UserId, id, ct)
                ? Results.NoContent()
                : ApiErrors.NotFound("feed_missing", "There is no such link.");
        }).WithName($"Delete{tag}CalendarFeed").WithSummary("Revokes one of the caller's subscription links. Cookie only.");
    }

    private static IResult TooLarge() =>
        ApiErrors.Json(413, "too_large", $"An import is at most {MaxImportBytes / (1024 * 1024)} MB.");

    // GET /feeds/<token>.ics: the workspace's events while the link's maker is
    // active (and still a member of the space); anything else is a 404, so a
    // revoked or stale link says nothing about what was there.
    private static async Task<IResult> FeedAsync(string file, HttpContext http, ICalendarFeedRepository feeds,
        ISystemRepository system, ISpaceRepository spaces, IEventRepository events)
    {
        var ct = http.RequestAborted;
        if (!file.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
        var feed = await feeds.FindByTokenAsync(file[..^".ics".Length], ct);
        if (feed is null) return Results.NotFound();
        var maker = await system.GetUserAsync(feed.CreatedBy, ct);
        if (maker is null || maker.State != UserStates.Active) return Results.NotFound();

        ContextRef ctx;
        string name;
        if (feed.ContextType == "space")
        {
            var sp = await spaces.GetByIdAsync(feed.ContextId, ct);
            if (sp is null || await spaces.GetMembershipAsync(sp.Id, feed.CreatedBy, ct) is null) return Results.NotFound();
            ctx = ContextRef.Space(sp.Id);
            name = sp.Name;
        }
        else
        {
            if (feed.ContextId != feed.CreatedBy) return Results.NotFound();
            ctx = ContextRef.User(feed.ContextId);
            name = "Fishbowl";
        }
        var all = await events.GetAllAsync(ctx, ct);
        http.Response.Headers.CacheControl = "private, max-age=300";
        return Results.Text(EventFormats.ToICalendar(all, name), "text/calendar; charset=utf-8");
    }
}
