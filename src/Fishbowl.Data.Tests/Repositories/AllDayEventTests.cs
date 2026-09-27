using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Tests.Repositories;

// All-day events are dates (user schema v12): start_date / end_date,
// end exclusive, start_at / end_at as UTC-midnight anchors. Covers the v12
// migration of old rows, writes from old clients, range reads at month
// edges, all-day series and reminders relative to local midnight.
public class AllDayEventTests : IDisposable
{
    private readonly string _dir;
    private readonly DatabaseFactory _factory;
    private readonly EventRepository _events;
    private const string User = "allday_user";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AllDayEventTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_allday_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _factory = new DatabaseFactory(_dir);
        _events = new EventRepository(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0)
        => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    // ── writes ──

    [Fact]
    public async Task Create_WithDates_StoresDatesAndAnchors()
    {
        var id = await _events.CreateAsync(User, new Event
        {
            Title = "Holiday",
            AllDay = true,
            StartDate = "2026-10-01",
            EndDate = "2026-10-04",
            StartAt = Utc(2026, 9, 30, 15),
            TimeZone = "Asia/Tokyo",
        }, Ct);

        var ev = (await _events.GetByIdAsync(User, id, Ct))!;
        Assert.Equal("2026-10-01", ev.StartDate);
        Assert.Equal("2026-10-04", ev.EndDate);
        Assert.Equal(Utc(2026, 10, 1), TimeUtil.AsUtc(ev.StartAt));
        Assert.Equal(Utc(2026, 10, 4), TimeUtil.AsUtc(ev.EndAt!.Value));
    }

    [Theory]
    // An old client in Tokyo: local midnight of Oct 1 = Sep 30 15:00Z.
    [InlineData("Asia/Tokyo", "2026-09-30T15:00:00Z", "2026-10-01T14:59:00Z", "2026-10-01", "2026-10-02")]
    [InlineData(null, "2026-09-30T15:00:00Z", "2026-10-01T14:59:00Z", "2026-10-01", "2026-10-02")]
    // Los Angeles (UTC−7): Oct 1 00:00 local = Oct 1 07:00Z, two days to Oct 3 00:00 local.
    [InlineData(null, "2026-10-01T07:00:00Z", "2026-10-03T07:00:00Z", "2026-10-01", "2026-10-03")]
    // No end, and a zero-length end: one day.
    [InlineData(null, "2026-10-01T07:00:00Z", null, "2026-10-01", "2026-10-02")]
    [InlineData("Europe/Lisbon", "2026-09-30T23:00:00Z", "2026-09-30T23:00:00Z", "2026-10-01", "2026-10-02")]
    public async Task Create_FromStartAtOnly_DerivesTheDates(string? zone, string start, string? end, string expStart, string expEnd)
    {
        var id = await _events.CreateAsync(User, new Event
        {
            Title = "Old client",
            AllDay = true,
            TimeZone = zone,
            StartAt = DateTime.Parse(start, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            EndAt = end is null ? null : DateTime.Parse(end, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
        }, Ct);

        var ev = (await _events.GetByIdAsync(User, id, Ct))!;
        Assert.Equal(expStart, ev.StartDate);
        Assert.Equal(expEnd, ev.EndDate);
    }

    [Theory]
    [InlineData("2026-13-01", null)]
    [InlineData("01.10.2026", null)]
    [InlineData("2026-10-02", "2026-10-02")]
    [InlineData("2026-10-02", "2026-10-01")]
    public async Task Create_RejectsBadDates(string startDate, string? endDate)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _events.CreateAsync(User, new Event
        {
            Title = "Bad",
            AllDay = true,
            StartDate = startDate,
            EndDate = endDate,
            StartAt = Utc(2026, 10, 1),
        }, Ct));
    }

    [Fact]
    public async Task Update_ToTimed_ClearsTheDates()
    {
        var ev = new Event { Title = "Flip", AllDay = true, StartDate = "2026-10-01", StartAt = Utc(2026, 10, 1) };
        var id = await _events.CreateAsync(User, ev, Ct);
        ev.AllDay = false;
        ev.StartAt = Utc(2026, 10, 1, 9);
        ev.EndAt = Utc(2026, 10, 1, 10);
        Assert.True(await _events.UpdateAsync(User, ev, Ct));

        var back = (await _events.GetByIdAsync(User, id, Ct))!;
        Assert.Null(back.StartDate);
        Assert.Null(back.EndDate);
        Assert.Equal(Utc(2026, 10, 1, 9), TimeUtil.AsUtc(back.StartAt));
    }

