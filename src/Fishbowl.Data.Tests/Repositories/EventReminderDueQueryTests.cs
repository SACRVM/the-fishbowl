using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Tests.Repositories;

// EventRepository.ListDueRemindersAsync is the scheduler's primary read.
// It has subtle SQL (datetime() coercion both sides, half-open window,
// ancient guard) so we cover the corner cases explicitly here.
public class EventReminderDueQueryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DatabaseFactory _factory;
    private readonly EventRepository _events;
    private const string TestUser = "due_query_user";

    public EventReminderDueQueryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "fishbowl_event_due_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _factory = new DatabaseFactory(_tempDir);
        _events = new EventRepository(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private async Task<string> CreateAsync(DateTime startAt, int? reminderMinutes, string title = "evt")
    {
        var ct = TestContext.Current.CancellationToken;
        return await _events.CreateAsync(TestUser,
            new Event { Title = title, StartAt = startAt, ReminderMinutes = reminderMinutes }, ct);
    }

    [Fact]
    public async Task IgnoresEventsWithoutReminderMinutes()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        await CreateAsync(now.AddMinutes(30), reminderMinutes: null);
        await CreateAsync(now.AddMinutes(30), reminderMinutes: 30, title: "with-rem");

        var due = await _events.ListDueRemindersAsync(
            ContextRef.User(TestUser),
            from: now.AddMinutes(-5),
            to: now.AddMinutes(5),
            notAncient: now.AddDays(-1), ct);

        Assert.Single(due);
        Assert.Equal("with-rem", due[0].Title);
    }

    [Fact]
    public async Task FiresEventWhenTriggerCrossesWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);

        // Event starts at 10:30, 30-minute reminder → trigger at 10:00.
        // Window [09:55, 10:05) catches it.
        await CreateAsync(now.AddMinutes(30), reminderMinutes: 30);

        var due = await _events.ListDueRemindersAsync(
            ContextRef.User(TestUser),
            from: now.AddMinutes(-5),
            to: now.AddMinutes(5),
            notAncient: now.AddDays(-1), ct);

        Assert.Single(due);
    }

    [Fact]
    public async Task DoesNotFireBeforeTriggerWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);

        // Event at 11:00, 15-min reminder → trigger at 10:45. Window
        // [09:55, 10:05) is too early.
        await CreateAsync(now.AddHours(1), reminderMinutes: 15);

        var due = await _events.ListDueRemindersAsync(
            ContextRef.User(TestUser),
            from: now.AddMinutes(-5),
            to: now.AddMinutes(5),
            notAncient: now.AddDays(-1), ct);

        Assert.Empty(due);
    }

    [Fact]
    public async Task DoesNotFireWhenWindowEndsExactlyAtTrigger()
    {
        // Half-open [from, to) — the trigger sitting exactly on `to` is the
        // NEXT tick's job. This is what stops two adjacent ticks from
        // double-firing the same trigger if the dispatcher hasn't yet
        // recorded the sent state.
        var ct = TestContext.Current.CancellationToken;
        var trigger = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        await CreateAsync(trigger.AddMinutes(30), reminderMinutes: 30);

        var due = await _events.ListDueRemindersAsync(
            ContextRef.User(TestUser),
            from: trigger.AddMinutes(-5),
            to: trigger, // upper bound exclusive
            notAncient: trigger.AddDays(-1), ct);

        Assert.Empty(due);
    }

    [Fact]
    public async Task SkipsAncientEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);

        // Event 3 days ago — reminder would have triggered then. Window
        // is current "now ± 5min", but notAncient cuts at 1 day back.
        await CreateAsync(now.AddDays(-3), reminderMinutes: 0); // trigger == start_at

        var due = await _events.ListDueRemindersAsync(
            ContextRef.User(TestUser),
            from: now.AddDays(-10),
            to: now.AddMinutes(5),
            notAncient: now.AddDays(-1), ct);

        Assert.Empty(due);
    }

    [Fact]
    public async Task ReminderMinutesZero_FiresAtStartAt()
    {
        // Zero is a valid value — "ping me when it starts". The SQL
        // generator must accept it (>= 0, not > 0).
        var ct = TestContext.Current.CancellationToken;
        var startAt = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        await CreateAsync(startAt, reminderMinutes: 0);

        var due = await _events.ListDueRemindersAsync(
            ContextRef.User(TestUser),
            from: startAt.AddSeconds(-30),
            to: startAt.AddSeconds(30),
            notAncient: startAt.AddDays(-1), ct);

        Assert.Single(due);
    }
}
