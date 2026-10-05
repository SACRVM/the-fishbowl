using Fishbowl.Bot.Discord.Commands;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Bot.Discord;

// Discord-side `provider` value for user_mappings. Every Discord-bot lookup
// of "who is this Fishbowl user?" goes through this constant — keeps the
// auth-mapping wire format consistent with Google's "google" provider key
// in Program.cs.
public static class DiscordProvider
{
    public const string Name = "discord";
}

// Thin wrapper over user_mappings keyed on Discord. Lives here so handlers
// don't each re-implement the same lookup, and so a future Telegram bot can
// follow the same pattern without coupling.
public class DiscordUserResolver
{
    private readonly ISystemRepository _system;

    public DiscordUserResolver(ISystemRepository system)
    {
        _system = system;
    }

    // The raw mapping, whatever the account's state — for the language of a
    // reply and for /unlink. Commands that act for the user go through
    // ResolveActiveAsync.
    public Task<string?> ResolveAsync(string discordUserId, CancellationToken ct)
        => _system.GetUserIdByMappingAsync(DiscordProvider.Name, discordUserId, ct);

    // The Fishbowl user the bot may act for, or the reply that says why not:
    // not linked, or linked to an account that isn't active. A disabled or
    // blocked account keeps its mapping (it can be enabled again), so the
    // state is read on every command — the bot's twin of
    // AccountStateMiddleware, which does the same per web request.
    public async Task<(BotUser? User, SlashCommandReply? Refusal)> ResolveActiveAsync(
        string discordUserId, CancellationToken ct)
    {
        var userId = await ResolveAsync(discordUserId, ct);
        if (string.IsNullOrEmpty(userId))
            return (null, Replies.NotLinked);
        var user = await _system.GetUserAsync(userId, ct);
        if (user is null || user.State != UserStates.Active)
            return (null, Replies.Say(user?.Language, "bot.inactive"));
        return (new BotUser(userId, user.Language), null);
    }

    // The linked user's UI language (users.language) for the bot's replies —
    // null for automatic, which a chat reads as English (ChatText).
    public async Task<string?> LanguageAsync(string? userId, CancellationToken ct)
        => string.IsNullOrEmpty(userId) ? null : (await _system.GetUserAsync(userId, ct))?.Language;

    // Language for a Discord account: its linked user's, else null.
    public async Task<string?> LanguageOfAsync(string discordUserId, CancellationToken ct)
        => await LanguageAsync(await ResolveAsync(discordUserId, ct), ct);
}

// A linked, active Fishbowl user and the language the bot answers them in.
public sealed record BotUser(string UserId, string? Language);
