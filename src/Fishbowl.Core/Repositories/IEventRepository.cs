using Fishbowl.Core;
using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

public interface IEventRepository
{
    // ContextRef overloads — personal and team share the schema; the
    // ContextRef picks the file. Ordering is always by `start_at` ASC
    // so consumers can render a chronological timeline without re-sorting.

    Task<Event?> GetByIdAsync(ContextRef ctx, string id, CancellationToken ct = default);

    // Full list — typically small (dozens to hundreds). Use `GetRangeAsync`
    // for calendar-view queries so we don't over-fetch.
    Task<IEnumerable<Event>> GetAllAsync(ContextRef ctx, CancellationToken ct = default);

    // Half-open range [from, to) ordered by start_at. `from`/`to` are
    // compared against `start_at`. Recurrence (RRULE) is NOT expanded —
    // callers see the master event; the scheduler does expansion when it
    // fires reminders. That keeps the read path free of time-math surprises.
    Task<IEnumerable<Event>> GetRangeAsync(
        ContextRef ctx, DateTime from, DateTime to, CancellationToken ct = default);

    Task<string> CreateAsync(ContextRef ctx, string actorUserId, Event evt, CancellationToken ct = default);
    Task<bool> UpdateAsync(ContextRef ctx, Event evt, CancellationToken ct = default);
    Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default);

    // Events whose reminder *trigger time* (start_at − reminder_minutes)
    // falls in the half-open window [from, to). Used by the scheduler to
    // find reminders that are now due. Skips events whose `start_at` is
    // older than `notAncient` so a Saturday-down dispatcher doesn't drown
    // in long-past triggers on Monday morning.
    Task<IReadOnlyList<Event>> ListDueRemindersAsync(
        ContextRef ctx, DateTime from, DateTime to, DateTime notAncient, CancellationToken ct = default);

    // Legacy personal-context aliases for cookie-auth call sites.
    Task<Event?> GetByIdAsync(string userId, string id, CancellationToken ct = default);
    Task<IEnumerable<Event>> GetAllAsync(string userId, CancellationToken ct = default);
    Task<IEnumerable<Event>> GetRangeAsync(
        string userId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<string> CreateAsync(string userId, Event evt, CancellationToken ct = default);
    Task<bool> UpdateAsync(string userId, Event evt, CancellationToken ct = default);
    Task<bool> DeleteAsync(string userId, string id, CancellationToken ct = default);
}
