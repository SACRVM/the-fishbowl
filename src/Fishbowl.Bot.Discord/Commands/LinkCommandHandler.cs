using global::Discord;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
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
            .WithDescription(ChatText.Get("cmd.link", null))
            .WithDescriptionLocalizations(ChatText.Localizations("cmd.link"))
            .AddOption("code", ApplicationCommandOptionType.String,
                ChatText.Get("cmd.link.code", null),
                isRequired: true,
                descriptionLocalizations: ChatText.Localizations("cmd.link.code"));

        ApplyDmContext(builder);
        return builder.Build();
    }

    public async Task<SlashCommandReply> HandleAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var code = ctx.Get("code")?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code))
        {
            return Replies.Say(null, "link.missingCode");
        }

        // Don't let an already-linked Discord account silently re-link to a
        // different Fishbowl user — that'd be confusing for both old and new
        // owners. The user can `/unlink` first (future) or remove from web UI.
        var existing = await _system.GetUserIdByMappingAsync(DiscordProvider.Name, ctx.DiscordUserId, ct);
        if (!string.IsNullOrEmpty(existing))
        {
            return Replies.Say(await LanguageAsync(existing, ct), "link.already");
        }

        var redemption = await _links.RedeemAsync(code, ct);
        if (redemption is null)
        {
            // Don't differentiate "wrong" vs "expired" — same wording either
            // way (helps in the rare worry of someone shoulder-surfing codes).
            _logger.LogInformation("Discord link rejected for {Discord} (bad/expired code)", ctx.DiscordUserId);
            return Replies.Say(null, "link.badCode");
        }

        await _system.CreateUserMappingAsync(redemption.UserId, DiscordProvider.Name, ctx.DiscordUserId, ct);
        await _channels.UpsertAsync(redemption.UserId, DiscordProvider.Name, ctx.DiscordChannelId, ct);

        _logger.LogInformation("Linked Discord {Discord} → Fishbowl {UserId}", ctx.DiscordUserId, redemption.UserId);

        // Linked: from here on the bot speaks the user's language.
        return Replies.Say(await LanguageAsync(redemption.UserId, ct), "link.done");
    }

    private async Task<string?> LanguageAsync(string userId, CancellationToken ct)
        => (await _system.GetUserAsync(userId, ct))?.Language;

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
