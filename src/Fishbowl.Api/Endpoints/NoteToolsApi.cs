using System.Security.Claims;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// What the Notes app needs beyond CRUD (spec 2026-11-06-notes-for-agents), in
// both workspaces — personal /api/v1/notes/…, space /api/v1/spaces/<slug>/notes/…:
//   PATCH  …/{id}                         a NotePatch, applied atomically: tags
//                                         added / removed (a board move), append,
//                                         slug, pin, archive, the lock
//   GET    …/{id}/versions                what agents changed (no text)
//   GET    …/{id}/versions/{vid}          one version with its text
//   POST   …/{id}/versions/{vid}/restore  back to it (a person, a writer)
//   GET    …/filters                      the saved filters
//   POST   …/filters                      { name, expr }
//   PATCH  …/filters/{id}                 { name?, expr?, position? }
//   DELETE …/filters/{id}
// The personal path resolves a space key to its space, like /api/v1/notes.
// Lifting a lock is a person's act: the personal workspace's owner, a space's
// Admin or Owner — never a key.
public static class NoteToolsApi
{
    public sealed record FilterRequest(string? Name, string? Expr, double? Position);

    public static IEndpointRouteBuilder MapNoteToolsApi(this IEndpointRouteBuilder routes)
    {
        Map(routes.MapGroup("/api/v1/notes").RequireAuthorization(), space: false);
        Map(routes.MapGroup("/api/v1/spaces/{slug}/notes").RequireAuthorization(), space: true);
        return routes;
    }

    private sealed record Target(ContextRef Ctx, string UserId, bool CanWrite, bool CanUnlock);

    private static async Task<(Target? T, IResult? Error)> ResolveAsync(HttpContext http, bool space, bool write, CancellationToken ct)
    {
        var user = http.User;
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (string.IsNullOrEmpty(userId)) return (null, Results.Unauthorized());
        var key = NotesApi.IsKey(user);
        if (key && !user.HasClaim(McpContextClaims.Scope, write ? "write:notes" : "read:notes")) return (null, Results.Forbid());
        if (!space)
        {
            ContextRef ctx;
            try { ctx = McpContextClaims.Resolve(user); }
            catch (InvalidOperationException) { return (null, Results.Unauthorized()); }
            // A key writes as far as its scopes (trimmed to its owner's role).
            return (new Target(ctx, userId, CanWrite: true, CanUnlock: !key && ctx.Type == ContextType.User), null);
        }
        var spaces = (ISpaceRepository)http.RequestServices.GetService(typeof(ISpaceRepository))!;
        var resolved = await SpacesApi.ResolveSpaceAsync((string)http.Request.RouteValues["slug"]!, user, spaces, ct);
        if (resolved.Error is not null) return (null, resolved.Error);
        var role = resolved.Role!.Value;
        if (write && !role.CanWrite()) return (null, Results.Forbid());
        return (new Target(ContextRef.Space(resolved.Space!.Id), userId, role.CanWrite(), !key && role >= SpaceRole.Admin), null);
    }

    private static IResult Locked() => ApiErrors.Conflict("note_locked",
        "Only the workspace's owner — in a space an Admin — can unlock a note.");

    private static void Map(RouteGroupBuilder g, bool space)
    {
        var tag = space ? "Space" : "";

        g.MapPatch("/{id}", async (string id, NotePatch body, HttpContext http, INoteRepository notes, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, write: true, ct);
            if (err is not null) return err;
            if (body.Locked == false && !t!.CanUnlock) return Locked();
            if (NotesApi.IsKey(http.User) && (body.Content is not null || body.Append is not null)
                && await notes.GetByIdAsync(t!.Ctx, id, ct) is { } current && NotesApi.HoldsSecret(current))
                return NotesApi.SecretNoteRefused();
            try
            {
                var note = await notes.PatchAsync(t!.Ctx, id, body, NoteSources.Of(http.User), t.UserId, ct);
                return note is null ? Results.NotFound() : Results.Ok(NotesApi.ForCaller(note, http.User));
            }
            catch (ResourceValidationException ex) { return ValidationResults.From(ex); }
        }).WithName($"Patch{tag}Note").WithSummary("Changes a note in place: tags added/removed, append, slug, pin, archive, lock.");

