using System.Globalization;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class ReminderRepository : IReminderRepository
{
    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<ReminderRepository> _logger;

    public ReminderRepository(
        DatabaseFactory dbFactory,
        ILogger<ReminderRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<ReminderRepository>.Instance;
    }

    public async Task<IReadOnlySet<(string EventId, DateTime TriggerAt)>> GetSentTriggersAsync(
        ContextRef ctx, IEnumerable<string> eventIds, CancellationToken ct = default)
    {
        var ids = eventIds as IReadOnlyCollection<string> ?? eventIds.ToList();
        if (ids.Count == 0) return new HashSet<(string, DateTime)>();

        using var db = _dbFactory.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<(string EventId, string ScheduledAt)>(new CommandDefinition(
            "SELECT event_id, scheduled_at FROM reminders WHERE sent_at IS NOT NULL AND event_id IN @ids",
            new { ids }, cancellationToken: ct));

        // scheduled_at strings can carry "Z", a local offset (pre-expansion
        // rows written from Local-kind DateTimes), or no zone at all —
        // AdjustToUniversal + AssumeUniversal collapses all three to the
        // same UTC instant the dispatcher recomputes from start_at.
        return rows
            .Select(r => (r.EventId, DateTime.Parse(r.ScheduledAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)))
            .ToHashSet();
    }

    public async Task<bool> RecordSentAsync(
        ContextRef ctx, Reminder reminder, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);

        // Insert is idempotent: a second insert for the same event from a
        // concurrent tick collides on the unique implicit (id) — the caller
        // generates a fresh ULID per attempt, so true collisions come from
        // a different code path. We don't enforce a unique (event_id) here
        // because recurring events legitimately produce one row per
        // occurrence; dedupe is the scheduler's job via GetSentTriggersAsync.
        var affected = await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO reminders (id, event_id, scheduled_at, sent_at, channel_type, channel_id)
            VALUES (@Id, @EventId, @ScheduledAt, @SentAt, @ChannelType, @ChannelId)",
            new
            {
                reminder.Id,
                reminder.EventId,
                ScheduledAt = reminder.ScheduledAt.ToString("o"),
                SentAt = reminder.SentAt?.ToString("o"),
                reminder.ChannelType,
                reminder.ChannelId,
            }, cancellationToken: ct));

        return affected > 0;
    }
}
