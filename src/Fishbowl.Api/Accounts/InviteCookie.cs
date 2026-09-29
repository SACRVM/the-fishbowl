using Fishbowl.Core.Models;
using Microsoft.AspNetCore.Http;

namespace Fishbowl.Api.Accounts;

// Carries an invitation link's token across the sign-in: /invite/<token>
// sets it for a visitor who isn't signed in, the sign-in (Google or local)
// hands it to AccountGate, and it is cleared once someone is signed in.
public static class InviteCookie
{
    public const string Name = "fb_invite";

    public static string? Read(HttpContext context) =>
        context.Request.Cookies.TryGetValue(Name, out var token) && SpaceInvites.LooksValid(token) ? token : null;

    public static void Write(HttpContext context, string token, DateTime expiresAt)
    {
        var day = DateTime.UtcNow.AddDays(1);
        context.Response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,   // survives the round trip through Google
            Path = "/",
            Expires = expiresAt < day ? expiresAt : day,
        });
    }

    public static void Clear(HttpContext context)
    {
        if (context.Request.Cookies.ContainsKey(Name))
            context.Response.Cookies.Delete(Name, new CookieOptions { Path = "/" });
    }
}
