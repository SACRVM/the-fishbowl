using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;

namespace Fishbowl.Api.Endpoints;

public static class AccountApi
{
    public static IEndpointRouteBuilder MapAccountApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1");

        group.MapGet("/me", async (ClaimsPrincipal user, ISystemRepository repo, CancellationToken ct) =>
        {
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var profile = await repo.GetUserAsync(userId, ct);
            if (profile is null) return Results.NotFound();

            // An explicit projection: the User record also carries the local
            // password hash and salt, which never leave the server.
            return Results.Ok(new
            {
                id = profile.Id,
                name = profile.Name,
                email = profile.Email,
                avatarUrl = profile.AvatarUrl,
                createdAt = profile.CreatedAt,
                isAdmin = profile.IsAdmin,
                accent = profile.Accent,
                dateFormat = profile.DateFormat,
            });
        })
        .WithName("GetMe")
        .WithSummary("Returns the current authenticated user's profile.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .RequireAuthorization();

        // Personal settings: the UI accent and the date & time format. A
        // partial update — only the fields present in the body change, and
        // null resets one to its default. Cookie-only: an agent's key has no
        // business restyling its owner's UI.
        group.MapPatch("/me", async (JsonElement body, ClaimsPrincipal user, ISystemRepository repo, CancellationToken ct) =>
        {
            if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return Results.Forbid();
            var userId = user.FindFirst("fishbowl_user_id")?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            if (body.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { error = "expected a JSON object" });

            // Present → (true, value-or-null); a non-string, non-null value is invalid.
            static (bool present, bool valid, string? value) Field(JsonElement obj, string name) =>
                !obj.TryGetProperty(name, out var v) ? (false, true, null)
                : v.ValueKind == JsonValueKind.Null ? (true, true, null)
                : v.ValueKind == JsonValueKind.String ? (true, true, v.GetString())
                : (true, false, null);

            var accent = Field(body, "accent");
            if (!accent.valid || (accent.value is not null && !TagPalette.IsSlot(accent.value)))
                return Results.BadRequest(new { error = "accent must be a palette slot or null" });
            var dateFormat = Field(body, "dateFormat");
            if (!dateFormat.valid || (dateFormat.value is not null && !DateFormats.IsKnown(dateFormat.value)))
                return Results.BadRequest(new { error = "dateFormat must be one of iso, de, uk, us or null" });

            if (accent.present && !await repo.SetAccentAsync(userId, accent.value, ct)) return Results.NotFound();
            if (dateFormat.present && !await repo.SetDateFormatAsync(userId, dateFormat.value, ct)) return Results.NotFound();
            return Results.NoContent();
        })
        .WithName("UpdateMe")
        .WithSummary("Updates the current user's settings (accent, date & time format). Partial; cookie only.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .RequireAuthorization();

        // Diagnostic: what does the server think about this request's auth?
        // Used by MCP clients to confirm a Bearer token is valid and to
        // discover the context + scopes it's bound to. Cookie users see
        // their personal context and no scopes (cookies have full access).
        group.MapGet("/auth/whoami", (ClaimsPrincipal user) =>
        {
            var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var ctxType = user.FindFirst(McpContextClaims.ContextType)?.Value ?? "user";
            var ctxId = user.FindFirst(McpContextClaims.ContextId)?.Value ?? userId;
            var scopes = user.FindAll(McpContextClaims.Scope).Select(c => c.Value).ToList();

            return Results.Ok(new
            {
                userId,
                context = new { type = ctxType, id = ctxId },
                scopes,
                authType = user.Identity?.AuthenticationType,
            });
        })
        .WithName("WhoAmI")
        .WithSummary("Returns the resolved context and scopes for the current request.")
        .RequireAuthorization();

        // POST is correct for a state-changing operation (sign-out clears the
        // cookie). Returns 204 — frontend redirects to /login itself.
        group.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        })
        .WithName("Logout")
        .WithSummary("Signs the current user out. Clears the auth cookie.")
        .Produces(StatusCodes.Status204NoContent);

        return routes;
    }
}
