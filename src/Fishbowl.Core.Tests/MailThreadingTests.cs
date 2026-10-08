using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

public class MailThreadingTests
{
    [Theory]
    [InlineData("Re: Hello", "hello")]
    [InlineData("AW: Hello", "hello")]
    [InlineData("Fwd: Hello", "hello")]
    [InlineData("WG: Hello", "hello")]
    [InlineData("SV: Hello", "hello")]
    [InlineData("Re: AW: Fwd: Hello", "hello")]
    [InlineData("RE[2]: Hello", "hello")]
    [InlineData("Re(3): WG:   Big   News ", "big news")]
    [InlineData("  Plain   Subject  ", "plain subject")]
    public void SubjectKey_StripsPrefixesFoldsSpacesLowerCases(string subject, string expected)
        => Assert.Equal(expected, MailThreading.SubjectKey(subject));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Re:")]
    [InlineData("Re: AW:  ")]
    public void SubjectKey_EmptyIsNull(string? subject)
        => Assert.Null(MailThreading.SubjectKey(subject));

    [Theory]
    [InlineData("Re: x", true)]
    [InlineData("AW: x", true)]
    [InlineData("Fwd: x", true)]
    [InlineData("Re[2]: x", true)]
    [InlineData("Regarding x", false)]
    [InlineData("x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsReplySubject_DetectsPrefix(string? subject, bool expected)
        => Assert.Equal(expected, MailThreading.IsReplySubject(subject));

    [Fact]
    public void MessageKey_UsesCleanedMessageId()
        => Assert.Equal("abc@host", MailThreading.MessageKey(" <abc@host> ", "a@b.c", DateTimeOffset.UtcNow, "s", 1));

    [Fact]
    public void MessageKey_WithoutId_IsStableHash()
    {
        var date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var a = MailThreading.MessageKey(null, "A@B.c", date, "subj", 10);
        var b = MailThreading.MessageKey("  ", "a@b.c", date, "subj", 10);
        var c = MailThreading.MessageKey(null, "a@b.c", date, "other", 10);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("fb-", a);
        Assert.EndsWith("@fishbowl", a);
    }

    [Fact]
    public void Parents_ReferencesThenInReplyTo_CleanedDistinctNeverSelf()
    {
        var parents = MailThreading.Parents("me@x", "<c@x>", new[] { "<a@x>", "b@x", "<a@x>", "<me@x>", " " });
        Assert.Equal(new[] { "a@x", "b@x", "c@x" }, parents);
    }

    [Fact]
    public void Parents_NoInput_IsEmpty()
        => Assert.Empty(MailThreading.Parents("k", null, null));

    [Fact]
    public void Snippet_FoldsWhitespaceAndCuts()
    {
        Assert.Equal("a b c", MailThreading.Snippet("  a \n\t b   c "));
        Assert.Null(MailThreading.Snippet("   "));
        Assert.Null(MailThreading.Snippet(null));
        var cut = MailThreading.Snippet(new string('x', 50), 10)!;
        Assert.Equal(new string('x', 10) + "…", cut);
        Assert.Equal("short", MailThreading.Snippet("short", 10));
    }
}
