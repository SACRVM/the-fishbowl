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

public static class TeamsApi
{
    public record CreateTeamRequest(string Name);

    public static RouteGroupBuilder MapTeamsApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/teams");

        // ────────── Team CRUD ──────────

        group.MapGet("/", async (ClaimsPrincipal user, ITeamRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var memberships = await repo.ListByMemberAsync(userId, ct);
            return Results.Ok(memberships.Select(m => new
            {
                id = m.Team.Id,
                slug = m.Team.Slug,
                name = m.Team.Name,
                role = m.Role.ToDbValue(),
                createdAt = m.Team.CreatedAt,
            }));
        })
        .WithName("ListTeams")
        .WithSummary("Lists teams the authenticated user belongs to.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/", async (
            CreateTeamRequest body, ClaimsPrincipal user, ITeamRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            try
            {
                var team = await repo.CreateAsync(userId, body.Name, ct);
                return Results.Created($"/api/v1/teams/{team.Slug}", team);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CreateTeam")
        .WithSummary("Creates a team owned by the authenticated user.")
        .Produces<Team>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/{slug}", async (string slug, ClaimsPrincipal user, ITeamRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var team = await repo.GetBySlugAsync(slug, ct);
            if (team is null) return Results.NotFound();

            var role = await repo.GetMembershipAsync(team.Id, userId, ct);
            if (role is null) return Results.Forbid();

            return Results.Ok(new
            {
                id = team.Id,
                slug = team.Slug,
                name = team.Name,
                role = role.Value.ToDbValue(),
                createdAt = team.CreatedAt,
            });
        })
        .WithName("GetTeam")
        .WithSummary("Gets a single team by slug. Requires membership.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{slug}", async (string slug, ClaimsPrincipal user, ITeamRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var team = await repo.GetBySlugAsync(slug, ct);
            if (team is null) return Results.NotFound();

            var ok = await repo.DeleteAsync(team.Id, userId, ct);
            return ok ? Results.NoContent() : Results.Forbid();
        })
        .WithName("DeleteTeam")
        .WithSummary("Deletes a team. Owner only. Leaves the .db file in place.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        // ────────── Nested: team notes ──────────
        // Routes to ContextRef.Team(team.Id). Membership checked per request;
        // readonly members blocked from writes.

        group.MapGet("/{slug}/notes", async (
            string slug, string[]? tag, string? match,
            ClaimsPrincipal user, ITeamRepository teams, INoteRepository notes, CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            var (team, _) = (resolved.Team!, resolved.Role!.Value);

            var tags = tag is { Length: > 0 } ? tag : null;
            var matchMode = match == "all" ? "all" : "any";
            return Results.Ok(await notes.GetAllAsync(ContextRef.Team(team.Id), tags, matchMode, ct));
        })
        .WithName("ListTeamNotes")
        .RequireScope("read:notes");

        group.MapGet("/{slug}/notes/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ITeamRepository teams, INoteRepository notes, CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            var team = resolved.Team!;

            var note = await notes.GetByIdAsync(ContextRef.Team(team.Id), id, ct);
            return note is not null ? Results.Ok(note) : Results.NotFound();
        })
        .WithName("GetTeamNote")
        .RequireScope("read:notes");

        group.MapPost("/{slug}/notes", async (
            string slug, Note note,
            ClaimsPrincipal user, ITeamRepository teams, INoteRepository notes, CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            var (team, role) = (resolved.Team!, resolved.Role!.Value);
            if (!role.CanWrite()) return Results.Forbid();

            var userId = user.FindFirst("fishbowl_user_id")!.Value;
            try
            {
                var created = await notes.CreateAsync(ContextRef.Team(team.Id), userId, note, ct);
                return Results.Created($"/api/v1/teams/{slug}/notes/{created}", note);
            }
            catch (NoteValidationException ex)
            {
                return Results.Json(new
                {
                    error = "note exceeds size limits",
                    field = ex.Error.Field,
                    reason = ex.Error.Reason,
                }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
        })
        .WithName("CreateTeamNote")
        .RequireScope("write:notes");

        group.MapPut("/{slug}/notes/{id}", async (
            string slug, string id, Note note,
            ClaimsPrincipal user, ITeamRepository teams, INoteRepository notes, CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            var (team, role) = (resolved.Team!, resolved.Role!.Value);
            if (!role.CanWrite()) return Results.Forbid();

            note.Id = id;
            try
            {
                var updated = await notes.UpdateAsync(ContextRef.Team(team.Id), note, ct);
                return updated ? Results.NoContent() : Results.NotFound();
            }
            catch (NoteValidationException ex)
            {
                return Results.Json(new
                {
                    error = "note exceeds size limits",
                    field = ex.Error.Field,
                    reason = ex.Error.Reason,
                }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
        })
        .WithName("UpdateTeamNote")
        .RequireScope("write:notes");

        group.MapDelete("/{slug}/notes/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ITeamRepository teams, INoteRepository notes, CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            var (team, role) = (resolved.Team!, resolved.Role!.Value);
            if (!role.CanWrite()) return Results.Forbid();

            var ok = await notes.DeleteAsync(ContextRef.Team(team.Id), id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteTeamNote")
        .RequireScope("write:notes");

        // ────────── Nested: team tags ──────────

        group.MapGet("/{slug}/tags", async (
            string slug, ClaimsPrincipal user, ITeamRepository teams, ITagRepository tags, CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            return Results.Ok(await tags.GetAllAsync(ContextRef.Team(resolved.Team!.Id), ct));
        })
        .WithName("ListTeamTags")
        .RequireScope("read:tags");

        group.MapPut("/{slug}/tags/{name}", async (
            string slug, string name, TagsApi.UpsertColorRequest body,
            ClaimsPrincipal user, ITeamRepository teams, ITagRepository tags, CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            try
            {
                var tag = await tags.UpsertColorAsync(
                    ContextRef.Team(resolved.Team!.Id), name, body.Color, ct);
                return Results.Ok(tag);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UpsertTeamTagColor")
        .RequireScope("write:tags");

        group.MapPost("/{slug}/tags/{name}/rename", async (
            string slug, string name, TagsApi.RenameRequest body,
            ClaimsPrincipal user, ITeamRepository teams, ITagRepository tags, CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            try
            {
                var renamed = await tags.RenameAsync(
                    ContextRef.Team(resolved.Team!.Id), name, body.NewName, ct);
                return renamed ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("RenameTeamTag")
        .RequireScope("write:tags");

        group.MapDelete("/{slug}/tags/{name}", async (
            string slug, string name,
            ClaimsPrincipal user, ITeamRepository teams, ITagRepository tags, CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            try
            {
                var deleted = await tags.DeleteAsync(
                    ContextRef.Team(resolved.Team!.Id), name, ct);
                return deleted ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("DeleteTeamTag")
        .RequireScope("write:tags");

        // ────────── Nested: team todos ──────────

        group.MapGet("/{slug}/todos", async (
            string slug,
            ClaimsPrincipal user, ITeamRepository teams, ITodoRepository todos,
            bool includeCompleted = false, CancellationToken ct = default) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            return Results.Ok(await todos.GetAllAsync(
                ContextRef.Team(resolved.Team!.Id), includeCompleted, ct));
        })
        .WithName("ListTeamTodos")
        .RequireScope("read:tasks");

        group.MapGet("/{slug}/todos/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ITeamRepository teams, ITodoRepository todos, CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            var item = await todos.GetByIdAsync(ContextRef.Team(resolved.Team!.Id), id, ct);
            return item is not null ? Results.Ok(item) : Results.NotFound();
        })
        .WithName("GetTeamTodo")
        .RequireScope("read:tasks");

        group.MapPost("/{slug}/todos", async (
            string slug, TodoItem item,
            ClaimsPrincipal user, ITeamRepository teams, ITodoRepository todos, CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            var actorUserId = user.FindFirst(McpContextClaims.UserId)!.Value;
            var id = await todos.CreateAsync(
                ContextRef.Team(resolved.Team!.Id), actorUserId, item, ct);
            return Results.Created($"/api/v1/teams/{slug}/todos/{id}", item);
        })
        .WithName("CreateTeamTodo")
        .RequireScope("write:tasks");

        group.MapPut("/{slug}/todos/{id}", async (
            string slug, string id, TodoItem item,
            ClaimsPrincipal user, ITeamRepository teams, ITodoRepository todos, CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            item.Id = id;
            var ok = await todos.UpdateAsync(ContextRef.Team(resolved.Team!.Id), item, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("UpdateTeamTodo")
        .RequireScope("write:tasks");

        group.MapDelete("/{slug}/todos/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ITeamRepository teams, ITodoRepository todos, CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            var ok = await todos.DeleteAsync(ContextRef.Team(resolved.Team!.Id), id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteTeamTodo")
        .RequireScope("write:tasks");

        // ────────── Nested: team contacts ──────────

        group.MapGet("/{slug}/contacts", async (
            string slug,
            ClaimsPrincipal user, ITeamRepository teams, IContactRepository contacts,
            bool includeArchived = false, CancellationToken ct = default) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            return Results.Ok(await contacts.GetAllAsync(
                ContextRef.Team(resolved.Team!.Id), includeArchived, ct));
        })
        .WithName("ListTeamContacts")
        .RequireScope("read:contacts");

        group.MapGet("/{slug}/contacts/search", async (
            string slug, string q,
            ClaimsPrincipal user, ITeamRepository teams, IContactRepository contacts,
            int limit = 50, CancellationToken ct = default) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            var hits = await contacts.SearchAsync(
                ContextRef.Team(resolved.Team!.Id), q ?? "", limit, ct);
            return Results.Ok(hits);
        })
        .WithName("SearchTeamContacts")
        .RequireScope("read:contacts");

        group.MapGet("/{slug}/contacts/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ITeamRepository teams, IContactRepository contacts,
            CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            var item = await contacts.GetByIdAsync(ContextRef.Team(resolved.Team!.Id), id, ct);
            return item is not null ? Results.Ok(item) : Results.NotFound();
        })
        .WithName("GetTeamContact")
        .RequireScope("read:contacts");

        group.MapPost("/{slug}/contacts", async (
            string slug, Contact contact,
            ClaimsPrincipal user, ITeamRepository teams, IContactRepository contacts,
            CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(contact.Name))
                return Results.BadRequest(new { error = "name is required" });

            var actorId = user.FindFirst(McpContextClaims.UserId)!.Value;
            var id = await contacts.CreateAsync(
                ContextRef.Team(resolved.Team!.Id), actorId, contact, ct);
            return Results.Created($"/api/v1/teams/{slug}/contacts/{id}", contact);
        })
        .WithName("CreateTeamContact")
        .RequireScope("write:contacts");

        group.MapPut("/{slug}/contacts/{id}", async (
            string slug, string id, Contact contact,
            ClaimsPrincipal user, ITeamRepository teams, IContactRepository contacts,
            CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(contact.Name))
                return Results.BadRequest(new { error = "name is required" });

            contact.Id = id;
            var ok = await contacts.UpdateAsync(ContextRef.Team(resolved.Team!.Id), contact, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("UpdateTeamContact")
        .RequireScope("write:contacts");

        group.MapDelete("/{slug}/contacts/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ITeamRepository teams, IContactRepository contacts,
            CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            var ok = await contacts.DeleteAsync(ContextRef.Team(resolved.Team!.Id), id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteTeamContact")
        .RequireScope("write:contacts");

        // ────────── Nested: team re-index (cookie-only, like /api/v1/search/reindex) ──────────

        // Hybrid notes search, team-scoped. Mirrors /api/v1/search with the
        // resolved team context — same response shape, same scope.
        group.MapGet("/{slug}/search", async (
            string slug, string q,
            ClaimsPrincipal user, ITeamRepository teams,
            Fishbowl.Core.Search.ISearchService search,
            int limit = 20, bool includePending = true, CancellationToken ct = default) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;

            limit = Math.Clamp(limit, 1, 100);
            var result = await search.HybridSearchAsync(
                ContextRef.Team(resolved.Team!.Id), q ?? "", limit, includePending, ct);
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
        .WithName("SearchTeamNotes")
        .RequireScope("read:notes");

        group.MapPost("/{slug}/search/reindex", async (
            string slug,
            ClaimsPrincipal user, ITeamRepository teams, INoteRepository notes, CancellationToken ct) =>
        {
            // Mirror of SearchApi.cs: maintenance endpoint is cookie-only.
            if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme)
                return Results.Forbid();

            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            var result = await notes.ReEmbedAllAsync(ContextRef.Team(resolved.Team!.Id), ct);
            return Results.Ok(new { processed = result.Processed, failed = result.Failed });
        })
        .WithName("ReindexTeamSearch");

        // ────────── Nested: team events ──────────

        group.MapGet("/{slug}/events", async (
            string slug,
            ClaimsPrincipal user, ITeamRepository teams, IEventRepository events,
            DateTime? from = null, DateTime? to = null, CancellationToken ct = default) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;

            if ((from is null) != (to is null))
                return Results.BadRequest(new { error = "from and to must both be provided or both omitted" });

            var teamCtx = ContextRef.Team(resolved.Team!.Id);
            if (from is not null)
                return Results.Ok(await events.GetRangeAsync(teamCtx, from.Value, to!.Value, ct));
            return Results.Ok(await events.GetAllAsync(teamCtx, ct));
        })
        .WithName("ListTeamEvents")
        .RequireScope("read:events");

        group.MapGet("/{slug}/events/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ITeamRepository teams, IEventRepository events,
            CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            var item = await events.GetByIdAsync(ContextRef.Team(resolved.Team!.Id), id, ct);
            return item is not null ? Results.Ok(item) : Results.NotFound();
        })
        .WithName("GetTeamEvent")
        .RequireScope("read:events");

        group.MapPost("/{slug}/events", async (
            string slug, Event evt,
            ClaimsPrincipal user, ITeamRepository teams, IEventRepository events,
            CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            try
            {
                var actorId = user.FindFirst(McpContextClaims.UserId)!.Value;
                var id = await events.CreateAsync(
                    ContextRef.Team(resolved.Team!.Id), actorId, evt, ct);
                return Results.Created($"/api/v1/teams/{slug}/events/{id}", evt);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CreateTeamEvent")
        .RequireScope("write:events");

        group.MapPut("/{slug}/events/{id}", async (
            string slug, string id, Event evt,
            ClaimsPrincipal user, ITeamRepository teams, IEventRepository events,
            CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            evt.Id = id;
            try
            {
                var ok = await events.UpdateAsync(ContextRef.Team(resolved.Team!.Id), evt, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UpdateTeamEvent")
        .RequireScope("write:events");

        group.MapDelete("/{slug}/events/{id}", async (
            string slug, string id,
            ClaimsPrincipal user, ITeamRepository teams, IEventRepository events,
            CancellationToken ct) =>
        {
            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanWrite()) return Results.Forbid();

            var ok = await events.DeleteAsync(ContextRef.Team(resolved.Team!.Id), id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteTeamEvent")
        .RequireScope("write:events");

        // ────────── Nested: team DB export (cookie-only, owner-only) ──────────
        // A team DB copy is a terminal, migrate-your-data-out action — owner
        // only, same rule as team deletion. Members already have read access
        // through the API; this endpoint is about walking off with the whole
        // database file.
        group.MapGet("/{slug}/export/db", async (
            string slug,
            ClaimsPrincipal user, ITeamRepository teams, DatabaseFactory dbFactory, CancellationToken ct) =>
        {
            if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme)
                return Results.Forbid();

            var resolved = await ResolveTeamAsync(slug, user, teams, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanDeleteTeam()) return Results.Forbid();

            var teamCtx = ContextRef.Team(resolved.Team!.Id);
            var bytes = await ExportApi.BackupContextAsync(dbFactory, teamCtx, ct);
            var filename = $"fishbowl-team-{resolved.Team.Slug}-{DateTime.UtcNow:yyyyMMdd}.db";
            return Results.File(bytes, "application/vnd.sqlite3", filename);
        })
        .WithName("ExportTeamDatabase");

        return group.RequireAuthorization();
    }

    private record TeamResolution(Team? Team, TeamRole? Role, IResult? Error);

    // Resolves {slug} → Team + the caller's TeamRole. Returns an Error result
    // (401/404/403) when the caller isn't authenticated, the team doesn't
    // exist, or the caller isn't a member. Keeps the endpoint handlers above
    // declarative.
    private static async Task<TeamResolution> ResolveTeamAsync(
        string slug, ClaimsPrincipal user, ITeamRepository teams, CancellationToken ct)
    {
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (string.IsNullOrEmpty(userId))
            return new TeamResolution(null, null, Results.Unauthorized());

        var team = await teams.GetBySlugAsync(slug, ct);
        if (team is null)
            return new TeamResolution(null, null, Results.NotFound());

        // Bearer-context match: if the principal is a token, it must be bound
        // to THIS team. A personal token on a team URL is rejected even when
        // the underlying user is a team member — the token's own context is
        // the authoritative scope, not the human behind it.
        if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme)
        {
            var ctxType = user.FindFirst(McpContextClaims.ContextType)?.Value;
            var ctxId = user.FindFirst(McpContextClaims.ContextId)?.Value;
            if (ctxType != "team" || ctxId != team.Slug)
                return new TeamResolution(team, null, Results.Forbid());
        }

        var role = await teams.GetMembershipAsync(team.Id, userId, ct);
        if (role is null)
            return new TeamResolution(team, null, Results.Forbid());

        return new TeamResolution(team, role, null);
    }
}
