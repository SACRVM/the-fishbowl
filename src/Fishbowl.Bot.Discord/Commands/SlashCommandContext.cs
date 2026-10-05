namespace Fishbowl.Bot.Discord.Commands;

// Plain-data view of a Discord slash-command invocation. Decouples handler
// logic from Discord.Net's SocketSlashCommand so handlers can be tested
// without a live gateway connection.
//
// DiscordChannelId is the channel the command ran in. When that isn't the
// bot's own DM with the user (a DM with a friend), OpenBotDmAsync opens —
// or finds — the bot's DM and returns its id; null means the command ran in
// the bot's DM already. Only /link needs it: that's where reminders go.
public sealed record SlashCommandContext(
    string DiscordUserId,
    string DiscordUsername,
    string DiscordChannelId,
    IReadOnlyDictionary<string, string?> Options,
    Func<CancellationToken, Task<string>>? OpenBotDmAsync = null)
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
