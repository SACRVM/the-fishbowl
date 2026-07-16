using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

// Reminder dispatch records — the idempotency latch that keeps the
// scheduler from re-firing the same trigger across ticks. Lives in the
// per-context DB beside `events`; the scheduler reads `events` to find
// what's due, then writes here when it actually fires the notification.
public interface IReminderRepository
{
    // Returns (event_id, trigger time) pairs that already have a sent
    // reminder row in this context. Trigger-granular because recurring
    // events fire once per occurrence under the same event_id — an
    // event-id-only latch would silence a series after its first
    // reminder. TriggerAt comes back UTC-normalized so callers can
    // compare against locally computed trigger instants. Bulk-shaped to
    // avoid N+1 SELECTs from the dispatcher's hot loop.
    Task<IReadOnlySet<(string EventId, DateTime TriggerAt)>> GetSentTriggersAsync(
        ContextRef ctx, IEnumerable<string> eventIds, CancellationToken ct = default);

    // Inserts the reminder row with sent_at set — single atomic write,
    // because the scheduler only ever records reminders that have been
    // delivered (not pending). Returns true on insert, false on conflict.
    Task<bool> RecordSentAsync(ContextRef ctx, Reminder reminder, CancellationToken ct = default);
}
