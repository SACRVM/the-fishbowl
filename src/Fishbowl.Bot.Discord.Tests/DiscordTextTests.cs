using Fishbowl.Bot.Discord;
using Xunit;

namespace Fishbowl.Bot.Discord.Tests;

// Discord refuses messages over 2000 characters; Fit keeps whole lines.
public class DiscordTextTests
{
    [Fact]
    public void Fit_LeavesShortTextAlone()
    {
        Assert.Equal("hello\nworld", DiscordText.Fit("hello\nworld"));
    }

    [Fact]
    public void Fit_CutsAtALineBreak_UnderTheLimit()
    {
        var lines = string.Join("\n", Enumerable.Range(0, 300).Select(i => $"line {i:000} of the digest"));
        var fit = DiscordText.Fit(lines);
        Assert.True(fit.Length <= DiscordText.MaxLength);
        Assert.EndsWith("\n…", fit);
        Assert.StartsWith("line 000", fit);
        Assert.DoesNotContain("line 299", fit);
    }

    [Fact]
    public void Fit_OneHugeLine_IsCutToo()
    {
        var fit = DiscordText.Fit(new string('x', 5000));
        Assert.True(fit.Length <= DiscordText.MaxLength);
    }
}