        g.MapGet("/{id}/versions", async (string id, HttpContext http, INoteRepository notes, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, write: false, ct);
            if (err is not null) return err;
            var versions = await notes.ListVersionsAsync(t!.Ctx, id, ct);
            return Results.Ok(versions.Select(v => new { v.Id, v.Title, v.Tags, savedAt = v.SavedAt.ToUniversalTime(), v.SavedBy, v.Source }));
        }).WithName($"List{tag}NoteVersions").WithSummary("A note as it was before each agent's change, newest first (without the text).");

        g.MapGet("/{id}/versions/{vid}", async (string id, string vid, HttpContext http, INoteRepository notes, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, write: false, ct);
            if (err is not null) return err;
            var v = await notes.GetVersionAsync(t!.Ctx, id, vid, ct);
            if (v is null) return Results.NotFound();
            var content = NotesApi.IsKey(http.User) ? SecretStripper.Strip(v.Content) : v.Content;
            return Results.Ok(new { v.Id, v.Title, content, v.Tags, savedAt = v.SavedAt.ToUniversalTime(), v.SavedBy, v.Source });
        }).WithName($"Get{tag}NoteVersion").WithSummary("One earlier version of a note, with its text.");

        g.MapPost("/{id}/versions/{vid}/restore", async (string id, string vid, HttpContext http, INoteRepository notes, CancellationToken ct) =>
        {
            if (NotesApi.IsKey(http.User)) return Results.Forbid();
            var (t, err) = await ResolveAsync(http, space, write: true, ct);
            if (err is not null) return err;
            var v = await notes.GetVersionAsync(t!.Ctx, id, vid, ct);
            if (v is null) return Results.NotFound();
            if (await notes.GetByIdAsync(t.Ctx, id, ct) is { } current && NotesApi.HoldsSecret(current))
                return NotesApi.SecretNoteRefused();
            try
            {
                var note = await notes.PatchAsync(t.Ctx, id,
                    new NotePatch { Title = v.Title, Content = v.Content ?? "", Tags = v.Tags }, NoteSource.Human, t.UserId, ct);
                return note is null ? Results.NotFound() : Results.Ok(note);
            }
            catch (ResourceValidationException ex) { return ValidationResults.From(ex); }
        }).WithName($"Restore{tag}NoteVersion").WithSummary("Puts a note back to an earlier version (a person's act).");

        g.MapGet("/filters", async (HttpContext http, INoteFilterRepository filters, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, write: false, ct);
            if (err is not null) return err;
            return Results.Ok(await filters.ListAsync(t!.Ctx, ct));
        }).WithName($"List{tag}NoteFilters").WithSummary("The Notes app's saved filters of the workspace.");

        g.MapPost("/filters", async (FilterRequest body, HttpContext http, INoteFilterRepository filters, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, write: true, ct);
            if (err is not null) return err;
            try { return Results.Ok(await filters.CreateAsync(t!.Ctx, t.UserId, body.Name ?? "", body.Expr ?? "", ct)); }
            catch (ArgumentException ex) { return ApiErrors.BadRequest("invalid_value", ex.Message, new { reason = ex.Message }); }
        }).WithName($"Create{tag}NoteFilter").WithSummary("Saves a filter: a name and a tag expression.");

        g.MapPatch("/filters/{fid}", async (string fid, FilterRequest body, HttpContext http, INoteFilterRepository filters, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, write: true, ct);
            if (err is not null) return err;
            try
            {
                var f = await filters.UpdateAsync(t!.Ctx, fid, body.Name, body.Expr, body.Position, ct);
                return f is null ? Results.NotFound() : Results.Ok(f);
            }
            catch (ArgumentException ex) { return ApiErrors.BadRequest("invalid_value", ex.Message, new { reason = ex.Message }); }
        }).WithName($"Update{tag}NoteFilter").WithSummary("Renames, changes or moves a saved filter.");

        g.MapDelete("/filters/{fid}", async (string fid, HttpContext http, INoteFilterRepository filters, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, write: true, ct);
            if (err is not null) return err;
            return await filters.DeleteAsync(t!.Ctx, fid, ct) ? Results.NoContent() : Results.NotFound();
        }).WithName($"Delete{tag}NoteFilter").WithSummary("Deletes a saved filter.");
    }
}
