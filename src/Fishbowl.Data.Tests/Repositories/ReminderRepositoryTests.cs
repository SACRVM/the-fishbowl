using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Tests.Repositories;

// Reminder repo carries the "we already fired this" latch for the scheduler.
// Tests exercise the bulk dedupe query and the single-row insert with
// realistic event-row plumbing so the FK to events doesn't bite.
public class ReminderRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DatabaseFactory _factory;
    private readonly EventRepository _events;
    private readonly ReminderRepository _reminders;
    private const string TestUser = "reminder_user";

    public ReminderRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "fishbowl_reminder_repo_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _factory = new DatabaseFactory(_tempDir);
        _events = new EventRepository(_factory);
        _reminders = new ReminderRepository(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private async Task<string> CreateEventAsync(DateTime startAt, int? reminderMinutes = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var evt = new Event
        {
            Title = "Test event",
            StartAt = startAt,
            ReminderMinutes = reminderMinutes,
        };
        return await _events.CreateAsync(TestUser, evt, ct);
    }

    [Fact]
    public async Task GetSentTriggersAsync_ReturnsEmptyForUnknownEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _reminders.GetSentTriggersAsync(
            ContextRef.User(TestUser),
            new[] { "non-existent-1", "non-existent-2" }, ct);
        Assert.Empty(result);
    }

    [Fact]
    public async Task RecordSentAsync_PersistsReminder_AndAppearsInSentSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventId = await CreateEventAsync(
            new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
            reminderMinutes: 15);

        var trigger = new DateTime(2026, 6, 1, 9, 45, 0, DateTimeKind.Utc);
        var sentAt = new DateTime(2026, 6, 1, 9, 45, 30, DateTimeKind.Utc);

        var inserted = await _reminders.RecordSentAsync(
            ContextRef.User(TestUser),
            new Reminder
            {
                Id = "r_test_001",
                EventId = eventId,
                ScheduledAt = trigger,
                SentAt = sentAt,
                ChannelType = "discord",
                ChannelId = "dm-channel-id",
            }, ct);

        Assert.True(inserted);

        var sent = await _reminders.GetSentTriggersAsync(
            ContextRef.User(TestUser),
            new[] { eventId, "some-other-event" }, ct);
        Assert.Contains((eventId, trigger), sent);
        Assert.DoesNotContain(sent, t => t.EventId == "some-other-event");
    }

    [Fact]
    public async Task GetSentTriggersAsync_DistinguishesOccurrencesOfSameEvent()
    {
        // Recurring events fire once per occurrence under the same
        // event_id — the latch must be trigger-granular or the second
        // occurrence would be silently swallowed.
        var ct = TestContext.Current.CancellationToken;
        var eventId = await CreateEventAsync(
            new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
            reminderMinutes: 15);

        var firstTrigger = new DateTime(2026, 6, 1, 9, 45, 0, DateTimeKind.Utc);
        await _reminders.RecordSentAsync(
            ContextRef.User(TestUser),
            new Reminder
            {
                Id = "r_occ_1",
                EventId = eventId,
                ScheduledAt = firstTrigger,
                SentAt = firstTrigger.AddSeconds(30),
                ChannelType = "discord",
                ChannelId = "dm-channel-id",
            }, ct);

        var sent = await _reminders.GetSentTriggersAsync(
            ContextRef.User(TestUser), new[] { eventId }, ct);

        var secondTrigger = new DateTime(2026, 6, 2, 9, 45, 0, DateTimeKind.Utc);
        Assert.Contains((eventId, firstTrigger), sent);
        Assert.DoesNotContain((eventId, secondTrigger), sent);
    }

    [Fact]
    public async Task GetSentTriggersAsync_IgnoresPendingRowsWhereSentAtIsNull()
    {
        // The schema allows sent_at to be null (a future scheduler model
        // might insert "scheduled" rows ahead of time). The dedupe query
        // must only catch *delivered* reminders, otherwise pending rows
        // would silently block re-tries when delivery hadn't happened.
        var ct = TestContext.Current.CancellationToken;
        var eventId = await CreateEventAsync(
            new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
            reminderMinutes: 15);

        // Direct insert to get a pending row in there; the repo's
        // RecordSentAsync always sets sent_at on the row it writes.
        using (var db = _factory.CreateContextConnection(ContextRef.User(TestUser)))
        {
            await Dapper.SqlMapper.ExecuteAsync(db, @"
                INSERT INTO reminders (id, event_id, scheduled_at, sent_at, channel_type, channel_id)
                VALUES ('r_pending', @eventId, '2026-06-01T09:45:00Z', NULL, 'discord', 'cid')",
                new { eventId });
        }

        var sent = await _reminders.GetSentTriggersAsync(
            ContextRef.User(TestUser),
            new[] { eventId }, ct);

        Assert.Empty(sent);
    }
}
