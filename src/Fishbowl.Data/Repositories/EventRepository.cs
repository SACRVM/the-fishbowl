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
        // answer without needing SQLite's date() functions. An event that
        // starts before the window but is still running inside it (a
        // multi-day event) belongs to the window too.
        var plain = (await db.QueryAsync<Event>(new CommandDefinition(@"
            SELECT * FROM events
            WHERE (start_at >= @from OR (end_at IS NOT NULL AND end_at > @from))
              AND start_at < @to
              AND (rrule IS NULL OR rrule = '')
              AND COALESCE(all_day, 0) = 0
            ORDER BY start_at ASC",
            new
            {
                from = from.ToString("o"),
                to = to.ToString("o"),
            }, cancellationToken: ct))).ToList();

        // All-day events are dates (v12), and which dates [from, to) covers
        // depends on the caller's zone - so they're matched on dates with a
        // day of slack each side, and callers place them by StartDate.
        var (fromDay, toDay) = DayWindow(from, to);
        plain.AddRange(await db.QueryAsync<Event>(new CommandDefinition(@"
            SELECT * FROM events
            WHERE all_day = 1
              AND (rrule IS NULL OR rrule = '')
              AND start_date < @toDay AND end_date > @fromDay",
            new
            {
                fromDay = AllDayDates.ToText(fromDay),
                toDay = AllDayDates.ToText(toDay),
            }, cancellationToken: ct)));

        // Recurring series can begin long before the window and still occur
        // inside it — fetch every series that starts before the window end
        // and expand in C# (SQL can't walk an RRULE). Series count per
        // context is human-scale, so this stays cheap.
        var recurring = await db.QueryAsync<Event>(new CommandDefinition(@"
            SELECT * FROM events
            WHERE rrule IS NOT NULL AND rrule != ''
              AND start_at < @to
            ORDER BY start_at ASC",
            new { to = TimeUtil.AsUtc(to).AddDays(2).ToString("o") }, cancellationToken: ct));

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
            if (ev.AllDay)
            {
                // Whole days on the date anchors (UTC, so no DST), widened
                // by the series' length so a multi-day occurrence that began
                // before the window still shows.
                var span = duration ?? TimeSpan.FromDays(1);
                foreach (var occ in RRule.Expand(ev.StartAt, spec,
                    AllDayDates.Anchor(fromDay) - span, AllDayDates.Anchor(toDay)))
                    results.Add(CloneAt(ev, occ, duration));
                continue;
            }
            var zone = RRule.ResolveZone(ev.TimeZone);
            foreach (var occ in RRule.Expand(ev.StartAt, spec, fromUtc, toUtc, zone))
                results.Add(CloneAt(ev, occ, duration));
        }

        return results.OrderBy(e => e.StartAt).ToList();
    }

    // The dates a UTC window can touch in any zone: one day of slack on each
    // side, `to` exclusive.
    private static (DateOnly From, DateOnly To) DayWindow(DateTime from, DateTime to)
    {
        var f = DateOnly.FromDateTime(TimeUtil.AsUtc(from)).AddDays(-1);
        var t = DateOnly.FromDateTime(TimeUtil.AsUtc(to)).AddDays(2);
        return (f, t);
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
        StartDate = ev.AllDay ? AllDayDates.ToText(AllDayDates.FromAnchor(occStart)) : null,
        EndDate = ev.AllDay
            ? AllDayDates.ToText(AllDayDates.FromAnchor(occStart + (duration ?? TimeSpan.FromDays(1))))
            : null,
        RRule = ev.RRule,
        TimeZone = ev.TimeZone,
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
        AllDayDates.Normalize(evt);
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
                                start_date, end_date,
                                rrule, time_zone, location, reminder_minutes,
                                external_id, external_source,
                                created_by, created_at, updated_at)
            VALUES (@Id, @Title, @Description, @StartAt, @EndAt, @AllDay,
                    @StartDate, @EndDate,
                    @RRule, @TimeZone, @Location, @ReminderMinutes,
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
                evt.StartDate,
                evt.EndDate,
                evt.RRule,
                evt.TimeZone,
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
        AllDayDates.Normalize(evt);
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
                start_date = @StartDate, end_date = @EndDate,
                rrule = @RRule, time_zone = @TimeZone, location = @Location,
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
                evt.StartDate,
                evt.EndDate,
                evt.RRule,
                evt.TimeZone,
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
              AND COALESCE(all_day, 0) = 0
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
              AND COALESCE(all_day, 0) = 0
              AND datetime(start_at, '-' || reminder_minutes || ' minutes') < datetime(@to)
            ORDER BY start_at ASC",
            new { to = to.ToString("o") }, cancellationToken: ct));

        // All-day events remind relative to local midnight of their date in
        // the event's zone (UTC without one) - computed here, not in SQL.
        var allDay = await db.QueryAsync<Event>(new CommandDefinition(@"
            SELECT * FROM events
            WHERE all_day = 1
              AND reminder_minutes IS NOT NULL
              AND reminder_minutes >= 0
              AND ((rrule IS NOT NULL AND rrule != '') OR end_date >= @notAncientDay)",
            new { notAncientDay = AllDayDates.ToText(DateOnly.FromDateTime(TimeUtil.AsUtc(notAncient)).AddDays(-1)) },
            cancellationToken: ct));

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
            var zone = RRule.ResolveZone(ev.TimeZone);
            foreach (var occ in RRule.Expand(
                ev.StartAt, spec, fromUtc.AddMinutes(minutes), toUtc.AddMinutes(minutes), zone))
                results.Add(CloneAt(ev, occ, duration));
        }

        foreach (var ev in allDay)
            results.AddRange(DueAllDay(ev, fromUtc, toUtc, TimeUtil.AsUtc(notAncient)));

        return results.OrderBy(e => e.StartAt).ToList();
    }

    // Due reminders of one all-day event (a series expands on its dates).
    // Each returned copy's StartAt is the moment its day begins where the
    // event lives, so the trigger (StartAt - minutes) and the scheduler's
    // latch key are real instants.
    private static IEnumerable<Event> DueAllDay(Event ev, DateTime fromUtc, DateTime toUtc, DateTime notAncientUtc)
    {
        NormalizeTimes(ev);
        if (!AllDayDates.TryParse(ev.StartDate, out var first)) yield break;
        var minutes = ev.ReminderMinutes!.Value;
        var zone = RRule.ResolveZone(ev.TimeZone);
        var span = AllDayDates.TryParse(ev.EndDate, out var endDate) ? endDate.DayNumber - first.DayNumber : 1;

        IEnumerable<(DateOnly Date, bool Instance)> dates;
        if (!string.IsNullOrEmpty(ev.RRule) && RRule.TryParse(ev.RRule, out var spec))
        {
            // Occurrence dates whose local midnight can fall in [from + m, to + m).
            var lo = DateOnly.FromDateTime(fromUtc.AddMinutes(minutes)).AddDays(-1);
            var hi = DateOnly.FromDateTime(toUtc.AddMinutes(minutes)).AddDays(2);
            dates = RRule.Expand(AllDayDates.Anchor(first), spec, AllDayDates.Anchor(lo), AllDayDates.Anchor(hi))
                .Select(o => (AllDayDates.FromAnchor(o), true));
        }
        else
        {
            dates = new[] { (first, false) };
        }

        foreach (var (date, instance) in dates)
        {
            var midnight = AllDayDates.LocalMidnightUtc(date, zone);
            var trigger = midnight.AddMinutes(-minutes);
            if (trigger < fromUtc || trigger >= toUtc) continue;
            if (!instance && midnight < notAncientUtc) continue;
            var copy = CloneAt(ev, AllDayDates.Anchor(date), TimeSpan.FromDays(span));
            copy.IsRecurringInstance = instance;
            copy.StartAt = midnight;
            copy.EndAt = AllDayDates.LocalMidnightUtc(date.AddDays(span), zone);
            yield return copy;
        }
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
