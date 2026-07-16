using System.Security.Claims;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Fishbowl.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

public static class SpacesApi
{
    public record CreateSpaceRequest(string Name);

    public static RouteGroupBuilder MapSpacesApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/spaces");

        // ────────── Space CRUD ──────────

        group.MapGet("/", async (ClaimsPrincipal user, ISpaceRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var memberships = await repo.ListByMemberAsync(userId, ct);
            return Results.Ok(memberships.Select(m => new
            {
                id = m.Space.Id,
                slug = m.Space.Slug,
                name = m.Space.Name,
                role = m.Role.ToDbValue(),
                createdAt = m.Space.CreatedAt,
            }));
        })
        .WithName("ListSpaces")
        .WithSummary("Lists spaces the authenticated user belongs to.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/", async (
            CreateSpaceRequest body, ClaimsPrincipal user, ISpaceRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            try
            {
                var space = await repo.CreateAsync(userId, body.Name, ct);
                return Results.Created($"/api/v1/spaces/{space.Slug}", space);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CreateSpace")
        .WithSummary("Creates a space owned by the authenticated user.")
        .Produces<Space>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/{slug}", async (string slug, ClaimsPrincipal user, ISpaceRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var space = await repo.GetBySlugAsync(slug, ct);
            if (space is null) return Results.NotFound();

            var role = await repo.GetMembershipAsync(space.Id, userId, ct);
            if (role is null) return Results.Forbid();

            return Results.Ok(new
            {
                id = space.Id,
                slug = space.Slug,
                name = space.Name,
                role = role.Value.ToDbValue(),
                createdAt = space.CreatedAt,
            });
        })
        .WithName("GetSpace")
        .WithSummary("Gets a single space by slug. Requires membership.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{slug}", async (string slug, ClaimsPrincipal user, ISpaceRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var space = await repo.GetBySlugAsync(slug, ct);
            if (space is null) return Results.NotFound();

            var ok = await repo.DeleteAsync(space.Id, userId, ct);
            return ok ? Results.NoContent() : Results.Forbid();
        })
        .WithName("DeleteSpace")
        .WithSummary("Deletes a space. Owner only. Leaves the .db file in place.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        // ────────── Nested: space notes ──────────
        // Routes to ContextRef.Space(space.Id). Membership checked per request;
        // readonly members blocked from writes.

        group.MapGet("/{slug}/notes", async (
            string slug, string[]? tag, string? match, int? limit, int? offset,
            ClaimsPrincipal user, ISpaceRepository spaces, INoteRepository notes, CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var (space, _) = (resolved.Space!, resolved.Role!.Value);

            var tags = tag is { Length: > 0 } ? tag : null;
            var matchMode = match == "all" ? "all" : "any";
            return Results.Ok(await notes.GetAllAsync(
                ContextRef.Space(space.Id), tags, matchMode, limit, offset ?? 0, ct));
        })
        .WithName("ListSpaceNotes")
        .RequireScope("read:notes");

        group.MapGet("/{slug}/notes/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ISpaceRepository spaces, INoteRepository notes, CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var space = resolved.Space!;

            var note = await notes.GetByIdAsync(ContextRef.Space(space.Id), id, ct);
            return note is not null ? Results.Ok(note) : Results.NotFound();
        })
        .WithName("GetSpaceNote")
        .RequireScope("read:notes");

        group.MapPost("/{slug}/notes", async (
            string slug, Note note,
            ClaimsPrincipal user, ISpaceRepository spaces, INoteRepository notes, CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var (space, role) = (resolved.Space!, resolved.Role!.Value);
            if (!role.CanWrite()) return Results.Forbid();

            var userId = user.FindFirst("fishbowl_user_id")!.Value;
            try
            {
                var created = await notes.CreateAsync(ContextRef.Space(space.Id), userId, note, ct);
                return Results.Created($"/api/v1/spaces/{slug}/notes/{created}", note);
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.PayloadTooLarge(ex);
            }
        })
        .WithName("CreateSpaceNote")
        .RequireScope("write:notes");

        group.MapPut("/{slug}/notes/{id}", async (
            string slug, string id, Note note,
            ClaimsPrincipal user, ISpaceRepository spaces, INoteRepository notes, CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var (space, role) = (resolved.Space!, resolved.Role!.Value);
            if (!role.CanWrite()) return Results.Forbid();

            note.Id = id;
            try
            {
                var updated = await notes.UpdateAsync(ContextRef.Space(space.Id), note, ct);
                return updated ? Results.NoContent() : Results.NotFound();
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.PayloadTooLarge(ex);
            }
        })
        .WithName("UpdateSpaceNote")
        .RequireScope("write:notes");

        group.MapDelete("/{slug}/notes/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ISpaceRepository spaces, INoteRepository notes, CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var (space, role) = (resolved.Space!, resolved.Role!.Value);
            if (!role.CanWrite()) return Results.Forbid();

            var ok = await notes.DeleteAsync(ContextRef.Space(space.Id), id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteSpaceNote")
        .RequireScope("write:notes");

        // ────────── Nested: space tags ──────────

        group.MapGet("/{slug}/tags", async (
            string slug, ClaimsPrincipal user, ISpaceRepository spaces, ITagRepository tags, CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            return Results.Ok(await tags.GetAllAsync(ContextRef.Space(resolved.Space!.Id), ct));
        })
        .WithName("ListSpaceTags")
        .RequireScope("read:tags");

        group.MapPut("/{slug}/tags/{name}", async (
            string slug, string name, TagsApi.UpsertColorRequest body,
            ClaimsPrincipal user, ISpaceRepository spaces, ITagRepository tags, CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            try
            {
                var tag = await tags.UpsertColorAsync(
                    ContextRef.Space(resolved.Space!.Id), name, body.Color, ct);
                return Results.Ok(tag);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UpsertSpaceTagColor")
        .RequireScope("write:tags");

        group.MapPost("/{slug}/tags/{name}/rename", async (
            string slug, string name, TagsApi.RenameRequest body,
            ClaimsPrincipal user, ISpaceRepository spaces, ITagRepository tags, CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            try
            {
                var renamed = await tags.RenameAsync(
                    ContextRef.Space(resolved.Space!.Id), name, body.NewName, ct);
                return renamed ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("RenameSpaceTag")
        .RequireScope("write:tags");

        group.MapDelete("/{slug}/tags/{name}", async (
            string slug, string name,
            ClaimsPrincipal user, ISpaceRepository spaces, ITagRepository tags, CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            try
            {
                var deleted = await tags.DeleteAsync(
                    ContextRef.Space(resolved.Space!.Id), name, ct);
                return deleted ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("DeleteSpaceTag")
        .RequireScope("write:tags");

        // ────────── Nested: space todos ──────────

        group.MapGet("/{slug}/todos", async (
            string slug,
            ClaimsPrincipal user, ISpaceRepository spaces, ITodoRepository todos,
            bool includeCompleted = false, CancellationToken ct = default) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            return Results.Ok(await todos.GetAllAsync(
                ContextRef.Space(resolved.Space!.Id), includeCompleted, ct));
        })
        .WithName("ListSpaceTodos")
        .RequireScope("read:tasks");

        group.MapGet("/{slug}/todos/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ISpaceRepository spaces, ITodoRepository todos, CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var item = await todos.GetByIdAsync(ContextRef.Space(resolved.Space!.Id), id, ct);
            return item is not null ? Results.Ok(item) : Results.NotFound();
        })
        .WithName("GetSpaceTodo")
        .RequireScope("read:tasks");

        group.MapPost("/{slug}/todos", async (
            string slug, TodoItem item,
            ClaimsPrincipal user, ISpaceRepository spaces, ITodoRepository todos, CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            var actorUserId = user.FindFirst(McpContextClaims.UserId)!.Value;
            try
            {
                var id = await todos.CreateAsync(
                    ContextRef.Space(resolved.Space!.Id), actorUserId, item, ct);
                return Results.Created($"/api/v1/spaces/{slug}/todos/{id}", item);
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.PayloadTooLarge(ex);
            }
        })
        .WithName("CreateSpaceTodo")
        .RequireScope("write:tasks");

        group.MapPut("/{slug}/todos/{id}", async (
            string slug, string id, TodoItem item,
            ClaimsPrincipal user, ISpaceRepository spaces, ITodoRepository todos, CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            item.Id = id;
            try
            {
                var ok = await todos.UpdateAsync(ContextRef.Space(resolved.Space!.Id), item, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.PayloadTooLarge(ex);
            }
        })
        .WithName("UpdateSpaceTodo")
        .RequireScope("write:tasks");

        group.MapDelete("/{slug}/todos/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ISpaceRepository spaces, ITodoRepository todos, CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            var ok = await todos.DeleteAsync(ContextRef.Space(resolved.Space!.Id), id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteSpaceTodo")
        .RequireScope("write:tasks");

        // ────────── Nested: space contacts ──────────

        group.MapGet("/{slug}/contacts", async (
            string slug,
            ClaimsPrincipal user, ISpaceRepository spaces, IContactRepository contacts,
            bool includeArchived = false, CancellationToken ct = default) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            return Results.Ok(await contacts.GetAllAsync(
                ContextRef.Space(resolved.Space!.Id), includeArchived, ct));
        })
        .WithName("ListSpaceContacts")
        .RequireScope("read:contacts");

        group.MapGet("/{slug}/contacts/search", async (
            string slug, string q,
            ClaimsPrincipal user, ISpaceRepository spaces, IContactRepository contacts,
            int limit = 50, CancellationToken ct = default) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var hits = await contacts.SearchAsync(
                ContextRef.Space(resolved.Space!.Id), q ?? "", limit, ct);
            return Results.Ok(hits);
        })
        .WithName("SearchSpaceContacts")
        .RequireScope("read:contacts");

        group.MapGet("/{slug}/contacts/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ISpaceRepository spaces, IContactRepository contacts,
            CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var item = await contacts.GetByIdAsync(ContextRef.Space(resolved.Space!.Id), id, ct);
            return item is not null ? Results.Ok(item) : Results.NotFound();
        })
        .WithName("GetSpaceContact")
        .RequireScope("read:contacts");

        group.MapPost("/{slug}/contacts", async (
            string slug, Contact contact,
            ClaimsPrincipal user, ISpaceRepository spaces, IContactRepository contacts,
            CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(contact.Name))
                return Results.BadRequest(new { error = "name is required" });

            var actorId = user.FindFirst(McpContextClaims.UserId)!.Value;
            try
            {
                var id = await contacts.CreateAsync(
                    ContextRef.Space(resolved.Space!.Id), actorId, contact, ct);
                return Results.Created($"/api/v1/spaces/{slug}/contacts/{id}", contact);
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.PayloadTooLarge(ex);
            }
        })
        .WithName("CreateSpaceContact")
        .RequireScope("write:contacts");

        group.MapPut("/{slug}/contacts/{id}", async (
            string slug, string id, Contact contact,
            ClaimsPrincipal user, ISpaceRepository spaces, IContactRepository contacts,
            CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(contact.Name))
                return Results.BadRequest(new { error = "name is required" });

            contact.Id = id;
            try
            {
                var ok = await contacts.UpdateAsync(ContextRef.Space(resolved.Space!.Id), contact, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.PayloadTooLarge(ex);
            }
        })
        .WithName("UpdateSpaceContact")
        .RequireScope("write:contacts");

        group.MapDelete("/{slug}/contacts/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ISpaceRepository spaces, IContactRepository contacts,
            CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            var ok = await contacts.DeleteAsync(ContextRef.Space(resolved.Space!.Id), id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteSpaceContact")
        .RequireScope("write:contacts");

        // ────────── Nested: space re-index (cookie-only, like /api/v1/search/reindex) ──────────

        // Hybrid notes search, space-scoped. Mirrors /api/v1/search with the
        // resolved space context — same response shape, same scope.
        group.MapGet("/{slug}/search", async (
            string slug, string q,
            ClaimsPrincipal user, ISpaceRepository spaces,
            Fishbowl.Core.Search.ISearchService search,
            string[]? tag = null, string? match = null,
            int limit = 20, bool includePending = true, CancellationToken ct = default) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;

            limit = Math.Clamp(limit, 1, 100);
            var tags = tag is { Length: > 0 } ? tag : null;
            var matchMode = match == "all" ? "all" : "any";
            var result = await search.HybridSearchAsync(
                ContextRef.Space(resolved.Space!.Id), q ?? "", limit, includePending,
                tags, matchMode, ct);
            return Results.Ok(new
            {
                notes = result.Hits.Select(h => new
                {
                    id = h.Note.Id,
                    title = h.Note.Title,
                    content = h.Note.Content,
                    tags = h.Note.Tags,
                    createdAt = h.Note.CreatedAt,
                    updatedAt = h.Note.UpdatedAt,
                    pinned = h.Note.Pinned,
                    archived = h.Note.Archived,
                    score = h.Score,
                }).ToList(),
                degraded = result.Degraded,
            });
        })
        .WithName("SearchSpaceNotes")
        .RequireScope("read:notes");

        group.MapPost("/{slug}/search/reindex", async (
            string slug,
            ClaimsPrincipal user, ISpaceRepository spaces, INoteRepository notes, CancellationToken ct) =>
        {
            // Mirror of SearchApi.cs: maintenance endpoint is cookie-only.
            if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme)
                return Results.Forbid();

            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            var result = await notes.ReEmbedAllAsync(ContextRef.Space(resolved.Space!.Id), ct);
            return Results.Ok(new { processed = result.Processed, failed = result.Failed });
        })
        .WithName("ReindexSpaceSearch");

        // ────────── Nested: space events ──────────

        group.MapGet("/{slug}/events", async (
            string slug,
            ClaimsPrincipal user, ISpaceRepository spaces, IEventRepository events,
            DateTime? from = null, DateTime? to = null, CancellationToken ct = default) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;

            if ((from is null) != (to is null))
                return Results.BadRequest(new { error = "from and to must both be provided or both omitted" });

            var spaceCtx = ContextRef.Space(resolved.Space!.Id);
            if (from is not null)
                return Results.Ok(await events.GetRangeAsync(spaceCtx, from.Value, to!.Value, ct));
            return Results.Ok(await events.GetAllAsync(spaceCtx, ct));
        })
        .WithName("ListSpaceEvents")
        .RequireScope("read:events");

        group.MapGet("/{slug}/events/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ISpaceRepository spaces, IEventRepository events,
            CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var item = await events.GetByIdAsync(ContextRef.Space(resolved.Space!.Id), id, ct);
            return item is not null ? Results.Ok(item) : Results.NotFound();
        })
        .WithName("GetSpaceEvent")
        .RequireScope("read:events");

        group.MapPost("/{slug}/events", async (
            string slug, Event evt,
            ClaimsPrincipal user, ISpaceRepository spaces, IEventRepository events,
            CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            try
            {
                var actorId = user.FindFirst(McpContextClaims.UserId)!.Value;
                var id = await events.CreateAsync(
                    ContextRef.Space(resolved.Space!.Id), actorId, evt, ct);
                return Results.Created($"/api/v1/spaces/{slug}/events/{id}", evt);
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.PayloadTooLarge(ex);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CreateSpaceEvent")
        .RequireScope("write:events");

        group.MapPut("/{slug}/events/{id}", async (
            string slug, string id, Event evt,
            ClaimsPrincipal user, ISpaceRepository spaces, IEventRepository events,
            CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            evt.Id = id;
            try
            {
                var ok = await events.UpdateAsync(ContextRef.Space(resolved.Space!.Id), evt, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.PayloadTooLarge(ex);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UpdateSpaceEvent")
        .RequireScope("write:events");

        group.MapDelete("/{slug}/events/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ISpaceRepository spaces, IEventRepository events,
            CancellationToken ct) =>
        {
            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            var ok = await events.DeleteAsync(ContextRef.Space(resolved.Space!.Id), id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteSpaceEvent")
        .RequireScope("write:events");

        // ────────── Nested: space DB export (cookie-only, owner-only) ──────────
        // A space DB copy is a terminal, migrate-your-data-out action — owner
        // only, same rule as space deletion. Members already have read access
        // through the API; this endpoint is about walking off with the whole
        // database file.
        group.MapGet("/{slug}/export/db", async (
            string slug,
            ClaimsPrincipal user, ISpaceRepository spaces, DatabaseFactory dbFactory, CancellationToken ct) =>
        {
            if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme)
                return Results.Forbid();

            var resolved = await ResolveSpaceAsync(slug, user, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanDeleteSpace()) return Results.Forbid();

            var spaceCtx = ContextRef.Space(resolved.Space!.Id);
            var bytes = await ExportApi.BackupContextAsync(dbFactory, spaceCtx, ct);
            var filename = $"fishbowl-space-{resolved.Space.Slug}-{DateTime.UtcNow:yyyyMMdd}.db";
            return Results.File(bytes, "application/vnd.sqlite3", filename);
        })
        .WithName("ExportSpaceDatabase");

        return group.RequireAuthorization();
    }

    private record SpaceResolution(Space? Space, SpaceRole? Role, IResult? Error);

    // Resolves {slug} → Space + the caller's SpaceRole. Returns an Error result
    // (401/404/403) when the caller isn't authenticated, the space doesn't
    // exist, or the caller isn't a member. Keeps the endpoint handlers above
    // declarative.
    private static async Task<SpaceResolution> ResolveSpaceAsync(
        string slug, ClaimsPrincipal user, ISpaceRepository spaces, CancellationToken ct)
    {
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (string.IsNullOrEmpty(userId))
            return new SpaceResolution(null, null, Results.Unauthorized());

        var space = await spaces.GetBySlugAsync(slug, ct);
        if (space is null)
            return new SpaceResolution(null, null, Results.NotFound());

        // Bearer-context match: if the principal is a token, it must be bound
        // to THIS space. A personal token on a space URL is rejected even when
        // the underlying user is a space member — the token's own context is
        // the authoritative scope, not the human behind it.
        if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme)
        {
            var ctxType = user.FindFirst(McpContextClaims.ContextType)?.Value;
            var ctxId = user.FindFirst(McpContextClaims.ContextId)?.Value;
            if (ctxType != "space" || ctxId != space.Slug)
                return new SpaceResolution(space, null, Results.Forbid());
        }

        var role = await spaces.GetMembershipAsync(space.Id, userId, ct);
        if (role is null)
            return new SpaceResolution(space, null, Results.Forbid());

        return new SpaceResolution(space, role, null);
    }
}
