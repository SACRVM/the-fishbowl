using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Tests.Repositories;

// Range reads refuse windows that are inverted, unreal or open-ended, find a
// recurring occurrence still running at `from`, and writes store instants as
// UTC whatever offset the body carried.
public class EventRangeAndUtcTests : IDisposable
{
    private readonly string _dir;
    private readonly DatabaseFactory _factory;
    private readonly EventRepository _repo;
    private static readonly ContextRef Ctx = ContextRef.User("event_range_user");

    public EventRangeAndUtcTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_event_range_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _factory = new DatabaseFactory(_dir);
        _repo = new EventRepository(_factory);
    }

    private static DateTime Utc(int y, int m, int d, int h = 0) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Range_InvertedTooLongOrAtDateTimeLimits_ThrowsArgument_NotOverflow()
    {
        var ct = TestContext.Current.CancellationToken;
        var day = Utc(2026, 10, 4);

        await Assert.ThrowsAsync<ArgumentException>(() => _repo.GetRangeAsync(Ctx, day, day.AddDays(-1), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _repo.GetRangeAsync(Ctx, day, day.AddYears(100), ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.GetRangeAsync(Ctx, DateTime.MaxValue.AddDays(-30), DateTime.MaxValue, ct));
    }

    [Fact]
    public async Task Create_WithoutStartAt_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.CreateAsync(Ctx, "u", new Event { Title = "When?", RRule = "FREQ=DAILY" }, ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.CreateAsync(Ctx, "u", new Event { Title = "When?", AllDay = true }, ct));
    }

    [Fact]
    public async Task Range_FindsTheRecurringOccurrenceStillRunningAtFrom()
    {
        var ct = TestContext.Current.CancellationToken;
        // Every night 22:00–02:00 UTC.
        var id = await _repo.CreateAsync(Ctx, "u", new Event
        {
            Title = "Night shift",
            StartAt = Utc(2026, 10, 1, 22),
            EndAt = Utc(2026, 10, 2, 2),
            RRule = "FREQ=DAILY",
        }, ct);

        var day = (await _repo.GetRangeAsync(Ctx, Utc(2026, 10, 5), Utc(2026, 10, 6), ct)).ToList();

        // The one from the night before (still running at 00:00) and tonight's.
        Assert.Equal(new[] { Utc(2026, 10, 4, 22), Utc(2026, 10, 5, 22) }, day.Select(e => e.StartAt));
        Assert.All(day, e => Assert.Equal(id, e.Id));

        // One that ended exactly at `from` doesn't belong to the window.
        var fromTwo = (await _repo.GetRangeAsync(Ctx, Utc(2026, 10, 5, 2), Utc(2026, 10, 5, 3), ct)).ToList();
        Assert.Empty(fromTwo);
    }

    [Fact]
    public async Task Create_WithAnOffset_StoresUtc()
    {
        var ct = TestContext.Current.CancellationToken;
        // What System.Text.Json makes of "2026-10-04T10:00:00+02:00": a Local time.
        var local = new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.FromHours(2)).LocalDateTime;
        var id = await _repo.CreateAsync(Ctx, "u", new Event
        {
            Title = "Offset",
            StartAt = local,
            EndAt = local.AddHours(1),
        }, ct);

        using (var db = _factory.CreateContextConnection(Ctx))
        {
            var stored = db.QuerySingle<(string Start, string End)>(
                "SELECT start_at, end_at FROM events WHERE id = @id", new { id });
            Assert.Equal("2026-10-04T08:00:00.0000000Z", stored.Start);
            Assert.Equal("2026-10-04T09:00:00.0000000Z", stored.End);
        }

        var hit = Assert.Single(await _repo.GetRangeAsync(Ctx, Utc(2026, 10, 4, 7), Utc(2026, 10, 4, 9), ct));
        Assert.Equal(Utc(2026, 10, 4, 8), hit.StartAt);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
