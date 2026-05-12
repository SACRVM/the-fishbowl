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

    public async Task<IReadOnlySet<string>> GetSentEventIdsAsync(
        ContextRef ctx, IEnumerable<string> eventIds, CancellationToken ct = default)
    {
        var ids = eventIds as IReadOnlyCollection<string> ?? eventIds.ToList();
        if (ids.Count == 0) return new HashSet<string>();

        using var db = _dbFactory.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<string>(new CommandDefinition(
            "SELECT event_id FROM reminders WHERE sent_at IS NOT NULL AND event_id IN @ids",
            new { ids }, cancellationToken: ct));
        return new HashSet<string>(rows);
    }

    public async Task<bool> RecordSentAsync(
        ContextRef ctx, Reminder reminder, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);

        // Insert is idempotent: a second insert for the same event from a
        // concurrent tick collides on the unique implicit (id) — the caller
        // generates a fresh ULID per attempt, so true collisions come from
        // a different code path. We don't enforce a unique (event_id) here
        // because the model could expand to multiple reminders per event;
        // dedupe is the scheduler's job via GetSentEventIdsAsync.
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
