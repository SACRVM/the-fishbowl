namespace Fishbowl.Bot.Discord.Commands;

// Plain-data view of a Discord slash-command invocation. Decouples handler
// logic from Discord.Net's SocketSlashCommand so handlers can be tested
// without a live gateway connection.
public sealed record SlashCommandContext(
    string DiscordUserId,
    string DiscordUsername,
    string DiscordChannelId,
    IReadOnlyDictionary<string, string?> Options)
{
    public string? Get(string optionName)
        => Options.TryGetValue(optionName, out var value) ? value : null;
}

// Result of a slash-command handler. `Ephemeral` hides the reply from anyone
// but the invoker — defaults true for the bot since DMs are 1:1 anyway, but
// staying explicit keeps the contract honest if the bot ever ends up in a
// shared context.
public sealed record SlashCommandReply(string Message, bool Ephemeral = true)
{
    public static SlashCommandReply Plain(string text) => new(text);
}
