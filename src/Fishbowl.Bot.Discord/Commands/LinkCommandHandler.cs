using global::Discord;
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Bot.Discord.Commands;

// /link <code> — redeems a one-shot code minted from the web UI and binds
// (provider="discord", provider_id=<discord_user_id>) → fishbowl user_id.
// Also stores the DM channel so the future reminder dispatcher can DM the
// user without re-resolving.
public class LinkCommandHandler : ISlashCommandHandler
{
    public string Name => "link";

    private readonly IDiscordLinkRepository _links;
    private readonly ISystemRepository _system;
    private readonly INotificationChannelRepository _channels;
    private readonly ILogger<LinkCommandHandler> _logger;

    public LinkCommandHandler(
        IDiscordLinkRepository links,
        ISystemRepository system,
        INotificationChannelRepository channels,
        ILogger<LinkCommandHandler>? logger = null)
    {
        _links = links;
        _system = system;
        _channels = channels;
        _logger = logger ?? NullLogger<LinkCommandHandler>.Instance;
    }

    public SlashCommandProperties Build()
    {
        var builder = new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Connect your Discord account to your Fishbowl.")
            .AddOption("code", ApplicationCommandOptionType.String,
                "The link code shown in your Fishbowl notification settings.",
                isRequired: true);

        ApplyDmContext(builder);
        return builder.Build();
    }

    public async Task<SlashCommandReply> HandleAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var code = ctx.Get("code")?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code))
        {
            return SlashCommandReply.Plain("Please supply your link code: `/link <code>`.");
        }

        // Don't let an already-linked Discord account silently re-link to a
        // different Fishbowl user — that'd be confusing for both old and new
        // owners. The user can `/unlink` first (future) or remove from web UI.
        var existing = await _system.GetUserIdByMappingAsync(DiscordProvider.Name, ctx.DiscordUserId, ct);
        if (!string.IsNullOrEmpty(existing))
        {
            return SlashCommandReply.Plain(
                "This Discord account is already linked to a Fishbowl. " +
                "Unlink it from your Fishbowl notification settings before linking again.");
        }

        var redemption = await _links.RedeemAsync(code, ct);
        if (redemption is null)
        {
            // Don't differentiate "wrong" vs "expired" — same wording either
            // way (helps in the rare worry of someone shoulder-surfing codes).
            _logger.LogInformation("Discord link rejected for {Discord} (bad/expired code)", ctx.DiscordUserId);
            return SlashCommandReply.Plain(
                "That code didn't work. Generate a fresh one in your Fishbowl notification settings (codes expire after 10 minutes).");
        }

        await _system.CreateUserMappingAsync(redemption.UserId, DiscordProvider.Name, ctx.DiscordUserId, ct);
        await _channels.UpsertAsync(redemption.UserId, DiscordProvider.Name, ctx.DiscordChannelId, ct);

        _logger.LogInformation("Linked Discord {Discord} → Fishbowl {UserId}", ctx.DiscordUserId, redemption.UserId);

        return SlashCommandReply.Plain(
            "Linked. From here on you can `/remember`, `/search`, `/recent` — and I'll DM you reminders. " +
            "Try `/help` for the full list.");
    }

    internal static void ApplyDmContext(SlashCommandBuilder builder)
    {
        // User-installable + DM-only. Leaves Guild context off entirely, so
        // even if a user installs the app to a server we never respond there.
        builder.IntegrationTypes = new HashSet<ApplicationIntegrationType>
        {
            ApplicationIntegrationType.UserInstall,
        };
        builder.ContextTypes = new HashSet<InteractionContextType>
        {
            InteractionContextType.BotDm,
            InteractionContextType.PrivateChannel,
        };
    }
}
