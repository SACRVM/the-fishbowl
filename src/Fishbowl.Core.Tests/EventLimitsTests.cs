using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

public class EventLimitsTests
{
    [Fact]
    public void Validate_NormalEvent_ReturnsNull()
    {
        var e = new Event
        {
            Title = "team standup",
            Description = "daily 09:30 sync",
            StartAt = DateTime.UtcNow,
            Location = "office",
            RRule = "FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR",
        };
        Assert.Null(EventLimits.Validate(e));
    }

    [Fact]
    public void Validate_OversizedTitle_ReturnsTitleError()
    {
        var e = new Event { Title = new string('x', EventLimits.MaxTitleLength + 1) };
        var err = EventLimits.Validate(e);
        Assert.NotNull(err);
        Assert.Equal("event", err!.Resource);
        Assert.Equal("title", err.Field);
    }

    [Fact]
    public void Validate_OversizedDescription_ReturnsDescriptionError()
    {
        var e = new Event
        {
            Title = "ok",
            Description = new string('d', EventLimits.MaxDescriptionLength + 1),
        };
        var err = EventLimits.Validate(e);
        Assert.NotNull(err);
        Assert.Equal("description", err!.Field);
    }

    [Fact]
    public void Validate_OversizedRRule_ReturnsRRuleError()
    {
        var e = new Event { Title = "ok", RRule = new string('r', EventLimits.MaxRRuleLength + 1) };
        var err = EventLimits.Validate(e);
        Assert.NotNull(err);
        Assert.Equal("rrule", err!.Field);
    }

    [Fact]
    public void Validate_OversizedLocation_ReturnsLocationError()
    {
        var e = new Event { Title = "ok", Location = new string('l', EventLimits.MaxLocationLength + 1) };
        var err = EventLimits.Validate(e);
        Assert.NotNull(err);
        Assert.Equal("location", err!.Field);
    }

    [Fact]
    public void Validate_OversizedExternalId_ReturnsExternalIdError()
    {
        var e = new Event
        {
            Title = "ok",
            ExternalId = new string('i', EventLimits.MaxExternalIdLength + 1),
        };
        var err = EventLimits.Validate(e);
        Assert.NotNull(err);
        Assert.Equal("externalId", err!.Field);
    }

    [Fact]
    public void Validate_NullOptionalFields_AreAllowed()
    {
        var e = new Event { Title = "minimal" };
        Assert.Null(EventLimits.Validate(e));
    }
}
