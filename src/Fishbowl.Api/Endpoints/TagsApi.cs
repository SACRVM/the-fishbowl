using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Api.Endpoints;

public static class TagsApi
{
    public record UpsertColorRequest(string Color);
    public record RenameRequest(string NewName);

    public static RouteGroupBuilder MapTagsApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/tags").PersonalKeysOnly();

        group.MapGet("/", async (ClaimsPrincipal user, ITagRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            return Results.Ok(await repo.GetAllAsync(userId, ct));
        })
        .WithName("ListTags")
        .WithSummary("Lists all tags for the authenticated user with usage counts.")
        .Produces<IEnumerable<Tag>>()
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("read:tags");

        group.MapPut("/{name}", async (
            string name,
            UpsertColorRequest body,
            ClaimsPrincipal user,
            ITagRepository repo,
            CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            try
            {
                var tag = await repo.UpsertColorAsync(userId, name, body.Color, ct);
                return Results.Ok(tag);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UpsertTagColor")
        .WithSummary("Creates a tag with a color, or updates an existing tag's color.")
        .Produces<Tag>()
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("write:tags");

        group.MapPost("/{name}/rename", async (
            string name,
            RenameRequest body,
            ClaimsPrincipal user,
            ITagRepository repo,
            CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            try
            {
                var renamed = await repo.RenameAsync(userId, name, body.NewName, ct);
                return renamed ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("RenameTag")
        .WithSummary("Renames a tag and rewrites every referencing note.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("write:tags");

        group.MapDelete("/{name}", async (
            string name,
            ClaimsPrincipal user,
            ITagRepository repo,
            CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            try
            {
                var deleted = await repo.DeleteAsync(userId, name, ct);
                return deleted ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("DeleteTag")
        .WithSummary("Deletes a tag and strips it from every referencing note.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("write:tags");

        return group.RequireAuthorization();
    }
}
