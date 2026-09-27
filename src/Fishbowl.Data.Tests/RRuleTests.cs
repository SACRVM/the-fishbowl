using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Data.Tests;

// RRule is the shared recurrence subset behind reminder expansion and
// calendar-range reads. Parse tests pin the supported grammar (and that
// everything else is rejected, so callers degrade gracefully); expansion
// tests pin windowing, COUNT/UNTIL semantics, and the calendar edge cases
// (short months, leap years).
public class RRuleTests
{
    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0)
        => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    // ── Parsing ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FREQ=DAILY", RRuleFreq.Daily, 1)]
    [InlineData("RRULE:FREQ=WEEKLY", RRuleFreq.Weekly, 1)]
    [InlineData("freq=monthly;interval=3", RRuleFreq.Monthly, 3)]
    [InlineData("FREQ=YEARLY;INTERVAL=2", RRuleFreq.Yearly, 2)]
    public void TryParse_AcceptsSupportedSubset(string text, RRuleFreq freq, int interval)
    {
        Assert.True(RRule.TryParse(text, out var spec));
        Assert.Equal(freq, spec.Freq);
        Assert.Equal(interval, spec.Interval);
    }

    [Fact]
    public void TryParse_ParsesByDayCountUntil()
    {
        Assert.True(RRule.TryParse(
            "FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=10;UNTIL=20270101T000000Z", out var spec));
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }, spec.ByDay);
        Assert.Equal(10, spec.Count);
        Assert.Equal(Utc(2027, 1, 1), spec.Until);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FREQ=HOURLY")]              // sub-daily out of subset
    [InlineData("FREQ=SECONDLY")]
    [InlineData("INTERVAL=2")]               // FREQ is mandatory
    [InlineData("FREQ=DAILY;INTERVAL=0")]
    [InlineData("FREQ=WEEKLY;BYDAY=2MO")]    // ordinal BYDAY out of subset
    [InlineData("FREQ=MONTHLY;BYDAY=MO")]    // BYDAY only supported on WEEKLY
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=15")]
    [InlineData("FREQ=DAILY;BYSETPOS=1")]
    [InlineData("FREQ=DAILY;COUNT=0")]
    [InlineData("FREQ=DAILY;UNTIL=notadate")]
    [InlineData("garbage")]
    public void TryParse_RejectsOutOfSubset(string? text)
    {
        Assert.False(RRule.TryParse(text, out _));
    }

    // ── Expansion ───────────────────────────────────────────────────────

    [Fact]
    public void Expand_Daily_YieldsOccurrencesInsideWindowOnly()
    {
        Assert.True(RRule.TryParse("FREQ=DAILY;INTERVAL=2", out var spec));
        var start = Utc(2026, 7, 1, 9, 0);

        var occs = RRule.Expand(start, spec, Utc(2026, 7, 4), Utc(2026, 7, 10)).ToList();

        Assert.Equal(new[] { Utc(2026, 7, 5, 9, 0), Utc(2026, 7, 7, 9, 0), Utc(2026, 7, 9, 9, 0) }, occs);
    }

    [Fact]
    public void Expand_IncludesDtStartWhenInWindow()
    {
        Assert.True(RRule.TryParse("FREQ=DAILY", out var spec));
        var start = Utc(2026, 7, 1, 9, 0);

        var occs = RRule.Expand(start, spec, Utc(2026, 7, 1), Utc(2026, 7, 2)).ToList();

        Assert.Equal(new[] { start }, occs);
    }

    [Fact]
    public void Expand_WeeklyByDay_LandsOnListedWeekdays()
    {
        // DTSTART Wed 2026-07-01 09:00 UTC; MO,WE weekly.
        Assert.True(RRule.TryParse("FREQ=WEEKLY;BYDAY=MO,WE", out var spec));
        var start = Utc(2026, 7, 1, 9, 0);

        var occs = RRule.Expand(start, spec, Utc(2026, 7, 1), Utc(2026, 7, 14)).ToList();

        // Wed 1st (DTSTART), Mon 6th, Wed 8th, Mon 13th.
        Assert.Equal(new[]
        {
            Utc(2026, 7, 1, 9, 0),
            Utc(2026, 7, 6, 9, 0),
            Utc(2026, 7, 8, 9, 0),
            Utc(2026, 7, 13, 9, 0),
        }, occs);
    }

    [Fact]
    public void Expand_Count_ConsumedByPreWindowOccurrences()
    {
        // 3 occurrences total (Jul 1, 2, 3); window starts at Jul 3 —
        // only the last one is left, and Jul 4+ never happens.
        Assert.True(RRule.TryParse("FREQ=DAILY;COUNT=3", out var spec));
        var start = Utc(2026, 7, 1, 9, 0);

        var occs = RRule.Expand(start, spec, Utc(2026, 7, 3), Utc(2026, 7, 30)).ToList();

        Assert.Equal(new[] { Utc(2026, 7, 3, 9, 0) }, occs);
    }

    [Fact]
    public void Expand_Until_StopsInclusive()
    {
        Assert.True(RRule.TryParse("FREQ=DAILY;UNTIL=20260703T090000Z", out var spec));
        var start = Utc(2026, 7, 1, 9, 0);

        var occs = RRule.Expand(start, spec, Utc(2026, 7, 1), Utc(2026, 7, 30)).ToList();

        Assert.Equal(new[] { Utc(2026, 7, 1, 9, 0), Utc(2026, 7, 2, 9, 0), Utc(2026, 7, 3, 9, 0) }, occs);
    }

    [Fact]
    public void Expand_Monthly_SkipsMonthsWithoutTheDay()
    {
        // 31st monthly from Jan: Feb/Apr/Jun have no 31st and are skipped.
        Assert.True(RRule.TryParse("FREQ=MONTHLY", out var spec));
        var start = Utc(2026, 1, 31, 12, 0);

        var occs = RRule.Expand(start, spec, Utc(2026, 1, 1), Utc(2026, 7, 1)).ToList();

        Assert.Equal(new[]
        {
            Utc(2026, 1, 31, 12, 0),
            Utc(2026, 3, 31, 12, 0),
            Utc(2026, 5, 31, 12, 0),
        }, occs);
    }

    [Fact]
    public void Expand_Yearly_Feb29OnlyOnLeapYears()
    {
        Assert.True(RRule.TryParse("FREQ=YEARLY", out var spec));
        var start = Utc(2024, 2, 29, 8, 0);

        var occs = RRule.Expand(start, spec, Utc(2024, 1, 1), Utc(2029, 1, 1)).ToList();

        Assert.Equal(new[] { Utc(2024, 2, 29, 8, 0), Utc(2028, 2, 29, 8, 0) }, occs);
    }

    // ── Time zones: wall-clock time holds across DST ──────────────────

    // Europe/Lisbon: WEST (UTC+1) until 2026-10-25 01:00 UTC, then WET (UTC+0).
    private static TimeZoneInfo Lisbon => RRule.ResolveZone("Europe/Lisbon")!;

    [Fact]
    public void Expand_WithZone_WeeklyKeepsLocalTimeAcrossDstEnd()
    {
        Assert.True(RRule.TryParse("FREQ=WEEKLY", out var spec));
        // Tue 2026-10-20 18:00 Lisbon (WEST) = 17:00 UTC.
        var start = Utc(2026, 10, 20, 17, 0);

        var occs = RRule.Expand(start, spec, Utc(2026, 10, 1), Utc(2026, 11, 1), Lisbon).ToList();

        // Tue 27th is after the switch: 18:00 WET = 18:00 UTC.
        Assert.Equal(new[] { Utc(2026, 10, 20, 17, 0), Utc(2026, 10, 27, 18, 0) }, occs);
        foreach (var o in occs)
            Assert.Equal(18, TimeZoneInfo.ConvertTimeFromUtc(o, Lisbon).Hour);
    }

    [Fact]
    public void Expand_WithoutZone_KeepsUtcTimeOfDay()
    {
        Assert.True(RRule.TryParse("FREQ=WEEKLY", out var spec));
        var start = Utc(2026, 10, 20, 17, 0);

        var occs = RRule.Expand(start, spec, Utc(2026, 10, 1), Utc(2026, 11, 1)).ToList();

        Assert.Equal(new[] { Utc(2026, 10, 20, 17, 0), Utc(2026, 10, 27, 17, 0) }, occs);
    }

    [Fact]
    public void Expand_WithZone_DailyInSpringGapMovesForward()
    {
        Assert.True(RRule.TryParse("FREQ=DAILY", out var spec));
        // 01:30 Lisbon (WET = UTC) daily; 2027-03-28 01:00 WET jumps to 02:00 WEST,
        // so 01:30 doesn't exist that day and lands on 02:30 WEST = 01:30 UTC.
        var start = Utc(2027, 3, 27, 1, 30);

        var occs = RRule.Expand(start, spec, Utc(2027, 3, 27), Utc(2027, 3, 30), Lisbon).ToList();

        Assert.Equal(new[] { Utc(2027, 3, 27, 1, 30), Utc(2027, 3, 28, 1, 30), Utc(2027, 3, 29, 0, 30) }, occs);
    }

    [Fact]
    public void ResolveZone_UnknownOrEmpty_IsNull()
    {
        Assert.Null(RRule.ResolveZone(null));
        Assert.Null(RRule.ResolveZone(""));
        Assert.Null(RRule.ResolveZone("Mars/Olympus_Mons"));
        Assert.NotNull(RRule.ResolveZone("Europe/Berlin"));
    }

    [Fact]
    public void Expand_WindowBeforeSeriesStart_IsEmpty()
    {
        Assert.True(RRule.TryParse("FREQ=DAILY", out var spec));
        var start = Utc(2026, 7, 10, 9, 0);

        var occs = RRule.Expand(start, spec, Utc(2026, 7, 1), Utc(2026, 7, 5)).ToList();

        Assert.Empty(occs);
    }
}
