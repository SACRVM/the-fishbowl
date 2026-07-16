using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class EventRepository : IEventRepository
{
    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<EventRepository> _logger;

    public EventRepository(
        DatabaseFactory dbFactory,
        ILogger<EventRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<EventRepository>.Instance;
    }

    // ────────── ContextRef overloads (canonical) ──────────

    public async Task<Event?> GetByIdAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.QuerySingleOrDefaultAsync<Event>(
            new CommandDefinition("SELECT * FROM events WHERE id = @id",
                new { id }, cancellationToken: ct));
    }

    public async Task<IEnumerable<Event>> GetAllAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.QueryAsync<Event>(new CommandDefinition(
            "SELECT * FROM events ORDER BY start_at ASC", cancellationToken: ct));
    }

    public async Task<IEnumerable<Event>> GetRangeAsync(
        ContextRef ctx, DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (to <= from)
            throw new ArgumentException("`to` must be strictly after `from`", nameof(to));

        using var db = _dbFactory.CreateContextConnection(ctx);

        // start_at is stored as ISO-8601, which sorts lexicographically in
        // the same order as DateTime — string comparison gives the right
        // answer without needing SQLite's date() functions.
        var plain = (await db.QueryAsync<Event>(new CommandDefinition(@"
            SELECT * FROM events
            WHERE start_at >= @from AND start_at < @to
              AND (rrule IS NULL OR rrule = '')
            ORDER BY start_at ASC",
            new
            {
                from = from.ToString("o"),
                to = to.ToString("o"),
            }, cancellationToken: ct))).ToList();

        // Recurring series can begin long before the window and still occur
        // inside it — fetch every series that starts before the window end
        // and expand in C# (SQL can't walk an RRULE). Series count per
        // context is human-scale, so this stays cheap.
        var recurring = await db.QueryAsync<Event>(new CommandDefinition(@"
            SELECT * FROM events
            WHERE rrule IS NOT NULL AND rrule != ''
              AND start_at < @to
            ORDER BY start_at ASC",
            new { to = to.ToString("o") }, cancellationToken: ct));

        var fromUtc = TimeUtil.AsUtc(from);
        var toUtc = TimeUtil.AsUtc(to);
        var results = plain;
        foreach (var ev in results)
            NormalizeTimes(ev);

        foreach (var ev in recurring)
        {
            NormalizeTimes(ev);
            if (!RRule.TryParse(ev.RRule, out var spec))
            {
                // Out-of-subset rule — degrade to the master occurrence
                // only, exactly what the pre-expansion read path returned.
                if (ev.StartAt >= fromUtc && ev.StartAt < toUtc)
                    results.Add(ev);
                continue;
            }

            var duration = ev.EndAt is DateTime end ? end - ev.StartAt : (TimeSpan?)null;
            foreach (var occ in RRule.Expand(ev.StartAt, spec, fromUtc, toUtc))
                results.Add(CloneAt(ev, occ, duration));
        }

        return results.OrderBy(e => e.StartAt).ToList();
    }

    // Expanded occurrence of a recurring master — same Id, shifted times.
    private static Event CloneAt(Event ev, DateTime occStart, TimeSpan? duration) => new()
    {
        Id = ev.Id,
        Title = ev.Title,
        Description = ev.Description,
        StartAt = occStart,
        EndAt = duration is TimeSpan d ? occStart + d : null,
        AllDay = ev.AllDay,
        RRule = ev.RRule,
        Location = ev.Location,
        ReminderMinutes = ev.ReminderMinutes,
        ExternalId = ev.ExternalId,
        ExternalSource = ev.ExternalSource,
        CreatedBy = ev.CreatedBy,
        CreatedAt = ev.CreatedAt,
        UpdatedAt = ev.UpdatedAt,
        IsRecurringInstance = true,
    };

    // Stored instants are UTC but the TEXT→DateTime parse can surface them
    // as Local/Unspecified kinds; expansion windows and the scheduler's
    // trigger latch compare instants in C#, so pin everything to UTC here.
    private static void NormalizeTimes(Event ev)
    {
        ev.StartAt = TimeUtil.AsUtc(ev.StartAt);
        if (ev.EndAt is DateTime end) ev.EndAt = TimeUtil.AsUtc(end);
    }

    public async Task<string> CreateAsync(
        ContextRef ctx, string actorUserId, Event evt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(evt.Title))
            throw new ArgumentException("Event title is required", nameof(evt));
        // `end == start` is a zero-duration point-in-time event; only
        // strictly inverted windows are invalid.
        if (evt.EndAt is not null && evt.EndAt < evt.StartAt)
            throw new ArgumentException("Event end_at cannot be before start_at", nameof(evt));
        EnforceLimits(evt);

        if (string.IsNullOrEmpty(evt.Id))
            evt.Id = Ulid.NewUlid().ToString();

        evt.CreatedAt = DateTime.UtcNow;
        evt.UpdatedAt = evt.CreatedAt;
        evt.CreatedBy = actorUserId;

        _logger.LogDebug("Creating event {Id} in context {CtxType}:{CtxId}",
            evt.Id, ctx.Type, ctx.Id);

        using var db = _dbFactory.CreateContextConnection(ctx);
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO events (id, title, description, start_at, end_at, all_day,
                                rrule, location, reminder_minutes,
                                external_id, external_source,
                                created_by, created_at, updated_at)
            VALUES (@Id, @Title, @Description, @StartAt, @EndAt, @AllDay,
                    @RRule, @Location, @ReminderMinutes,
                    @ExternalId, @ExternalSource,
                    @CreatedBy, @CreatedAt, @UpdatedAt)",
            new
            {
                evt.Id,
                evt.Title,
                evt.Description,
                StartAt = evt.StartAt.ToString("o"),
                EndAt = evt.EndAt?.ToString("o"),
                AllDay = evt.AllDay ? 1 : 0,
                evt.RRule,
                evt.Location,
                evt.ReminderMinutes,
                evt.ExternalId,
                evt.ExternalSource,
                evt.CreatedBy,
                CreatedAt = evt.CreatedAt.ToString("o"),
                UpdatedAt = evt.UpdatedAt.ToString("o"),
            }, cancellationToken: ct));

        return evt.Id;
    }

    public async Task<bool> UpdateAsync(ContextRef ctx, Event evt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(evt.Title))
            throw new ArgumentException("Event title is required", nameof(evt));
        // `end == start` is a zero-duration point-in-time event; only
        // strictly inverted windows are invalid.
        if (evt.EndAt is not null && evt.EndAt < evt.StartAt)
            throw new ArgumentException("Event end_at cannot be before start_at", nameof(evt));
        EnforceLimits(evt);

        evt.UpdatedAt = DateTime.UtcNow;

        using var db = _dbFactory.CreateContextConnection(ctx);
        var affected = await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE events
            SET title = @Title, description = @Description,
                start_at = @StartAt, end_at = @EndAt, all_day = @AllDay,
                rrule = @RRule, location = @Location,
                reminder_minutes = @ReminderMinutes,
                external_id = @ExternalId, external_source = @ExternalSource,
                updated_at = @UpdatedAt
            WHERE id = @Id",
            new
            {
                evt.Title,
                evt.Description,
                StartAt = evt.StartAt.ToString("o"),
                EndAt = evt.EndAt?.ToString("o"),
                AllDay = evt.AllDay ? 1 : 0,
                evt.RRule,
                evt.Location,
                evt.ReminderMinutes,
                evt.ExternalId,
                evt.ExternalSource,
                UpdatedAt = evt.UpdatedAt.ToString("o"),
                evt.Id,
            }, cancellationToken: ct));

        return affected > 0;
    }

    public async Task<IReadOnlyList<Event>> ListDueRemindersAsync(
        ContextRef ctx, DateTime from, DateTime to, DateTime notAncient, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);

        // trigger_at = start_at - reminder_minutes; SQLite computes that on
        // the fly via `datetime(start_at, '-N minutes')`. Comparisons go
        // through `datetime()` on both sides so the formats line up — raw
        // ISO-8601 strings compare lexicographically *only* when both sides
        // have identical precision, which the SQLite datetime function
        // strips. Skipping the `datetime()` cast on @from/@to would silently
        // miss matches.
        var single = (await db.QueryAsync<Event>(new CommandDefinition(@"
            SELECT * FROM events
            WHERE reminder_minutes IS NOT NULL
              AND reminder_minutes >= 0
              AND (rrule IS NULL OR rrule = '')
              AND datetime(start_at, '-' || reminder_minutes || ' minutes') >= datetime(@from)
              AND datetime(start_at, '-' || reminder_minutes || ' minutes') <  datetime(@to)
              AND datetime(start_at) >= datetime(@notAncient)
            ORDER BY start_at ASC",
            new
            {
                from = from.ToString("o"),
                to = to.ToString("o"),
                notAncient = notAncient.ToString("o"),
            }, cancellationToken: ct))).ToList();

        // Recurring series: any rule whose DTSTART trigger precedes the
        // window end could have an occurrence due — SQL can't walk an
        // RRULE, so expansion happens here. No notAncient guard needed:
        // a trigger inside [from, to) implies the occurrence is recent.
        var recurring = await db.QueryAsync<Event>(new CommandDefinition(@"
            SELECT * FROM events
            WHERE reminder_minutes IS NOT NULL
              AND reminder_minutes >= 0
              AND rrule IS NOT NULL AND rrule != ''
              AND datetime(start_at, '-' || reminder_minutes || ' minutes') < datetime(@to)
            ORDER BY start_at ASC",
            new { to = to.ToString("o") }, cancellationToken: ct));

        var results = single;
        foreach (var ev in results)
            NormalizeTimes(ev);

        var fromUtc = TimeUtil.AsUtc(from);
        var toUtc = TimeUtil.AsUtc(to);
        foreach (var ev in recurring)
        {
            NormalizeTimes(ev);
            var minutes = ev.ReminderMinutes!.Value;

            if (!RRule.TryParse(ev.RRule, out var spec))
            {
                // Out-of-subset rule — same treatment as before expansion
                // existed: a single occurrence at DTSTART.
                var trigger = ev.StartAt.AddMinutes(-minutes);
                if (trigger >= fromUtc && trigger < toUtc
                    && ev.StartAt >= TimeUtil.AsUtc(notAncient))
                    results.Add(ev);
                continue;
            }

            // occurrence ∈ [from + minutes, to + minutes) ⇔ trigger ∈ [from, to)
            var duration = ev.EndAt is DateTime end ? end - ev.StartAt : (TimeSpan?)null;
            foreach (var occ in RRule.Expand(
                ev.StartAt, spec, fromUtc.AddMinutes(minutes), toUtc.AddMinutes(minutes)))
                results.Add(CloneAt(ev, occ, duration));
        }

        return results.OrderBy(e => e.StartAt).ToList();
    }

    public async Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);

        // reminders FK references events(id) — cascade the reminder rows
        // here rather than letting SQLite error out. Reminder delivery is
        // purely ephemeral state, not worth trying to preserve when the
        // underlying event is gone.
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM reminders WHERE event_id = @id",
            new { id }, cancellationToken: ct));

        var affected = await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM events WHERE id = @id",
            new { id }, cancellationToken: ct));

        return affected > 0;
    }

    private static void EnforceLimits(Event evt)
    {
        var error = EventLimits.Validate(evt);
        if (error is not null) throw new ResourceValidationException(error);
    }

    // ────────── Legacy personal-context aliases ──────────

    public Task<Event?> GetByIdAsync(string userId, string id, CancellationToken ct = default)
        => GetByIdAsync(ContextRef.User(userId), id, ct);

    public Task<IEnumerable<Event>> GetAllAsync(string userId, CancellationToken ct = default)
        => GetAllAsync(ContextRef.User(userId), ct);

    public Task<IEnumerable<Event>> GetRangeAsync(
        string userId, DateTime from, DateTime to, CancellationToken ct = default)
        => GetRangeAsync(ContextRef.User(userId), from, to, ct);

    public Task<string> CreateAsync(string userId, Event evt, CancellationToken ct = default)
        => CreateAsync(ContextRef.User(userId), userId, evt, ct);

    public Task<bool> UpdateAsync(string userId, Event evt, CancellationToken ct = default)
        => UpdateAsync(ContextRef.User(userId), evt, ct);

    public Task<bool> DeleteAsync(string userId, string id, CancellationToken ct = default)
        => DeleteAsync(ContextRef.User(userId), id, ct);
}
