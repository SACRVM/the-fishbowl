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

    // Half-open range [from, to) ordered by start_at. Recurring events
    // (RRULE within the supported subset — see Fishbowl.Core.Util.RRule)
    // are expanded into per-occurrence instances flagged with
    // IsRecurringInstance; instances share the master's Id, so writes
    // against an instance's id edit the whole series. Out-of-subset rules
    // degrade to the master event only.
    Task<IEnumerable<Event>> GetRangeAsync(
        ContextRef ctx, DateTime from, DateTime to, CancellationToken ct = default);

    Task<string> CreateAsync(ContextRef ctx, string actorUserId, Event evt, CancellationToken ct = default);
    Task<bool> UpdateAsync(ContextRef ctx, Event evt, CancellationToken ct = default);
    Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default);

    // Events whose reminder *trigger time* (start_at − reminder_minutes)
    // falls in the half-open window [from, to). Used by the scheduler to
    // find reminders that are now due. Recurring events are expanded: each
    // due occurrence comes back as an IsRecurringInstance clone with
    // StartAt set to the occurrence start, so trigger math and messages
    // are per-occurrence. StartAt/EndAt are normalized to UTC kinds.
    // Skips non-recurring events whose `start_at` is older than
    // `notAncient` so a Saturday-down dispatcher doesn't drown in
    // long-past triggers on Monday morning.
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
