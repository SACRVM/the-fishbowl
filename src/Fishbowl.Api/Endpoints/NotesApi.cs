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
    // A key (an MCP client, an OAuth connector, a script) never sees a
    // secret: what it reads goes through SecretStripper.StripNote, as on the
    // MCP wire. The signed-in UI gets the note as stored — it needs the
    // ciphertext envelope to decrypt. Shared with the space routes.
    internal static bool IsKey(ClaimsPrincipal user)
        => user.Identity?.AuthenticationType == McpContextClaims.BearerScheme;

    internal static Note ForCaller(Note note, ClaimsPrincipal user)
        => IsKey(user) ? SecretStripper.StripNote(note) : note;

    // A key can't see secrets, so a PUT from one would overwrite them with
    // "[secret content hidden]" and drop the ciphertext.
    internal static bool HoldsSecret(Note note)
        => SecretStripper.ContainsSecret(note.Content) || note.ContentSecret is { Length: > 0 };

    internal static IResult SecretNoteRefused()
        => ApiErrors.Conflict("secret_note", "This note holds secrets — agents can't change it; edit it in Fishbowl.");

    public static RouteGroupBuilder MapNotesApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/notes");

        // Resolves the request to a ContextRef. Cookie users get ContextRef.User
        // (their fishbowl_user_id). Bearer users get whatever their key was
        // issued against — personal → User, space → Space. So a space-scoped
        // Bearer hitting /api/v1/notes reads SPACE notes, not the user's
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
        static NoteSource SourceForPrincipal(ClaimsPrincipal user) => NoteSources.Of(user);

        group.MapGet("/", async (
            string[]? tag,
            string? match,
            int? limit,
            int? offset,
            ClaimsPrincipal user,
            INoteRepository repo,
            CancellationToken ct) =>
        {
            var ctx = TryResolveContext(user);
            if (ctx is null) return Results.Unauthorized();

            var tags = tag is { Length: > 0 } ? tag : null;
            var matchMode = match == "all" ? "all" : "any";
            var notes = await repo.GetAllAsync(ctx.Value, tags, matchMode, limit, offset ?? 0, ct);
            return Results.Ok(notes.Select(n => ForCaller(n, user)));
        })
        .WithName("ListNotes")
        .WithSummary("Lists notes for the resolved context. Optional ?tag=foo&tag=bar&match=any|all filter and ?limit=&offset= paging.")
        .Produces<IEnumerable<Note>>()
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("read:notes");

        group.MapGet("/{id}", async (string id, ClaimsPrincipal user, INoteRepository repo, CancellationToken ct) =>
        {
            var ctx = TryResolveContext(user);
            if (ctx is null) return Results.Unauthorized();

            var note = await repo.GetByIdAsync(ctx.Value, id, ct);
            return note is not null ? Results.Ok(ForCaller(note, user)) : Results.NotFound();
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
            catch (ResourceValidationException ex)
            {
                return ValidationResults.From(ex);
            }
        })
        .WithName("CreateNote")
        .WithSummary("Creates a new note in the resolved context.")
        .Produces<Note>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status413PayloadTooLarge)
        .RequireScope("write:notes");

        group.MapPut("/{id}", async (string id, Note note, ClaimsPrincipal user, INoteRepository repo, CancellationToken ct) =>
        {
            var ctx = TryResolveContext(user);
            if (ctx is null) return Results.Unauthorized();

            note.Id = id;
            if (IsKey(user) && await repo.GetByIdAsync(ctx.Value, id, ct) is { } current && HoldsSecret(current))
                return SecretNoteRefused();
            try
            {
                var updated = await repo.UpdateAsync(ctx.Value, note, SourceForPrincipal(user), ct);
                return updated ? Results.NoContent() : Results.NotFound();
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.From(ex);
            }
        })
        .WithName("UpdateNote")
        .WithSummary("Updates an existing note.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status413PayloadTooLarge)
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
