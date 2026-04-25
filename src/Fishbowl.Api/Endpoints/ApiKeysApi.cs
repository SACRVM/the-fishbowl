using System.Security.Claims;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

public static class ApiKeysApi
{
    // "ApiKey" — the scheme name used by ApiKeyAuthenticationHandler when it
    // populates Identity.AuthenticationType. Cookie principals read "Google"
    // (OAuth provenance) or whatever else was the original ticket issuer;
    // what matters here is blocking the Bearer path.
    private const string ApiKeyScheme = "ApiKey";

    // Request/response DTOs. Keeping them nested to ApiKeysApi so neither
    // leaks into Core — this is API-edge contract, not a domain type.
    public record CreateKeyRequest(string Name, string ContextType, string? ContextId, List<string> Scopes);
    public record KeyResponse(
        string Id, string Name, string KeyPrefix, List<string> Scopes,
        string ContextType, string ContextId,
        DateTime CreatedAt, DateTime? LastUsedAt);
    public record CreatedKeyResponse(
        string Id, string Name, string KeyPrefix, List<string> Scopes,
        string ContextType, string ContextId,
        DateTime CreatedAt, string RawToken);

    public static IEndpointRouteBuilder MapApiKeysApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/keys").RequireAuthorization();

        // Key management is cookie-only — a token must never be able to mint
        // more tokens or revoke itself. Enforced per-handler because the
        // default authorization policy has to stay scheme-agnostic (cookie
        // tests override schemes).
        static IResult? RejectIfBearer(ClaimsPrincipal user)
            => user.Identity?.AuthenticationType == ApiKeyScheme
                ? Results.Forbid()
                : null;

        group.MapGet("/", async (ClaimsPrincipal user, IApiKeyRepository repo, CancellationToken ct) =>
        {
            if (RejectIfBearer(user) is { } reject) return reject;
            var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var keys = await repo.ListByUserAsync(userId, ct);
            return Results.Ok(keys.Select(k => new KeyResponse(
                k.Id, k.Name, k.KeyPrefix, k.Scopes,
                k.ContextType, k.ContextId,
                k.CreatedAt, k.LastUsedAt)));
        })
        .WithName("ListApiKeys")
        .WithSummary("Lists all non-revoked API keys for the current user.");

        group.MapPost("/", async (
            CreateKeyRequest body,
            ClaimsPrincipal user,
            IApiKeyRepository keys,
            ITeamRepository teams,
            CancellationToken ct) =>
        {
            if (RejectIfBearer(user) is { } reject) return reject;
            var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "name is required" });
            if (body.Scopes is null || body.Scopes.Count == 0)
                return Results.BadRequest(new { error = "at least one scope is required" });

            var unknownScopes = ScopeCatalog.UnknownScopes(body.Scopes);
            if (unknownScopes.Count > 0)
                return Results.BadRequest(new
                {
                    error = "unknown scope(s) requested",
                    unknown = unknownScopes,
                    valid = ScopeCatalog.All,
                });

            ContextRef context;
            if (body.ContextType == "user")
            {
                // Personal keys always bind to the requesting user — a user
                // must not mint a key scoped to someone else's personal space.
                context = ContextRef.User(userId);
            }
            else if (body.ContextType == "team")
            {
                if (string.IsNullOrWhiteSpace(body.ContextId))
                    return Results.BadRequest(new { error = "contextId is required for team keys" });
                var team = await teams.GetBySlugAsync(body.ContextId, ct);
                if (team is null) return Results.NotFound(new { error = "unknown team" });
                var role = await teams.GetMembershipAsync(team.Id, userId, ct);
                if (role is null) return Results.Forbid();
                // Bind to slug (URL-identifier) not id — tokens are issued
                // against the name the user asked for, and team slugs never
                // change (unique across the deployment).
                context = ContextRef.Team(team.Slug);
            }
            else
            {
                return Results.BadRequest(new { error = "contextType must be 'user' or 'team'" });
            }

            var issued = await keys.IssueAsync(userId, context, body.Name, body.Scopes, ct);
            return Results.Ok(new CreatedKeyResponse(
                issued.Record.Id,
                issued.Record.Name,
                issued.Record.KeyPrefix,
                issued.Record.Scopes,
                issued.Record.ContextType,
                issued.Record.ContextId,
                issued.Record.CreatedAt,
                issued.RawToken));
        })
        .WithName("CreateApiKey")
        .WithSummary("Mints a new API key. The raw token is returned exactly once.");

        group.MapDelete("/{id}", async (string id, ClaimsPrincipal user, IApiKeyRepository repo, CancellationToken ct) =>
        {
            if (RejectIfBearer(user) is { } reject) return reject;
            var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var ok = await repo.RevokeAsync(id, userId, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("RevokeApiKey")
        .WithSummary("Revokes the given key. Idempotent-ish: unknown IDs return 404.");

        return routes;
    }
}
