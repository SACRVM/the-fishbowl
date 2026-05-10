using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Api.Endpoints;

// Notification channel management for the logged-in user. Cookie-only —
// Bearer tokens are scoped to data, not account-level wiring, so they get
// 403 on these routes (mirrors /api/v1/export/db).
//
// `/discord/link` mints a one-shot redemption code. The user types it into
// the Fishbowl Discord bot's `/link` slash command, which redeems via
// IDiscordLinkRepository → user_mappings + notification_channels rows. We
// don't expose the redeem endpoint over HTTP — the bot is the only legitimate
// caller and lives in-process.
public static class NotificationsApi
{
    public static IEndpointRouteBuilder MapNotificationsApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/account/notifications");

        group.MapGet("/", async (
            ClaimsPrincipal user,
            INotificationChannelRepository channels,
            CancellationToken ct) =>
        {
            if (!IsCookieAuth(user, out var userId))
                return Results.Forbid();

            var rows = await channels.ListForUserAsync(userId, ct);
            // Project to a thin wire shape — the platform-side `channel_id`
            // is sensitive (DM channel snowflake) and there's no UI need
            // to round-trip it. Just confirm the link exists.
            var view = rows.Select(c => new
            {
                type = c.ChannelType,
                enabled = c.Enabled,
                linkedAt = c.CreatedAt,
            });
            return Results.Ok(view);
        })
        .WithName("ListNotificationChannels")
        .WithSummary("List notification channels linked to the current user.")
        .RequireAuthorization();

        group.MapGet("/discord/status", async (
            ClaimsPrincipal user,
            ISystemRepository system,
            INotificationChannelRepository channels,
            CancellationToken ct) =>
        {
            if (!IsCookieAuth(user, out var userId))
                return Results.Forbid();

            var token = await system.GetConfigAsync("Discord:BotToken", ct);
            var enabled = !string.IsNullOrWhiteSpace(token)
                          && !string.Equals(token, "placeholder", StringComparison.OrdinalIgnoreCase);

            var existing = await channels.GetAsync(userId, "discord", ct);

            return Results.Ok(new
            {
                enabled,
                linked = existing is not null,
                channelEnabled = existing?.Enabled ?? false,
            });
        })
        .WithName("GetDiscordNotificationStatus")
        .WithSummary("Returns whether Discord is configured on this Fishbowl and whether the current user has linked.")
        .RequireAuthorization();

        group.MapPost("/discord/link", async (
            ClaimsPrincipal user,
            ISystemRepository system,
            IDiscordLinkRepository links,
            CancellationToken ct) =>
        {
            if (!IsCookieAuth(user, out var userId))
                return Results.Forbid();

            var token = await system.GetConfigAsync("Discord:BotToken", ct);
            if (string.IsNullOrWhiteSpace(token)
                || string.Equals(token, "placeholder", StringComparison.OrdinalIgnoreCase))
            {
                // No bot token configured — minting a code would be a dead
                // button (the user has nowhere to type it). Tell the UI.
                return Results.BadRequest(new
                {
                    error = "discord_not_configured",
                    message = "Discord integration is not configured on this Fishbowl host."
                });
            }

            var (code, expiresAt) = await links.CreateCodeAsync(userId, ct);
            return Results.Ok(new
            {
                code,
                expiresAt = expiresAt.ToString("o"),
                instructions = "DM the Fishbowl Discord bot and run `/link " + code + "` within 10 minutes.",
            });
        })
        .WithName("CreateDiscordLinkCode")
        .WithSummary("Mints a one-shot Discord link code for the current user.")
        .RequireAuthorization();

        return routes;
    }

    // Bearer tokens are bound to data scopes, not the account-level surface.
    // We use the auth scheme name to decide — same shape as ExportApi.cs.
    private static bool IsCookieAuth(ClaimsPrincipal user, out string userId)
    {
        userId = user.FindFirst(McpContextClaims.UserId)?.Value ?? string.Empty;
        if (string.IsNullOrEmpty(userId)) return false;
        return user.Identity?.AuthenticationType != McpContextClaims.BearerScheme;
    }
}