    // ── range reads ──

    [Fact]
    public async Task Range_MatchesAllDayOnDates_InAnyCallerZone()
    {
        await _events.CreateAsync(User, new Event { Title = "Sep30", AllDay = true, StartDate = "2026-09-30", StartAt = Utc(2026, 9, 30) }, Ct);
        await _events.CreateAsync(User, new Event { Title = "Oct1", AllDay = true, StartDate = "2026-10-01", StartAt = Utc(2026, 10, 1) }, Ct);
        await _events.CreateAsync(User, new Event { Title = "Oct31-Nov2", AllDay = true, StartDate = "2026-10-31", EndDate = "2026-11-02", StartAt = Utc(2026, 10, 31) }, Ct);
        await _events.CreateAsync(User, new Event { Title = "Nov5", AllDay = true, StartDate = "2026-11-05", StartAt = Utc(2026, 11, 5) }, Ct);

        // October as Tokyo asks for it (local midnights, UTC+9) and as LA does (UTC−7).
        foreach (var (from, to) in new[]
        {
            (Utc(2026, 9, 30, 15), Utc(2026, 10, 31, 15)),
            (Utc(2026, 10, 1, 7), Utc(2026, 11, 1, 7)),
        })
        {
            var titles = (await _events.GetRangeAsync(User, from, to, Ct)).Select(e => e.Title).ToList();
            // October's days are all there; neighbours inside the slack may
            // be too (callers place by date); Nov 5 is far outside.
            Assert.Contains("Oct1", titles);
            Assert.Contains("Oct31-Nov2", titles);
            Assert.DoesNotContain("Nov5", titles);
            Assert.Single(titles, t => t == "Oct1");
        }
    }

    [Fact]
    public async Task Range_AllDaySeries_ExpandsOnDates()
    {
        // Every Sunday from Oct 4, across the late-October DST switch.
        await _events.CreateAsync(User, new Event
        {
            Title = "Sundays",
            AllDay = true,
            StartDate = "2026-10-04",
            StartAt = Utc(2026, 10, 4),
            RRule = "FREQ=WEEKLY",
            TimeZone = "Europe/Lisbon",
        }, Ct);

        var got = (await _events.GetRangeAsync(User, Utc(2026, 10, 1), Utc(2026, 11, 1), Ct))
            .Where(e => e.Title == "Sundays").Select(e => e.StartDate).ToList();
        Assert.Contains("2026-10-04", got);
        Assert.Contains("2026-10-11", got);
        Assert.Contains("2026-10-18", got);
        Assert.Contains("2026-10-25", got);
        Assert.All(got, d => Assert.Equal(DayOfWeek.Sunday, DateOnly.Parse(d!).DayOfWeek));
    }

    // ── reminders ──

    [Fact]
    public async Task Reminder_FiresRelativeToLocalMidnightOfTheDate()
    {
        // All day on Oct 10 in Tokyo, remind 60 minutes before:
        // local midnight Oct 10 = Oct 9 15:00Z, trigger Oct 9 14:00Z.
        await _events.CreateAsync(User, new Event
        {
            Title = "Tokyo day",
            AllDay = true,
            StartDate = "2026-10-10",
            StartAt = Utc(2026, 10, 10),
            TimeZone = "Asia/Tokyo",
            ReminderMinutes = 60,
        }, Ct);

        var early = await _events.ListDueRemindersAsync(ContextRef.User(User),
            Utc(2026, 10, 9, 13), Utc(2026, 10, 9, 13, 59), Utc(2026, 10, 8), Ct);
        Assert.Empty(early);

        var due = await _events.ListDueRemindersAsync(ContextRef.User(User),
            Utc(2026, 10, 9, 13, 59), Utc(2026, 10, 9, 14, 1), Utc(2026, 10, 8), Ct);
        var ev = Assert.Single(due);
        Assert.Equal(Utc(2026, 10, 9, 15), TimeUtil.AsUtc(ev.StartAt));
        Assert.Equal("2026-10-10", ev.StartDate);
    }

    [Fact]
    public async Task Reminder_WithoutZone_UsesUtcMidnight()
    {
        await _events.CreateAsync(User, new Event
        {
            Title = "UTC day",
            AllDay = true,
            StartDate = "2026-10-10",
            StartAt = Utc(2026, 10, 10),
            ReminderMinutes = 0,
        }, Ct);
        var due = await _events.ListDueRemindersAsync(ContextRef.User(User),
            Utc(2026, 10, 9, 23, 59), Utc(2026, 10, 10, 0, 1), Utc(2026, 10, 9), Ct);
        Assert.Single(due);
    }

