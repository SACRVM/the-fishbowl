using System.Security.Claims;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Tables;
using Fishbowl.Data.Tables;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// The workspace trash for records — notes, todos, events, contacts (space-apps
// spec, decision 13; files keep their own /files/trash, the Trash app shows
// both). Every member sees it; restoring and deleting for good need write
// access — and a space's table items the tables' rules: a dropped table
// the Designer role, another member's row of an own-rows table too.
// Cookie-only for now: it is the Trash app's surface.
//
//   GET    /api/v1/trash                       (+ /api/v1/spaces/<slug>/trash)
//   POST   /api/v1/trash/{id}/restore          → 200 { kind, itemId } | 409 restore_conflict
//   DELETE /api/v1/trash/{id}                  for good
//   DELETE /api/v1/trash                       empty it
public static class TrashApi
{
    public static IEndpointRouteBuilder MapTrashApi(this IEndpointRouteBuilder routes)
    {
        Map(routes.MapGroup("/api/v1/trash"), space: false);
        Map(routes.MapGroup("/api/v1/spaces/{slug}/trash"), space: true);
        return routes;
    }

    private static void Map(RouteGroupBuilder group, bool space)
    {
        group.MapGet("/", async (HttpContext http, ClaimsPrincipal user, ISpaceRepository spaces, ITrashRepository trash,
            ISystemRepository system, CancellationToken ct) =>
        {
            var (ctx, _, error) = await ResolveAsync(http, user, spaces, space, write: false, ct);
            if (error is not null) return error;
            var names = new Dictionary<string, string?>();
            var items = new List<object>();
            foreach (var t in await trash.ListAsync(ctx!.Value, ct))
            {
                string? by = null;
                if (t.DeletedBy is { } id)
                {
                    if (!names.TryGetValue(id, out by)) names[id] = by = (await system.GetUserAsync(id, ct))?.Name;
                }
                items.Add(new { id = t.Id, kind = t.Kind, itemId = t.ItemId, title = t.Title, deletedAt = t.DeletedAt, deletedBy = by });
            }
            return Results.Ok(items);
        })
        .WithSummary("The workspace's deleted notes, todos, events and contacts, newest first.")
        .RequireAuthorization();

        group.MapPost("/{id}/restore", async (string id, HttpContext http, ClaimsPrincipal user, ISpaceRepository spaces,
            ITrashRepository trash, TableRepository tables, CancellationToken ct) =>
        {
            var (ctx, actor, error) = await ResolveAsync(http, user, spaces, space, write: true, ct);
            if (error is not null) return error;
            if (await RefusedAsync(tables, ctx!.Value, actor, id, ct) is { } refused) return refused;
            var (result, item) = await trash.RestoreAsync(ctx!.Value, id, ct);
            return result switch
            {
                TrashRestore.Restored => Results.Ok(new { kind = item!.Kind, itemId = item.ItemId }),
                TrashRestore.Conflict => ApiErrors.Conflict("restore_conflict",
                    "It can't come back: its place is taken, or something it needs is gone."),
                _ => Results.NotFound(),
            };
        })
        .WithSummary("Put a deleted item back where it was — or refuse when that no longer works.")
        .RequireAuthorization();

        group.MapDelete("/{id}", async (string id, HttpContext http, ClaimsPrincipal user, ISpaceRepository spaces,
            ITrashRepository trash, TableRepository tables, CancellationToken ct) =>
        {
            var (ctx, actor, error) = await ResolveAsync(http, user, spaces, space, write: true, ct);
            if (error is not null) return error;
            if (await RefusedAsync(tables, ctx!.Value, actor, id, ct) is { } refused) return refused;
            return await trash.DeleteAsync(ctx!.Value, id, ct) ? Results.NoContent() : Results.NotFound();
        })
        .WithSummary("Delete one item from the trash for good.")
        .RequireAuthorization();

        group.MapDelete("/", async (HttpContext http, ClaimsPrincipal user, ISpaceRepository spaces,
            ITrashRepository trash, TableRepository tables, CancellationToken ct) =>
        {
            var (ctx, actor, error) = await ResolveAsync(http, user, spaces, space, write: true, ct);
            if (error is not null) return error;
            if (actor is null || actor.CanDesign) return Results.Ok(new { deleted = await trash.EmptyAsync(ctx!.Value, ct) });
            // Below Designer: what the caller may not delete for good stays
            // (dropped tables, others' rows of own-rows tables). Only what was
            // listed before the check goes.
            var items = await trash.ListAsync(ctx!.Value, ct);
            var refusals = await tables.TrashRefusalsAsync(ctx.Value, actor, null, ct);
            var deleted = 0;
            foreach (var item in items)
                if (!refusals.ContainsKey(item.Id) && await trash.DeleteAsync(ctx.Value, item.Id, ct)) deleted++;
            return Results.Ok(new { deleted });
        })
        .WithSummary("Empty the workspace's record trash.")
        .RequireAuthorization();
    }

    // A space's table items follow the tables' rules (TableRepository.
    // TrashRefusalsAsync); null = go ahead.
    private static async Task<IResult?> RefusedAsync(TableRepository tables, ContextRef ctx, TableActor? actor, string id, CancellationToken ct)
    {
        if (actor is null) return null;
        var refusals = await tables.TrashRefusalsAsync(ctx, actor, id, ct);
        return refusals.TryGetValue(id, out var ex) ? ApiErrors.Json(ex.Status, ex.Code, ex.Message, ex.Args) : null;
    }

    // The context, and in a space the caller as a TableActor (null in the
    // personal workspace, which has no tables).
    private static async Task<(ContextRef? Ctx, TableActor? Actor, IResult? Error)> ResolveAsync(
        HttpContext http, ClaimsPrincipal user, ISpaceRepository spaces, bool space, bool write, CancellationToken ct)
    {
        if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return (null, null, Results.Forbid());
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (!space)
        {
            return string.IsNullOrEmpty(userId)
                ? (null, null, Results.Unauthorized())
                : (ContextRef.User(userId), null, null);
        }
        var slug = http.Request.RouteValues["slug"] as string ?? "";
        var r = await SpacesApi.ResolveSpaceAsync(slug, user, spaces, ct);
        if (r.Error is not null) return (null, null, r.Error);
        if (write && !r.Role!.Value.CanWrite()) return (null, null, Results.Forbid());
        return (ContextRef.Space(r.Space!.Id), TablesApi.ActorFor(userId ?? "", r.Role!.Value), null);
    }
}
