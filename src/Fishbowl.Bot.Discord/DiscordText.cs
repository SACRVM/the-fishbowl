namespace Fishbowl.Bot.Discord;

// Discord refuses messages over 2000 characters — a long digest or a list
// of long titles would never arrive (and be retried forever). Fit keeps
// whole lines while they fit and ends with "…".
public static class DiscordText
{
    public const int MaxLength = 2000;

    public static string Fit(string text)
    {
        if (text.Length <= MaxLength) return text;
        const string more = "\n…";
        var budget = MaxLength - more.Length;
        var cut = text.LastIndexOf('\n', budget);
        return (cut > 0 ? text[..cut] : text[..budget]) + more;
    }
}
