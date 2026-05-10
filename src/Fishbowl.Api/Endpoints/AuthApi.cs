using System.Security.Claims;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// Local username/password login. Sibling to Google OAuth — both end up at the
// same cookie principal shape (NameIdentifier + fishbowl_user_id claims) so
// downstream authorisation doesn't care how the user signed in.
//
// Self-hosted on a LAN box, internet-free: Google OAuth needs internet for
// every login. Local auth gives the operator a way to use Fishbowl with no
// outbound network at all.
//
// Admin-reset path: when a user's `must_change_password` is set, /login
// verifies the (temp) password but does NOT issue a cookie. The browser
// receives `{ mustChangePassword: true }` and the login page swaps to a
// change-password form; /change-password verifies the temp + writes the
// chosen password + clears the flag + signs them in.
public static class AuthApi
{
    public static IEndpointRouteBuilder MapAuthApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/auth");

        group.MapPost("/login", async (
            LoginRequest request,
            HttpContext context,
            ISystemRepository system,
            IPasswordHasher hasher,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request?.Username) || string.IsNullOrEmpty(request.Password))
                return Results.BadRequest(new { error = "Username and password are required." });

            var user = await system.GetUserByLocalUsernameAsync(request.Username, ct);
            if (!VerifyOrSpendDummyTime(hasher, user, request.Password))
                return Results.Unauthorized();

            // Force-rotate path: temp password is correct, but the user must
            // pick their own before we issue a session cookie. Returning 200
            // (not 401) so the page can react meaningfully; we still don't
            // call SignInAsync.
            if (user!.MustChangePassword)
            {
                return Results.Ok(new
                {
                    mustChangePassword = true,
                    username = request.Username.Trim().ToLowerInvariant(),
                });
            }

            await SignInAsync(context, user);
            return Results.NoContent();
        })
        .WithName("LocalLogin")
        .WithSummary("Username/password sign-in. Issues the same cookie as Google OAuth.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .AllowAnonymous();

        // Two ways to land here:
        //   1. From the must-change branch above — user supplies the temp
        //      password as `currentPassword`. Verify, rotate, sign in.
        //   2. From a logged-in user changing their own password via a
        //      future Settings UI. Same shape — they still re-enter the
        //      old password as a confirmation step.
        // Either way, no admin path: an admin-reset always loops back through
        // the must-change flag, never directly through this endpoint.
        group.MapPost("/change-password", async (
            ChangePasswordRequest request,
            HttpContext context,
            ISystemRepository system,
            IPasswordHasher hasher,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request?.Username)
                || string.IsNullOrEmpty(request.CurrentPassword)
                || string.IsNullOrEmpty(request.NewPassword))
            {
                return Results.BadRequest(new { error = "Username, current password, and new password are all required." });
            }

            if (request.NewPassword.Length < 12)
                return Results.BadRequest(new { error = "New password must be at least 12 characters." });

            if (string.Equals(request.NewPassword, request.CurrentPassword, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "New password must differ from the current password." });

            var user = await system.GetUserByLocalUsernameAsync(request.Username, ct);
            if (!VerifyOrSpendDummyTime(hasher, user, request.CurrentPassword))
                return Results.Unauthorized();

            var fresh = hasher.Hash(request.NewPassword);
            await system.SetPasswordAsync(user!.Id, fresh.Hash, fresh.Salt, mustChange: false, ct);

            await SignInAsync(context, user);
            return Results.NoContent();
        })
        .WithName("ChangePassword")
        .WithSummary("Verify current password, write a new one, sign the user in.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .AllowAnonymous();

        return routes;
    }

    // Returns true iff `user` is non-null with valid hash/salt AND `password`
    // verifies. Else runs the hasher against a dummy hash to spend roughly the
    // same wall time, so /login can't be used as an existence oracle.
    private static bool VerifyOrSpendDummyTime(IPasswordHasher hasher, User? user, string password)
    {
        if (user is null || string.IsNullOrEmpty(user.PasswordHash) || string.IsNullOrEmpty(user.PasswordSalt))
        {
            hasher.Verify(password, "AAAA", "AAAA");
            return false;
        }
        return hasher.Verify(password, user.PasswordHash, user.PasswordSalt);
    }

    private static Task SignInAsync(HttpContext context, User user)
    {
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id));
        identity.AddClaim(new Claim("fishbowl_user_id", user.Id));
        if (!string.IsNullOrEmpty(user.Name))
            identity.AddClaim(new Claim(ClaimTypes.Name, user.Name));
        if (!string.IsNullOrEmpty(user.Email))
            identity.AddClaim(new Claim(ClaimTypes.Email, user.Email));

        return context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }
}

public sealed record LoginRequest(string Username, string Password);
public sealed record ChangePasswordRequest(string Username, string CurrentPassword, string NewPassword);
