using global::Discord;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Bot.Discord.Commands;

// /unlink — inverse of /link. Drops the (provider=discord, provider_id=<this
// discord user>) mapping and removes the cached DM channel so reminders stop
// going to a Discord account the user no longer wants connected. Idempotent
// from the user's perspective: invoking /unlink on an unlinked account replies
// politely instead of throwing. We never delete the underlying Fishbowl user
// here — that's a deliberate, terminal operation that lives elsewhere.
public class UnlinkCommandHandler : ISlashCommandHandler
{
    public string Name => "unlink";

    private readonly ISystemRepository _system;
    private readonly INotificationChannelRepository _channels;
    private readonly ILogger<UnlinkCommandHandler> _logger;

    public UnlinkCommandHandler(
        ISystemRepository system,
        INotificationChannelRepository channels,
        ILogger<UnlinkCommandHandler>? logger = null)
    {
        _system = system;
        _channels = channels;
        _logger = logger ?? NullLogger<UnlinkCommandHandler>.Instance;
    }

    public SlashCommandProperties Build()
    {
        var builder = new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription(ChatText.Get("cmd.unlink", null))
            .WithDescriptionLocalizations(ChatText.Localizations("cmd.unlink"));
        LinkCommandHandler.ApplyDmContext(builder);
        return builder.Build();
    }

    public async Task<SlashCommandReply> HandleAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var userId = await _system.GetUserIdByMappingAsync(DiscordProvider.Name, ctx.DiscordUserId, ct);
        if (string.IsNullOrEmpty(userId))
        {
            return Replies.Say(null, "unlink.notLinked");
        }

        // Order matters mildly: delete the mapping first so any concurrent
        // SendAsync racing this command sees the channel disappear before the
        // identity does (the channel-only state is a strictly less surprising
        // failure mode than a stranded mapping with no DM path).
        // Asked before the mapping goes: the goodbye is in their language.
        var lang = (await _system.GetUserAsync(userId, ct))?.Language;
        await _system.DeleteUserMappingAsync(DiscordProvider.Name, ctx.DiscordUserId, ct);
        await _channels.RemoveAsync(userId, DiscordProvider.Name, ct);

        _logger.LogInformation("Unlinked Discord {Discord} from Fishbowl {UserId}", ctx.DiscordUserId, userId);

        return Replies.Say(lang, "unlink.done");
    }
}
