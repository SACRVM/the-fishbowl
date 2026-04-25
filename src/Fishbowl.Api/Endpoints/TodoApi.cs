using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;

namespace Fishbowl.Api.Endpoints;

public static class TodoApi
{
    public static RouteGroupBuilder MapTodoApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/todos");

        group.MapGet("/", async (ClaimsPrincipal user, ITodoRepository repo, bool includeCompleted = false, CancellationToken ct = default) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            return Results.Ok(await repo.GetAllAsync(userId, includeCompleted, ct));
        })
        .WithName("ListTodos")
        .WithSummary("Lists all todos for the authenticated user.")
        .Produces<IEnumerable<TodoItem>>()
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("read:tasks");

        group.MapGet("/{id}", async (string id, ClaimsPrincipal user, ITodoRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var item = await repo.GetByIdAsync(userId, id, ct);
            return item != null ? Results.Ok(item) : Results.NotFound();
        })
        .WithName("GetTodo")
        .WithSummary("Gets a single todo by id.")
        .Produces<TodoItem>()
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("read:tasks");

        group.MapPost("/", async (TodoItem item, ClaimsPrincipal user, ITodoRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            try
            {
                var id = await repo.CreateAsync(userId, item, ct);
                return Results.Created($"/api/v1/todos/{id}", item);
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.PayloadTooLarge(ex);
            }
        })
        .WithName("CreateTodo")
        .WithSummary("Creates a new todo.")
        .Produces<TodoItem>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status413PayloadTooLarge)
        .RequireScope("write:tasks");

        group.MapPut("/{id}", async (string id, TodoItem item, ClaimsPrincipal user, ITodoRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            item.Id = id;
            try
            {
                var updated = await repo.UpdateAsync(userId, item, ct);
                return updated ? Results.NoContent() : Results.NotFound();
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.PayloadTooLarge(ex);
            }
        })
        .WithName("UpdateTodo")
        .WithSummary("Updates an existing todo.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status413PayloadTooLarge)
        .RequireScope("write:tasks");

        group.MapDelete("/{id}", async (string id, ClaimsPrincipal user, ITodoRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var deleted = await repo.DeleteAsync(userId, id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteTodo")
        .WithSummary("Deletes a todo.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("write:tasks");

        return group.RequireAuthorization();
    }
}
