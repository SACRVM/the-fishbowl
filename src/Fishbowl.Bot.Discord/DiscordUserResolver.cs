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

    public Task<string?> ResolveAsync(string discordUserId, CancellationToken ct)
        => _system.GetUserIdByMappingAsync(DiscordProvider.Name, discordUserId, ct);

    // The linked user's UI language (users.language) for the bot's replies —
    // null for automatic, which a chat reads as English (ChatText).
    public async Task<string?> LanguageAsync(string? userId, CancellationToken ct)
        => string.IsNullOrEmpty(userId) ? null : (await _system.GetUserAsync(userId, ct))?.Language;

    // Language for a Discord account: its linked user's, else null.
    public async Task<string?> LanguageOfAsync(string discordUserId, CancellationToken ct)
        => await LanguageAsync(await ResolveAsync(discordUserId, ct), ct);
}
