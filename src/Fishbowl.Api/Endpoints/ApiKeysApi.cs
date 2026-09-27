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
            ISpaceRepository spaces,
            CancellationToken ct) =>
        {
            if (RejectIfBearer(user) is { } reject) return reject;
            var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(body.Name))
                return ApiErrors.BadRequest("required", "name is required", new { field = "name" });
            if (body.Scopes is null || body.Scopes.Count == 0)
                return ApiErrors.BadRequest("scopes_required", "at least one scope is required");

            var unknownScopes = ScopeCatalog.UnknownScopes(body.Scopes);
            if (unknownScopes.Count > 0)
                return ApiErrors.BadRequest("unknown_scopes", "unknown scope(s) requested", new
                {
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
            else if (body.ContextType == "space")
            {
                if (string.IsNullOrWhiteSpace(body.ContextId))
                    return ApiErrors.BadRequest("required", "contextId is required for space keys", new { field = "contextId" });
                var space = await spaces.GetBySlugAsync(body.ContextId, ct);
                if (space is null) return ApiErrors.NotFound("unknown_space", "unknown space");
                var role = await spaces.GetMembershipAsync(space.Id, userId, ct);
                if (role is null) return Results.Forbid();
                // Bind to slug (URL-identifier) not id — tokens are issued
                // against the name the user asked for, and space slugs never
                // change (unique across the deployment).
                context = ContextRef.Space(space.Slug);
            }
            else
            {
                return ApiErrors.BadRequest("invalid_value", "contextType must be 'user' or 'space'", new { field = "contextType" });
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
