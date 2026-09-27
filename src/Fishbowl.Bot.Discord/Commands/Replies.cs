using Fishbowl.Core.Util;

namespace Fishbowl.Bot.Discord.Commands;

internal static class Replies
{
    // Used by every linked-only command. The user has to walk to their
    // Fishbowl notification settings to mint a code; we don't auto-DM the
    // link path because we have no way to identify the user yet — nor their
    // language, so this one is always English.
    public static readonly SlashCommandReply NotLinked = SlashCommandReply.Plain(ChatText.Get("bot.notLinked", null));

    // A reply line from the shared chat table, in the user's language.
    public static SlashCommandReply Say(string? language, string key, params object?[] args)
        => SlashCommandReply.Plain(ChatText.Get(key, language, args));
}
