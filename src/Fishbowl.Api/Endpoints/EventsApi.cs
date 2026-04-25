using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;

namespace Fishbowl.Api.Endpoints;

// CONCEPT.md § Calendar — "A calendar that belongs to you." Personal
// CRUD over the events table (schema v1). No recurrence expansion on
// read (callers get the master event; the future scheduler expands
// RRULE when firing reminders). Team variant lives in TeamsApi.
public static class EventsApi
{
    public static RouteGroupBuilder MapEventsApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/events");

        group.MapGet("/", async (
            ClaimsPrincipal user, IEventRepository repo,
            DateTime? from = null, DateTime? to = null, CancellationToken ct = default) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            // Either both from+to or neither. A half-range would silently
            // return weird results — fail fast instead.
            if ((from is null) != (to is null))
                return Results.BadRequest(new { error = "from and to must both be provided or both omitted" });

            if (from is not null)
                return Results.Ok(await repo.GetRangeAsync(userId, from.Value, to!.Value, ct));
            return Results.Ok(await repo.GetAllAsync(userId, ct));
        })
        .WithName("ListEvents")
        .WithSummary("Lists events. Optional ?from=&to= returns a chronological range.")
        .Produces<IEnumerable<Event>>()
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("read:events");

        group.MapGet("/{id}", async (
            string id, ClaimsPrincipal user, IEventRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            var item = await repo.GetByIdAsync(userId, id, ct);
            return item is not null ? Results.Ok(item) : Results.NotFound();
        })
        .WithName("GetEvent")
        .Produces<Event>()
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("read:events");

        group.MapPost("/", async (
            Event evt, ClaimsPrincipal user, IEventRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            try
            {
                var id = await repo.CreateAsync(userId, evt, ct);
                return Results.Created($"/api/v1/events/{id}", evt);
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
        .WithName("CreateEvent")
        .Produces<Event>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status413PayloadTooLarge)
        .RequireScope("write:events");

        group.MapPut("/{id}", async (
            string id, Event evt, ClaimsPrincipal user,
            IEventRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            evt.Id = id;
            try
            {
                var updated = await repo.UpdateAsync(userId, evt, ct);
                return updated ? Results.NoContent() : Results.NotFound();
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
        .WithName("UpdateEvent")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status413PayloadTooLarge)
        .RequireScope("write:events");

        group.MapDelete("/{id}", async (
            string id, ClaimsPrincipal user, IEventRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var deleted = await repo.DeleteAsync(userId, id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteEvent")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("write:events");

        return group.RequireAuthorization();
    }
}
