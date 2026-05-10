using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Tests.Repositories;

public class EventRepositoryTests : IDisposable
{
    private readonly string _tempDbDir;
    private readonly DatabaseFactory _dbFactory;
    private readonly EventRepository _repo;
    private const string TestUserId = "event_repo_user";

    public EventRepositoryTests()
    {
        _tempDbDir = Path.Combine(Path.GetTempPath(),
            "fishbowl_event_repo_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDbDir);
        _dbFactory = new DatabaseFactory(_tempDbDir);
        _repo = new EventRepository(_dbFactory);
    }

    [Fact]
    public async Task Create_SetsMetadataAndStoresAllFields()
    {
        var evt = new Event
        {
            Title = "Team offsite",
            Description = "two days in the mountains",
            StartAt = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
            EndAt = new DateTime(2026, 6, 2, 17, 0, 0, DateTimeKind.Utc),
            AllDay = false,
            Location = "Alpine lodge",
            ReminderMinutes = 60,
            RRule = "FREQ=YEARLY",
        };

        var id = await _repo.CreateAsync(TestUserId, evt,
            TestContext.Current.CancellationToken);

        Assert.Equal(id, evt.Id);
        Assert.Equal(TestUserId, evt.CreatedBy);
        Assert.NotEqual(default, evt.CreatedAt);

        var retrieved = await _repo.GetByIdAsync(TestUserId, id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("Team offsite", retrieved!.Title);
        Assert.Equal("Alpine lodge", retrieved.Location);
        Assert.Equal(60, retrieved.ReminderMinutes);
        Assert.Equal("FREQ=YEARLY", retrieved.RRule);
        Assert.False(retrieved.AllDay);
    }

    [Fact]
    public async Task Create_RejectsEmptyTitle()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.CreateAsync(TestUserId, new Event
            {
                Title = "  ",
                StartAt = DateTime.UtcNow,
            }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_RejectsEndBeforeStart()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.CreateAsync(TestUserId, new Event
            {
                Title = "bad window",
                StartAt = start,
                EndAt = start.AddMinutes(-5),
            }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_AcceptsZeroDurationEvent()
    {
        // start == end is a valid point-in-time event (e.g. "9:00 reminder").
        // Only strictly inverted windows are rejected.
        var moment = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);
        var id = await _repo.CreateAsync(TestUserId, new Event
        {
            Title = "alarm",
            StartAt = moment,
            EndAt = moment,
        }, TestContext.Current.CancellationToken);
        Assert.NotNull(id);
    }

    [Fact]
    public async Task Update_PersistsChangedFields()
    {
        var evt = new Event
        {
            Title = "old title",
            StartAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        var id = await _repo.CreateAsync(TestUserId, evt,
            TestContext.Current.CancellationToken);

        evt.Title = "new title";
        evt.Description = "added later";
        evt.AllDay = true;
        var ok = await _repo.UpdateAsync(TestUserId, evt,
            TestContext.Current.CancellationToken);
        Assert.True(ok);

        var fresh = await _repo.GetByIdAsync(TestUserId, id,
            TestContext.Current.CancellationToken);
        Assert.Equal("new title", fresh!.Title);
        Assert.Equal("added later", fresh.Description);
        Assert.True(fresh.AllDay);
    }

    [Fact]
    public async Task Update_Returns_False_ForMissingId()
    {
        var ok = await _repo.UpdateAsync(TestUserId, new Event
        {
            Id = "01HZ_GHOST",
            Title = "ghost",
            StartAt = DateTime.UtcNow,
        }, TestContext.Current.CancellationToken);
        Assert.False(ok);
    }

    [Fact]
    public async Task Delete_RemovesRow_AndCascadesReminders()
    {
        var evt = new Event
        {
            Title = "with reminder",
            StartAt = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc),
        };
        var id = await _repo.CreateAsync(TestUserId, evt,
            TestContext.Current.CancellationToken);

        // Seed a reminder row directly — it FKs to events(id).
        using (var db = _dbFactory.CreateContextConnection(ContextRef.User(TestUserId)))
        {
            var now = DateTime.UtcNow.ToString("o");
            await Dapper.SqlMapper.ExecuteAsync(db,
                "INSERT INTO reminders(id, event_id, scheduled_at, channel_type, channel_id) " +
                "VALUES ('r1', @eid, @sch, 'discord', 'chan')",
                new { eid = id, sch = now });
        }

        var ok = await _repo.DeleteAsync(TestUserId, id,
            TestContext.Current.CancellationToken);
        Assert.True(ok);

        var gone = await _repo.GetByIdAsync(TestUserId, id,
            TestContext.Current.CancellationToken);
        Assert.Null(gone);

        // Reminder rows must have been cascaded — otherwise the FK would
        // have blocked the event delete (strict mode isn't on by default
        // in SQLite, but orphaned reminder rows are semantic garbage).
        using (var db = _dbFactory.CreateContextConnection(ContextRef.User(TestUserId)))
        {
            var leftover = await Dapper.SqlMapper.ExecuteScalarAsync<long>(db,
                "SELECT COUNT(*) FROM reminders WHERE event_id = @eid",
                new { eid = id });
            Assert.Equal(0, leftover);
        }
    }

    [Fact]
    public async Task GetRange_ReturnsOnlyEventsWithin_AndOrdersByStart()
    {
        var baseTime = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await _repo.CreateAsync(TestUserId,
            new Event { Title = "morning", StartAt = baseTime.AddHours(9) },
            TestContext.Current.CancellationToken);
        await _repo.CreateAsync(TestUserId,
            new Event { Title = "afternoon", StartAt = baseTime.AddHours(14) },
            TestContext.Current.CancellationToken);
        await _repo.CreateAsync(TestUserId,
            new Event { Title = "later-week", StartAt = baseTime.AddDays(5) },
            TestContext.Current.CancellationToken);
        await _repo.CreateAsync(TestUserId,
            new Event { Title = "evening", StartAt = baseTime.AddHours(19) },
            TestContext.Current.CancellationToken);

        var dayRange = (await _repo.GetRangeAsync(
            TestUserId, baseTime, baseTime.AddDays(1),
            TestContext.Current.CancellationToken)).ToList();

        Assert.Equal(3, dayRange.Count);
        Assert.Equal(new[] { "morning", "afternoon", "evening" },
            dayRange.Select(e => e.Title).ToArray());
    }

    [Fact]
    public async Task GetRange_RejectsInvertedWindow()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.GetRangeAsync(TestUserId, start.AddDays(1), start,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAll_OrdersByStart()
    {
        await _repo.CreateAsync(TestUserId, new Event
        {
            Title = "third",
            StartAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        }, TestContext.Current.CancellationToken);
        await _repo.CreateAsync(TestUserId, new Event
        {
            Title = "first",
            StartAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        }, TestContext.Current.CancellationToken);
        await _repo.CreateAsync(TestUserId, new Event
        {
            Title = "second",
            StartAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        }, TestContext.Current.CancellationToken);

        var list = (await _repo.GetAllAsync(TestUserId,
            TestContext.Current.CancellationToken)).ToList();
        Assert.Equal(new[] { "first", "second", "third" },
            list.Select(e => e.Title).ToArray());
    }

    [Fact]
    public async Task TeamContext_IsolatedFromPersonal()
    {
        const string teamSlug = "01J_EVENT_TEAM";
        await _repo.CreateAsync(ContextRef.User(TestUserId), TestUserId,
            new Event { Title = "personal", StartAt = DateTime.UtcNow },
            TestContext.Current.CancellationToken);
        await _repo.CreateAsync(ContextRef.Team(teamSlug), TestUserId,
            new Event { Title = "team", StartAt = DateTime.UtcNow },
            TestContext.Current.CancellationToken);

        var personal = (await _repo.GetAllAsync(ContextRef.User(TestUserId),
            TestContext.Current.CancellationToken)).ToList();
        var team = (await _repo.GetAllAsync(ContextRef.Team(teamSlug),
            TestContext.Current.CancellationToken)).ToList();

        Assert.Single(personal);
        Assert.Equal("personal", personal[0].Title);
        Assert.Single(team);
        Assert.Equal("team", team[0].Title);
    }

    [Fact]
    public async Task Create_RejectsOversizedDescription()
    {
        var evt = new Event
        {
            Title = "ok",
            StartAt = DateTime.UtcNow,
            Description = new string('d', Fishbowl.Core.Util.EventLimits.MaxDescriptionLength + 1),
        };

        var ex = await Assert.ThrowsAsync<Fishbowl.Core.Util.ResourceValidationException>(() =>
            _repo.CreateAsync(TestUserId, evt, TestContext.Current.CancellationToken));
        Assert.Equal("event", ex.Error.Resource);
        Assert.Equal("description", ex.Error.Field);
    }

    [Fact]
    public async Task Update_RejectsOversizedRRule()
    {
        var evt = new Event { Title = "ok", StartAt = DateTime.UtcNow };
        var id = await _repo.CreateAsync(TestUserId, evt, TestContext.Current.CancellationToken);

        var loaded = await _repo.GetByIdAsync(TestUserId, id, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        loaded!.RRule = new string('r', Fishbowl.Core.Util.EventLimits.MaxRRuleLength + 1);

        await Assert.ThrowsAsync<Fishbowl.Core.Util.ResourceValidationException>(() =>
            _repo.UpdateAsync(TestUserId, loaded, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDbDir))
        {
            try { Directory.Delete(_tempDbDir, true); } catch { }
        }
    }
}