    [Fact]
    public async Task Reminder_AllDaySeries_FiresForTheOccurrence()
    {
        await _events.CreateAsync(User, new Event
        {
            Title = "Weekly day",
            AllDay = true,
            StartDate = "2026-10-04",
            StartAt = Utc(2026, 10, 4),
            RRule = "FREQ=WEEKLY",
            TimeZone = "Europe/Lisbon",
            ReminderMinutes = 0,
        }, Ct);
        // Oct 18 in summer time (UTC+1): local midnight = Oct 17 23:00Z.
        // Nov 1 after the Oct 25 switch (UTC+0): local midnight = Nov 1 00:00Z.
        var summer = await _events.ListDueRemindersAsync(ContextRef.User(User),
            Utc(2026, 10, 17, 22, 59), Utc(2026, 10, 17, 23, 1), Utc(2026, 10, 17), Ct);
        Assert.Equal("2026-10-18", Assert.Single(summer).StartDate);
        var winter = await _events.ListDueRemindersAsync(ContextRef.User(User),
            Utc(2026, 10, 31, 23, 59), Utc(2026, 11, 1, 0, 1), Utc(2026, 10, 31), Ct);
        Assert.Equal("2026-11-01", Assert.Single(winter).StartDate);
    }

    // ── v12 migration ──

    // Writes a pre-v12 row directly (old shape: local midnight as a UTC
    // instant, no dates), rewinds the DB to v11, then lets a fresh factory
    // migrate it.
    private async Task<Event> MigrateOldRowAsync(string startAt, string? endAt, string? zone)
    {
        using (var db = _factory.CreateContextConnection(ContextRef.User(User)))
        {
            await db.ExecuteAsync(@"
                INSERT INTO events (id, title, start_at, end_at, all_day, time_zone, created_by, created_at, updated_at)
                VALUES ('old', 'Old', @startAt, @endAt, 1, @zone, 'u', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')",
                new { startAt, endAt, zone });
            await db.ExecuteAsync("PRAGMA user_version = 11");
        }
        SqliteConnection.ClearAllPools();
        var migrated = new EventRepository(new DatabaseFactory(_dir));
        return (await migrated.GetByIdAsync(User, "old", Ct))!;
    }

    [Theory]
    // With the zone it was written in: exact.
    [InlineData("2026-09-30T15:00:00.0000000Z", "2026-10-01T15:00:00.0000000Z", "Asia/Tokyo", "2026-10-01", "2026-10-02")]
    [InlineData("2026-09-30T13:00:00.0000000Z", "2026-10-01T13:00:00.0000000Z", "Pacific/Kiritimati", "2026-10-01", "2026-10-02")]
    // Without: the +12 h rule (UTC−12 … UTC+12).
    [InlineData("2026-09-30T22:00:00.0000000Z", "2026-10-01T22:00:00.0000000Z", null, "2026-10-01", "2026-10-02")]
    [InlineData("2026-10-01T07:00:00.0000000Z", "2026-10-04T06:59:00.0000000Z", null, "2026-10-01", "2026-10-04")]
    // Missing end: one day.
    [InlineData("2026-10-01T07:00:00.0000000Z", null, null, "2026-10-01", "2026-10-02")]
    public async Task Migration_V12_DerivesDates(string startAt, string? endAt, string? zone, string expStart, string expEnd)
    {
        var ev = await MigrateOldRowAsync(startAt, endAt, zone);
        Assert.Equal(expStart, ev.StartDate);
        Assert.Equal(expEnd, ev.EndDate);
        Assert.Equal(AllDayDates.Anchor(DateOnly.Parse(expStart)), TimeUtil.AsUtc(ev.StartAt));
        Assert.Equal(AllDayDates.Anchor(DateOnly.Parse(expEnd)), TimeUtil.AsUtc(ev.EndAt!.Value));
    }

    // Documented limit: without a zone, a row from UTC+14 (Kiritimati local
    // midnight Oct 1 = Sep 30 10:00Z) lands a day early.
    [Fact]
    public async Task Migration_V12_WithoutZone_Utc14_IsADayEarly()
    {
        var ev = await MigrateOldRowAsync("2026-09-30T10:00:00.0000000Z", null, null);
        Assert.Equal("2026-09-30", ev.StartDate);
    }
}
