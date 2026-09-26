using System.Security.Claims;
using Fishbowl.Core.Files;
using Fishbowl.Core.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// Archived spaces (Files phase 3, spec § Space delete and archived spaces):
// what deleting a space with "Archive before deleting" leaves behind. Each
// archive is listed to the owner it belonged to and to whoever archived it —
// read straight from the ZIPs' manifests. Cookie-only; Bearer gets 403.
//   GET    /api/v1/archive/spaces                 list (newest first, days left)
//   GET    /api/v1/archive/spaces/{id}/download   the ZIP (a complete cold-import copy)
//   POST   /api/v1/archive/spaces/{id}/restore    a new space, you as its only member
//   DELETE /api/v1/archive/spaces/{id}            remove the ZIP
public static class ArchiveApi
{
    public static RouteGroupBuilder MapArchiveApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/archive/spaces");

        group.MapGet("/", async (ClaimsPrincipal user, ISpaceArchiveService archiver, CancellationToken ct) =>
        {
            var (userId, error) = Caller(user);
            return error ?? Results.Ok(await archiver.ListAsync(userId!, ct));
        })
        .WithName("ListArchivedSpaces");

        group.MapGet("/{id}/download", async (string id, ClaimsPrincipal user, ISpaceArchiveService archiver, CancellationToken ct) =>
        {
            var (userId, error) = Caller(user);
            if (error is not null) return error;
            var path = await archiver.GetPathAsync(id, userId!, ct);
            return path is null
                ? Results.NotFound()
                : Results.File(path, "application/zip", $"fishbowl-archive-{id}.zip", enableRangeProcessing: true);
        })
        .WithName("DownloadArchivedSpace");

        group.MapPost("/{id}/restore", async (string id, ClaimsPrincipal user, ISpaceArchiveService archiver, CancellationToken ct) =>
        {
            var (userId, error) = Caller(user);
            if (error is not null) return error;
            try
            {
                var space = await archiver.RestoreAsync(id, userId!, ct);
                return space is null
                    ? Results.NotFound()
                    : Results.Ok(new { id = space.Id, slug = space.Slug, name = space.Name, color = space.Color });
            }
            catch (FileStoreException ex) { return FilesApi.Fail(ex); }
        })
        .WithName("RestoreArchivedSpace");

        group.MapDelete("/{id}", async (string id, ClaimsPrincipal user, ISpaceArchiveService archiver, CancellationToken ct) =>
        {
            var (userId, error) = Caller(user);
            if (error is not null) return error;
            return await archiver.DeleteAsync(id, userId!, ct) ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteArchivedSpace");

        return group.RequireAuthorization();
    }

    private static (string? UserId, IResult? Error) Caller(ClaimsPrincipal user)
    {
        if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return (null, Results.Forbid());
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        return string.IsNullOrEmpty(userId) ? (null, Results.Unauthorized()) : (userId, null);
    }
}
