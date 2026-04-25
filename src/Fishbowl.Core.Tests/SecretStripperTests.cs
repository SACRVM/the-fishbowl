using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

// Unit-level coverage for the regex behaviour of SecretStripper. The
// integration tests in Fishbowl.Host.Tests/SecretStripInvariantTests
// confirm the wire-level invariant; these tests pin down the parser
// itself so a regex tweak can't quietly let plaintext slip through.
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

    [Fact]
    public void Strip_InlineBlock_ReplacedWithPlaceholder()
    {
        var stripped = SecretStripper.Strip("Before\n::secret\nopenai-api-key\n::end\nAfter");
        Assert.Equal($"Before\n{Placeholder}\nAfter", stripped);
    }

    [Fact]
    public void Strip_CaseInsensitive_StillMatches()
    {
        var stripped = SecretStripper.Strip("::SECRET\nshhh\n::END");
        Assert.Equal(Placeholder, stripped);
    }

    [Fact]
    public void Strip_EmptyBody_StillStripped()
    {
        // Encrypted payload with a metadata-only block still gets stripped
        // — the markers themselves leak count, so they must go.
        var stripped = SecretStripper.Strip("::secret\n\n::end");
        Assert.Equal(Placeholder, stripped);
    }

    [Fact]
    public void Strip_MultipleBlocksInOneNote_AllStripped()
    {
        var input = "A\n::secret\nfirst\n::end\nB\n::secret\nsecond\n::end\nC";
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

    [Fact]
    public void Strip_NoTrailingNewline_StillStripped()
    {
        // The regex must not require trailing whitespace after ::end.
        var stripped = SecretStripper.Strip("::secret\nbody\n::end");
        Assert.Equal(Placeholder, stripped);
    }

    [Fact]
    public void Strip_MarkerForm_Replaced()
    {
        var stripped = SecretStripper.Strip("Pre\n::secret#0::end\nMid\n::secret#42::end\nPost");
        Assert.DoesNotContain("::secret", stripped);
        Assert.Contains("Pre", stripped);
        Assert.Contains("Mid", stripped);
        Assert.Contains("Post", stripped);
    }

    [Fact]
    public void Strip_MarkerForm_CaseInsensitive()
    {
        Assert.Equal(Placeholder, SecretStripper.Strip("::SECRET#0::END"));
    }

    [Fact]
    public void Strip_MarkerForm_RequiresDigits()
    {
        // The marker form is `::secret#<int>::end`. Anything else (a user
        // who literally wrote that text) is *not* a marker — leave alone.
        var input = "::secret#abc::end";
        Assert.Equal(input, SecretStripper.Strip(input));
    }

    [Fact]
    public void StripNote_CopiesShallow_NullsContentSecret()
    {
        var original = new Note
        {
            Id = "01HXX",
            Title = "x",
            Content = "Public\n::secret\nshhh\n::end",
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
