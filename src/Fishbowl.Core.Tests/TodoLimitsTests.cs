using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

public class TodoLimitsTests
{
    [Fact]
    public void Validate_NormalTodo_ReturnsNull()
    {
        var t = new TodoItem
        {
            Title = "buy milk",
            Description = "skim, 2L",
            Source = "01HXX01ULID",
        };
        Assert.Null(TodoLimits.Validate(t));
    }

    [Fact]
    public void Validate_OversizedTitle_ReturnsTitleError()
    {
        var t = new TodoItem { Title = new string('x', TodoLimits.MaxTitleLength + 1) };
        var err = TodoLimits.Validate(t);
        Assert.NotNull(err);
        Assert.Equal("todo", err!.Resource);
        Assert.Equal("title", err.Field);
    }

    [Fact]
    public void Validate_OversizedDescription_ReturnsDescriptionError()
    {
        var t = new TodoItem
        {
            Title = "ok",
            Description = new string('d', TodoLimits.MaxDescriptionLength + 1),
        };
        var err = TodoLimits.Validate(t);
        Assert.NotNull(err);
        Assert.Equal("description", err!.Field);
    }

    [Fact]
    public void Validate_OversizedSource_ReturnsSourceError()
    {
        var t = new TodoItem
        {
            Title = "ok",
            Source = new string('s', TodoLimits.MaxSourceLength + 1),
        };
        var err = TodoLimits.Validate(t);
        Assert.NotNull(err);
        Assert.Equal("source", err!.Field);
    }

    [Fact]
    public void Validate_NullOptionalFields_AreAllowed()
    {
        var t = new TodoItem { Title = "ok", Description = null, Source = null };
        Assert.Null(TodoLimits.Validate(t));
    }
}
