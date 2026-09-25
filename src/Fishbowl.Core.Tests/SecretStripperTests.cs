using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

// Unit-level coverage for the regex behaviour of SecretStripper. The
// integration tests in Fishbowl.Host.Tests/SecretStripInvariantTests
// confirm the wire-level invariant; these tests pin down the parser
// itself so a regex tweak can't quietly let plaintext slip through.
//
// Every delimiter-sensitive case runs as a [Theory] over BOTH forms: ":::" is
// what new content is written with, "::" is what notes written before the
// change still carry. A delimiter the stripper stops recognising is plaintext
// crossing a trust boundary, so the legacy form is pinned here deliberately
// and must stay pinned until an explicit migration retires it.
public class SecretStripperTests
{
    private const string Placeholder = "[secret content hidden]";

    [Fact]
    public void Strip_NullAndEmpty_PassThrough()
    {
        Assert.Null(SecretStripper.Strip(null));
        Assert.Equal("", SecretStripper.Strip(""));
    }

    [Fact]
    public void Strip_NoSecret_PassThrough()
    {
        const string input = "Just public content with **markdown** and ::other::tokens.";
        Assert.Equal(input, SecretStripper.Strip(input));
    }

    [Theory]
    [InlineData(":::")]
    [InlineData("::")]
    public void Strip_InlineBlock_ReplacedWithPlaceholder(string d)
    {
        var stripped = SecretStripper.Strip($"Before\n{d}secret\nopenai-api-key\n{d}end\nAfter");
        Assert.Equal($"Before\n{Placeholder}\nAfter", stripped);
    }

    [Fact]
    public void Strip_MixedDelimiterForms_BothStripped()
    {
        // A note edited across the delimiter change carries one of each.
        // Neither may survive.
        var stripped = SecretStripper.Strip(
            "A\n::secret\nold-form\n::end\nB\n:::secret\nnew-form\n:::end\nC");
        Assert.DoesNotContain("old-form", stripped);
        Assert.DoesNotContain("new-form", stripped);
        Assert.Contains("A", stripped);
        Assert.Contains("C", stripped);
    }

    [Theory]
    [InlineData(":::")]
    [InlineData("::")]
    public void Strip_CaseInsensitive_StillMatches(string d)
    {
        var stripped = SecretStripper.Strip($"{d}SECRET\nshhh\n{d}END");
        Assert.Equal(Placeholder, stripped);
    }

    [Theory]
    [InlineData(":::")]
    [InlineData("::")]
    public void Strip_EmptyBody_StillStripped(string d)
    {
        // Encrypted payload with a metadata-only block still gets stripped
        // — the markers themselves leak count, so they must go.
        var stripped = SecretStripper.Strip($"{d}secret\n\n{d}end");
        Assert.Equal(Placeholder, stripped);
    }

    [Theory]
    [InlineData(":::")]
    [InlineData("::")]
    public void Strip_MultipleBlocksInOneNote_AllStripped(string d)
    {
        var input = $"A\n{d}secret\nfirst\n{d}end\nB\n{d}secret\nsecond\n{d}end\nC";
        var stripped = SecretStripper.Strip(input);
        Assert.DoesNotContain("first", stripped);
        Assert.DoesNotContain("second", stripped);
        Assert.Contains("A", stripped);
        Assert.Contains("B", stripped);
        Assert.Contains("C", stripped);
        // Both blocks should have been replaced — count placeholders.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            stripped!, System.Text.RegularExpressions.Regex.Escape(Placeholder));
        Assert.Equal(2, matches.Count);
    }

    [Theory]
    [InlineData(":::")]
    [InlineData("::")]
    public void Strip_NoTrailingNewline_StillStripped(string d)
    {
        // The regex must not require trailing whitespace after the closer.
        var stripped = SecretStripper.Strip($"{d}secret\nbody\n{d}end");
        Assert.Equal(Placeholder, stripped);
    }

    [Theory]
    [InlineData(":::")]
    [InlineData("::")]
    public void Strip_MarkerForm_Replaced(string d)
    {
        var stripped = SecretStripper.Strip(
            $"Pre\n{d}secret#0{d}end\nMid\n{d}secret#42{d}end\nPost");
        Assert.DoesNotContain("secret#", stripped);
        Assert.Contains("Pre", stripped);
        Assert.Contains("Mid", stripped);
        Assert.Contains("Post", stripped);
    }

    [Theory]
    [InlineData(":::")]
    [InlineData("::")]
    public void Strip_MarkerForm_CaseInsensitive(string d)
    {
        Assert.Equal(Placeholder, SecretStripper.Strip($"{d}SECRET#0{d}END"));
    }

    [Theory]
    [InlineData(":::")]
    [InlineData("::")]
    public void Strip_MarkerForm_RequiresDigits(string d)
    {
        // The marker form is `:::secret#<int>:::end`. Anything else (a user
        // who literally wrote that text) is *not* a marker — leave alone.
        var input = $"{d}secret#abc{d}end";
        Assert.Equal(input, SecretStripper.Strip(input));
    }

    [Fact]
    public void StripNote_CopiesShallow_NullsContentSecret()
    {
        var original = new Note
        {
            Id = "01HXX",
            Title = "x",
            Content = "Public\n:::secret\nshhh\n:::end",
            ContentSecret = new byte[] { 1, 2, 3 },
            Tags = new List<string> { "a" },
        };

        var stripped = SecretStripper.StripNote(original);

        // Original is untouched.
        Assert.NotNull(original.ContentSecret);
        Assert.Contains("shhh", original.Content!);

        // Copy is sanitised.
        Assert.Null(stripped.ContentSecret);
        Assert.DoesNotContain("shhh", stripped.Content!);
        Assert.Equal("01HXX", stripped.Id);
    }

    [Fact]
    public void StripNote_TagsListIsACopy_NotSameInstance()
    {
        var original = new Note { Title = "x", Tags = new List<string> { "a", "b" } };
        var stripped = SecretStripper.StripNote(original);

        Assert.Equal(original.Tags, stripped.Tags);
        Assert.NotSame(original.Tags, stripped.Tags);
    }
}
