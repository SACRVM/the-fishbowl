using Fishbowl.Core.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api;

public static class ScopedAuthorizationExtensions
{
    // Cookie/OAuth principals have no scope claims and represent a human
    // with full access — the check is "unless it's a Bearer token, let it
    // through". Bearer principals must carry the exact scope claim.
    // Only the ApiKey scheme is gated: anything else (the cookie, which also
    // carries a Google sign-in) passes — a new non-cookie scheme must be
    // added here deliberately.
    public static RouteHandlerBuilder RequireScope(this RouteHandlerBuilder builder, string scope)
        => builder.RequireAuthorization(policy => policy.RequireAssertion(ctx =>
        {
            var authType = ctx.User.Identity?.AuthenticationType;
            // Only Bearer principals are scope-gated. Everything else passes.
            if (authType != McpContextClaims.BearerScheme) return true;
            return ctx.User.HasClaim(McpContextClaims.Scope, scope);
        }));

    // Routes that only ever open the personal workspace (todos, events,
    // contacts, tags): a key bound to a space gets 403 — its data lives
    // under /api/v1/spaces/<slug>/…, and it must never reach its owner's
    // personal DB.
    public static RouteGroupBuilder PersonalKeysOnly(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var user = ctx.HttpContext.User;
            if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme
                && user.FindFirst(McpContextClaims.ContextType)?.Value != "user")
                return Results.Forbid();
            return await next(ctx);
        });
        return group;
    }
}
