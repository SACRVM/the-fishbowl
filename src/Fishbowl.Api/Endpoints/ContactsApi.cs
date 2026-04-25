using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;

namespace Fishbowl.Api.Endpoints;

// CONCEPT.md § Contacts — "Not an address book. A living record of the
// people who matter." Backend scaffolding only; UI wiring comes later.
// Shape mirrors TodoApi: personal-only route here, team-nested variant
// lives in TeamsApi.cs so the team-resolution/role-gating code stays
// next to the team notes/todos handlers.
public static class ContactsApi
{
    public static RouteGroupBuilder MapContactsApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/contacts");

        group.MapGet("/", async (
            ClaimsPrincipal user, IContactRepository repo,
            bool includeArchived = false,
            CancellationToken ct = default) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            return Results.Ok(await repo.GetAllAsync(userId, includeArchived, ct));
        })
        .WithName("ListContacts")
        .WithSummary("Lists all contacts for the authenticated user.")
        .Produces<IEnumerable<Contact>>()
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("read:contacts");

        // Search lives *before* /{id} so its static segment wins routing —
        // "search" would otherwise be indistinguishable from a contact id.
        group.MapGet("/search", async (
            ClaimsPrincipal user, IContactRepository repo,
            string q, int limit = 50, CancellationToken ct = default) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            var hits = await repo.SearchAsync(
                Fishbowl.Core.ContextRef.User(userId), q ?? "", limit, ct);
            return Results.Ok(hits);
        })
        .WithName("SearchContacts")
        .WithSummary("FTS5 search across name/email/phone/notes, ranked by bm25.")
        .Produces<IEnumerable<Contact>>()
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("read:contacts");

        group.MapGet("/{id}", async (
            string id, ClaimsPrincipal user, IContactRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            var item = await repo.GetByIdAsync(userId, id, ct);
            return item is not null ? Results.Ok(item) : Results.NotFound();
        })
        .WithName("GetContact")
        .Produces<Contact>()
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("read:contacts");

        group.MapPost("/", async (
            Contact contact, ClaimsPrincipal user, IContactRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(contact.Name))
                return Results.BadRequest(new { error = "name is required" });

            try
            {
                var id = await repo.CreateAsync(userId, contact, ct);
                return Results.Created($"/api/v1/contacts/{id}", contact);
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.PayloadTooLarge(ex);
            }
        })
        .WithName("CreateContact")
        .Produces<Contact>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status413PayloadTooLarge)
        .RequireScope("write:contacts");

        group.MapPut("/{id}", async (
            string id, Contact contact, ClaimsPrincipal user,
            IContactRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(contact.Name))
                return Results.BadRequest(new { error = "name is required" });

            contact.Id = id;
            try
            {
                var updated = await repo.UpdateAsync(userId, contact, ct);
                return updated ? Results.NoContent() : Results.NotFound();
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.PayloadTooLarge(ex);
            }
        })
        .WithName("UpdateContact")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status413PayloadTooLarge)
        .RequireScope("write:contacts");

        group.MapDelete("/{id}", async (
            string id, ClaimsPrincipal user, IContactRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var deleted = await repo.DeleteAsync(userId, id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteContact")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireScope("write:contacts");

        return group.RequireAuthorization();
    }
}
