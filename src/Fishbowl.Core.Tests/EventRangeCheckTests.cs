using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

public class EventRangeCheckTests
{
    private static readonly DateTime Day = new(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SixWeeks_IsFine() => Assert.Null(EventLimits.CheckRange(Day, Day.AddDays(42)));

    [Fact]
    public void Inverted_IsInvalid() => Assert.Equal("range_invalid", EventLimits.CheckRange(Day, Day.AddDays(-1))?.Code);

    [Fact]
    public void Empty_IsInvalid() => Assert.Equal("range_invalid", EventLimits.CheckRange(Day, Day)?.Code);

    [Fact]
    public void NearDateTimeLimits_IsInvalid_NotAnOverflow()
    {
        Assert.Equal("range_invalid", EventLimits.CheckRange(DateTime.MaxValue.AddDays(-3), DateTime.MaxValue)?.Code);
        Assert.Equal("range_invalid", EventLimits.CheckRange(DateTime.MinValue, DateTime.MinValue.AddDays(3))?.Code);
    }

    [Fact]
    public void LongerThanTheCap_IsTooLong()
    {
        Assert.Null(EventLimits.CheckRange(Day, Day.AddDays(EventLimits.MaxRangeDays)));
        Assert.Equal("range_too_long", EventLimits.CheckRange(Day, Day.AddDays(EventLimits.MaxRangeDays + 1))?.Code);
        Assert.Equal("range_too_long", EventLimits.CheckRange(Day, Day.AddYears(100))?.Code);
    }
}
