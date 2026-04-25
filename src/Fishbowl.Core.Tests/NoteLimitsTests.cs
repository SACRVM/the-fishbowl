using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

public class NoteLimitsTests
{
    [Fact]
    public void Validate_NormalNote_ReturnsNull()
    {
        var note = new Note
        {
            Title = "hello",
            Content = "a normal little body",
            Tags = new List<string> { "personal", "todo" },
        };
        Assert.Null(NoteLimits.Validate(note));
    }

    [Fact]
    public void Validate_EmptyNote_ReturnsNull()
    {
        // The repository handles empty content/tags fine; the limits gate
        // is about upper bounds, not required-field enforcement.
        Assert.Null(NoteLimits.Validate(new Note { Title = "" }));
    }

    [Fact]
    public void Validate_OversizedTitle_ReturnsTitleError()
    {
        var note = new Note { Title = new string('x', NoteLimits.MaxTitleLength + 1) };
        var err = NoteLimits.Validate(note);
        Assert.NotNull(err);
        Assert.Equal("title", err!.Field);
    }

    [Fact]
    public void Validate_TitleAtLimit_Allowed()
    {
        var note = new Note { Title = new string('x', NoteLimits.MaxTitleLength) };
        Assert.Null(NoteLimits.Validate(note));
    }

    [Fact]
    public void Validate_OversizedContent_ReturnsContentError()
    {
        var note = new Note
        {
            Title = "ok",
            Content = new string('x', NoteLimits.MaxContentLength + 1),
        };
        var err = NoteLimits.Validate(note);
        Assert.NotNull(err);
        Assert.Equal("content", err!.Field);
    }

    [Fact]
    public void Validate_OversizedContentSecret_ReturnsContentSecretError()
    {
        var note = new Note
        {
            Title = "ok",
            ContentSecret = new byte[NoteLimits.MaxContentSecretBytes + 1],
        };
        var err = NoteLimits.Validate(note);
        Assert.NotNull(err);
        Assert.Equal("contentSecret", err!.Field);
    }

    [Fact]
    public void Validate_TooManyTags_ReturnsTagsError()
    {
        var note = new Note
        {
            Title = "ok",
            Tags = Enumerable.Range(0, NoteLimits.MaxTagCount + 1)
                .Select(i => $"t{i}")
                .ToList(),
        };
        var err = NoteLimits.Validate(note);
        Assert.NotNull(err);
        Assert.Equal("tags", err!.Field);
    }

    [Fact]
    public void Validate_OversizedTagName_ReturnsTagsError()
    {
        var note = new Note
        {
            Title = "ok",
            Tags = new List<string> { "ok", new string('x', NoteLimits.MaxTagLength + 1) },
        };
        var err = NoteLimits.Validate(note);
        Assert.NotNull(err);
        Assert.Equal("tags", err!.Field);
    }

    [Fact]
    public void ResourceValidationException_CarriesStructuredError()
    {
        var error = new ResourceValidationError("note", "content", "exceeds X");
        var ex = new ResourceValidationException(error);
        Assert.Same(error, ex.Error);
        Assert.Contains("content", ex.Message);
        Assert.Contains("exceeds X", ex.Message);
    }
}
