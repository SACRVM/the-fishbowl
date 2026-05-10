namespace Fishbowl.Bot.Discord.Commands;

internal static class Replies
{
    // Used by every linked-only command. The user has to walk to their
    // Fishbowl notification settings to mint a code; we don't auto-DM the
    // link path because we have no way to identify the user yet.
    public static readonly SlashCommandReply NotLinked = SlashCommandReply.Plain(
        "I don't recognise this Discord account yet. " +
        "Open your Fishbowl notification settings to generate a link code, then run `/link <code>` here.");
}
