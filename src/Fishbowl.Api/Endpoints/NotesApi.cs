using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;

namespace Fishbowl.Api.Endpoints;

public static class NotesApi
{
    public static RouteGroupBuilder MapNotesApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/notes");

        // Resolves the request to a ContextRef. Cookie users get ContextRef.User
        // (their fishbowl_user_id). Bearer users get whatever their key was
        // issued against — personal → User, team → Team. So a team-scoped
        // Bearer hitting /api/v1/notes reads TEAM notes, not the user's
        // personal notes. Acceptance criterion from the MCP memory plan.
        static ContextRef? TryResolveContext(ClaimsPrincipal user)
        {
            try { return McpContextClaims.Resolve(user); }
            catch (InvalidOperationException) { return null; }
        }

        static string? ActorUserId(ClaimsPrincipal user)
            => user.FindFirst(McpContextClaims.UserId)?.Value;

        // Bearer clients are MCP-ish; cookie users are humans. The auth
        // scheme is the authoritative signal here — matches how the
        // RequireScope helper gates access.
        static NoteSource SourceForPrincipal(ClaimsPrincipal user)
            => user.Identity?.AuthenticationType == McpContextClaims.BearerScheme
                ? NoteSource.Mcp
                : NoteSource.Human;

        group.MapGet("/", async (
            string[]? tag,
            string? match,
            ClaimsPrincipal user,
            INoteRepository repo,
            CancellationToken ct) =>
        {
            var ctx = TryResolveContext(user);
            if (ctx is null) return Results.Unauthorized();

            var tags = tag is { Length: > 0 } ? tag : null;
            var matchMode = match == "all" ? "all" : "any";
            return Results.Ok(await repo.GetAllAsync(ctx.Value, tags, matchMode, ct));
        })
        .WithName("ListNotes")
        .WithSummary("Lists notes for the resolved context. Optional ?tag=foo&tag=bar&match=any|all filter.")
        .Produces<IEnumerable<Note>>()
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("read:notes");

        group.MapGet("/{id}", async (string id, ClaimsPrincipal user, INoteRepository repo, CancellationToken ct) =>
        {
            var ctx = TryResolveContext(user);
            if (ctx is null) return Results.Unauthorized();

            var note = await repo.GetByIdAsync(ctx.Value, id, ct);
            return note is not null ? Results.Ok(note) : Results.NotFound();
        })
        .WithName("GetNote")
        .WithSummary("Gets a single note by id.")
        .Produces<Note>()
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("read:notes");

        group.MapPost("/", async (Note note, ClaimsPrincipal user, INoteRepository repo, CancellationToken ct) =>
        {
            var ctx = TryResolveContext(user);
            var actor = ActorUserId(user);
            if (ctx is null || string.IsNullOrEmpty(actor)) return Results.Unauthorized();

            try
            {
                var id = await repo.CreateAsync(ctx.Value, actor, note, SourceForPrincipal(user), ct);
                return Results.Created($"/api/v1/notes/{id}", note);
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
        .WithName("CreateNote")
        .WithSummary("Creates a new note in the resolved context.")
        .Produces<Note>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("write:notes");

        group.MapPut("/{id}", async (string id, Note note, ClaimsPrincipal user, INoteRepository repo, CancellationToken ct) =>
        {
            var ctx = TryResolveContext(user);
            if (ctx is null) return Results.Unauthorized();

            note.Id = id;
            try
            {
                var updated = await repo.UpdateAsync(ctx.Value, note, SourceForPrincipal(user), ct);
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
        .WithName("UpdateNote")
        .WithSummary("Updates an existing note.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("write:notes");

        group.MapDelete("/{id}", async (string id, ClaimsPrincipal user, INoteRepository repo, CancellationToken ct) =>
        {
            var ctx = TryResolveContext(user);
            if (ctx is null) return Results.Unauthorized();

            var success = await repo.DeleteAsync(ctx.Value, id, ct);
            return success ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteNote")
        .WithSummary("Deletes a note.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("write:notes");

        return group.RequireAuthorization();
    }
}
