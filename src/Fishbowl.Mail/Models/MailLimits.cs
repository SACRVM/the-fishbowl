namespace Fishbowl.Mail.Models;

public sealed record MailLimits
{
    /// <summary>Maximum characters of message text returned by a get call.</summary>
    public int MaxBodyChars { get; init; } = 20_000;

    /// <summary>Default number of summaries returned by a search.</summary>
    public int DefaultSearchLimit { get; init; } = 20;

    /// <summary>Hard cap on summaries per search.</summary>
    public int MaxSearchLimit { get; init; } = 200;
}
