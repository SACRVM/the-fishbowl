using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

// A tag TagName can't store is the caller's mistake: NoteLimits refuses it as
// Invalid (400) instead of TagName.Normalize throwing deep in the tag
// repository (500). The per-tag length follows TagName's rule.
public class NoteTagValidationTests
{
    [Theory]
    [InlineData("two words")]
    [InlineData("emoji-🐟")]
    [InlineData("semi;colon")]
    public void Validate_TagTagNameCantTake_IsInvalid(string tag)
    {
        var err = NoteLimits.Validate(new Note { Title = "t", Tags = new() { "ok", tag } });
        Assert.NotNull(err);
        Assert.Equal("tags", err!.Field);
        Assert.Equal(ResourceValidationKind.Invalid, err.Kind);
    }

    [Fact]
    public void Validate_TagLongerThanTagNameAllows_IsInvalid()
    {
        Assert.Equal(TagName.MaxLength, NoteLimits.MaxTagLength);
        var err = NoteLimits.Validate(new Note { Title = "t", Tags = new() { new string('x', TagName.MaxLength + 1) } });
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
    }

    [Theory]
    [InlineData("Work")]
    [InlineData("  review:Pending ")]
    [InlineData("a_b-c:d")]
    public void Validate_TagThatNormalises_IsAccepted(string tag)
        => Assert.Null(NoteLimits.Validate(new Note { Title = "t", Tags = new() { tag, new string('y', TagName.MaxLength) } }));

    [Fact]
    public void Validate_BlankTag_IsIgnored()
        => Assert.Null(NoteLimits.Validate(new Note { Title = "t", Tags = new() { "", "  " } }));
}
