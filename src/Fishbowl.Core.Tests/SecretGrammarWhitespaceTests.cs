using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

// The editor (vendored sac-md-secret.js) tests its block boundaries with
// JavaScript's \s: `^:{2,3}secret(\s|$)` and `^:{2,3}end(\s|$)`. Whatever it
// masks must be stripped here, and nothing may close earlier than it does —
// a non-breaking space typed with Option+Space after `:::secret` used to
// leave the whole block in the clear on every machine-facing path.
public class SecretGrammarWhitespaceTests
{
    private const string Placeholder = "[secret content hidden]";

    [Theory]
    [InlineData("\u00A0")]   // no-break space (Option+Space)
    [InlineData("\u2007")]   // figure space
    [InlineData("\u202F")]   // narrow no-break space
    [InlineData("\u3000")]   // ideographic space
    [InlineData("\uFEFF")]   // BOM — JS \s, not .NET \s
    [InlineData("\v")]
    [InlineData("\f")]
    public void Strip_OpenerFollowedByJsWhitespace_OpensBlock(string ws)
    {
        var content = $"intro\n:::secret{ws}AWS root\nhunter2\n:::end\noutro";
        Assert.Equal($"intro\n{Placeholder}\noutro", SecretStripper.Strip(content));
        Assert.True(SecretStripper.ContainsSecret(content));
    }

    [Fact]
    public void Strip_CloserFollowedByNoBreakSpace_Closes()
    {
        var content = ":::secret\nhunter2\n:::end\u00A0rotate yearly\nafter";
        Assert.Equal($"{Placeholder}\nafter", SecretStripper.Strip(content));
    }

    // U+0085 is in .NET's \s but not in JS's: the editor doesn't close on it,
    // so neither may the stripper — the rest stays hidden.
    [Fact]
    public void Strip_CloserFollowedByNextLine_DoesNotClose()
    {
        var content = ":::secret\nhunter2\n:::end\u0085x\nstill secret";
        Assert.Equal(Placeholder, SecretStripper.Strip(content));
    }

    // A JS /i match is ASCII for these letters; the long s (U+017F) that
    // .NET's IgnoreCase would fold to "s" is no opener in the editor either.
    [Fact]
    public void Strip_LongS_IsNoOpener()
    {
        var content = ":::\u017Fecret\nplain";
        Assert.Equal(content, SecretStripper.Strip(content));
        Assert.False(SecretStripper.ContainsSecret(content));
    }

    [Fact]
    public void Strip_LineSeparatorInsideOpenerLabel_StillOpens()
    {
        var content = ":::secret label\u2028more\nhunter2\n:::end";
        Assert.Equal(Placeholder, SecretStripper.Strip(content));
    }

    [Fact]
    public void Strip_CrLfLines_StillMatch()
    {
        var content = "a\r\n:::secret\r\nhunter2\r\n:::end\r\nb";
        Assert.Equal($"a\r\n{Placeholder}\nb", SecretStripper.Strip(content));
    }

    [Fact]
    public void StripNote_NoBreakSpaceOpener_HidesSecret()
    {
        var note = new Note { Title = "t", Content = "x\n:::secret\u00A0\nhunter2\n:::end" };
        Assert.DoesNotContain("hunter2", SecretStripper.StripNote(note).Content);
    }
}
