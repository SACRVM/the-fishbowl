using Fishbowl.Core.Auth;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Fishbowl.Api.Accounts;

// The request gate for account state, right after authentication. It reads
// the account's state from system.db on every authenticated request, so an
// approval, a block or a disable takes effect on the very next request —
// no stale cookie claim to wait out.
//
//   pending            → only the waiting page, GET /api/v1/me and sign-out;
//                        every other /api and /mcp call is 403 {error:"pending"},
//                        every other page redirects to /pending
//   disabled / blocked → the cookie is dropped; /api and /mcp get 403
//                        {error:"<state>"}, pages go to /login with the reason
//   active, or no row  → untouched (tests and tooling use unregistered ids)
public sealed class AccountStateMiddleware
{
    public const string PendingPath = "/pending";

    private readonly RequestDelegate _next;

    public AccountStateMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ISystemRepository system)
    {
        var userId = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst(McpContextClaims.UserId)?.Value
            : null;
        if (string.IsNullOrEmpty(userId))
        {
            await _next(context);
            return;
        }

        var user = await system.GetUserAsync(userId, context.RequestAborted);
        var state = user?.State ?? UserStates.Active;
        if (state == UserStates.Active)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path;
        var programmatic = path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp");

        if (state == UserStates.Pending)
        {
            if (IsPendingAllowed(context.Request))
            {
                await _next(context);
                return;
            }
            if (programmatic)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "pending" }, context.RequestAborted);
                return;
            }
            context.Response.Redirect(PendingPath);
            return;
        }

        // Disabled or blocked: end the session here.
        if (context.User.Identity?.AuthenticationType != McpContextClaims.BearerScheme)
        {
            try { await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); }
            catch (InvalidOperationException) { /* no cookie scheme (test hosts) */ }
        }
        if (programmatic)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = state }, context.RequestAborted);
            return;
        }
        context.Response.Redirect("/login?authError=" + Uri.EscapeDataString(SignInRefusals.Describe(state)));
    }

    // What a pending account may reach: the waiting page, its own profile
    // (so a client can tell it is pending), sign-out, and static files the
    // waiting page needs (anything with an extension outside /api and /mcp).
    private static bool IsPendingAllowed(HttpRequest request)
    {
        var path = request.Path;
        if (path.Equals(PendingPath, StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/logout", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v1/auth/logout", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v1/me", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(request.Method)) return true;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp")) return false;
        return Path.HasExtension(path.Value);
    }
}

public static class AccountStateMiddlewareExtensions
{
    public static IApplicationBuilder UseAccountState(this IApplicationBuilder app) =>
        app.UseMiddleware<AccountStateMiddleware>();
}
