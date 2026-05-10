namespace Fishbowl.Bot.Discord;

// Bound from system.db's Discord:BotToken via ConfigurationCache. Empty token
// means "Discord disabled" — the hosted service no-ops with a warning so the
// rest of the host boots normally on a fresh install.
public class DiscordBotOptions
{
    public const string SectionName = "Discord";

    public string Token { get; set; } = string.Empty;
}
