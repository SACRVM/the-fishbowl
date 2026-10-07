using Fishbowl.Core.Models;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;

namespace Fishbowl.Core.Util;

// Events as iCalendar (RFC 5545): the Calendar app's .ics export and import
// and the subscription feed (sync spec, phase 1). Fishbowl's own fields:
// UID, SUMMARY, DESCRIPTION, LOCATION, DTSTART/DTEND (TZID for a timed event
// in a zone, UTC without one, VALUE=DATE for an all-day event — end
// exclusive, like Fishbowl's endDate), RRULE and the first VALARM before the
// start as reminderMinutes. Overridden occurrences (RECURRENCE-ID) and
// EXDATEs arrive with the exceptions model (phase 3): until then an import
// counts an override as skipped and keeps the series whole.
public static class EventFormats
{
    public const string ProductId = "-//The Fishbowl//Calendar//EN";

    /// <summary>The events (masters — a series once, with its RRULE) as one VCALENDAR.</summary>
    public static string ToICalendar(IEnumerable<Event> events, string calendarName)
    {
        var cal = new Calendar { ProductId = ProductId };
        cal.AddProperty("X-WR-CALNAME", calendarName);
        var zones = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            var ev = new CalendarEvent
            {
                Uid = string.IsNullOrWhiteSpace(e.Uid) ? e.Id + "@fishbowl" : e.Uid,
                Summary = e.Title,
                Description = Blank(e.Description),
                Location = Blank(e.Location),
                DtStamp = Utc(e.UpdatedAt == default ? DateTime.UtcNow : e.UpdatedAt),
            };
            if (e.CreatedAt != default) ev.Created = Utc(e.CreatedAt);
            if (e.UpdatedAt != default) ev.LastModified = Utc(e.UpdatedAt);

            if (e.AllDay && AllDayDates.TryParse(e.StartDate, out var startDate))
            {
                var endDate = AllDayDates.TryParse(e.EndDate, out var end) && end > startDate ? end : startDate.AddDays(1);
                ev.DtStart = new CalDateTime(startDate);
                ev.DtEnd = new CalDateTime(endDate);
            }
            else
            {
                var zone = RRule.ResolveZone(e.TimeZone);
                var tzid = IanaId(zone);
                ev.DtStart = At(e.StartAt, zone, tzid);
                if (e.EndAt is DateTime endAt && endAt >= e.StartAt) ev.DtEnd = At(endAt, zone, tzid);
                if (tzid is not null) zones.Add(tzid);
            }

            if (RRule.TryParse(e.RRule, out var spec)) ev.RecurrenceRule = new RecurrencePattern(spec.Text);
            if (e.ReminderMinutes is int minutes && minutes >= 0)
            {
                ev.Alarms.Add(new Alarm
                {
                    Action = AlarmAction.Display,
                    Description = e.Title,
                    Trigger = new Trigger(Duration.FromMinutes(-minutes)),
                });
            }
            cal.Events.Add(ev);
        }
        foreach (var tzid in zones) cal.AddTimeZone(tzid);
        return new CalendarSerializer().SerializeToString(cal) ?? "";
    }

    /// <summary>What a file held, read into events: the ones to create, and how many
    /// VEVENTs were left out (an overridden occurrence; one without a start).</summary>
    public sealed record Parsed(List<Event> Events, int Skipped);

    /// <summary>
    /// Reads every VEVENT of every VCALENDAR in the text. A time without a zone
    /// (floating) is taken in <paramref name="floatingZone"/> (the importing
    /// browser's), else UTC. Throws <see cref="FormatException"/> when the text
    /// isn't iCalendar.
    /// </summary>
    public static Parsed ParseICalendar(string text, string? floatingZone = null)
    {
        // Ical.Net reads anything without complaint (a vCard is an empty
        // collection): a file needs a VCALENDAR.
        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("The file isn't iCalendar.");
        List<Calendar> calendars;
        try { calendars = CalendarCollection.Load(text).ToList(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { throw new FormatException("The file isn't iCalendar.", ex); }
        if (calendars.Count == 0) throw new FormatException("The file isn't iCalendar.");

        var floating = RRule.ResolveZone(floatingZone);
        var list = new List<Event>();
        var skipped = 0;
        foreach (var cal in calendars)
        {
            foreach (var ev in cal.Events)
            {
                if (ev.RecurrenceId is not null || ev.DtStart is null) { skipped++; continue; }
                list.Add(ToEvent(ev, floating));
            }
        }
        return new Parsed(list, skipped);
    }

    private static Event ToEvent(CalendarEvent ev, TimeZoneInfo? floating)
    {
        var e = new Event
        {
            Uid = Blank(ev.Uid),
            Title = Blank(ev.Summary) ?? "Untitled",
            Description = Blank(ev.Description),
            Location = Blank(ev.Location),
        };
        var start = ev.DtStart!;
        if (!start.HasTime)
        {
            // All-day: dates, end exclusive (DTEND, else DURATION in days, else one day).
            e.AllDay = true;
            var first = start.Date;
            var last = ev.DtEnd is { HasTime: false } dtEnd && dtEnd.Date > first ? dtEnd.Date
                : ev.Duration is { } d && d.ToTimeSpanUnspecified().Days > 0 ? first.AddDays(d.ToTimeSpanUnspecified().Days)
                : first.AddDays(1);
            e.StartDate = AllDayDates.ToText(first);
            e.EndDate = AllDayDates.ToText(last);
            e.StartAt = AllDayDates.Anchor(first);
            e.EndAt = AllDayDates.Anchor(last);
        }
        else
        {
            var zone = ZoneOf(start, floating);
            e.TimeZone = zone is null ? null : IanaId(zone) ?? zone.Id;
            e.StartAt = InstantOf(start, floating);
            if (ev.DtEnd is { HasTime: true } end) e.EndAt = InstantOf(end, floating);
            else if (ev.Duration is { } d) e.EndAt = e.StartAt + d.ToTimeSpanUnspecified();
            if (e.EndAt < e.StartAt) e.EndAt = null;
        }

        var rule = ev.RecurrenceRule?.ToString();
        if (RRule.TryParse(rule, out var spec)) e.RRule = spec.Text;

        // The first alarm that rings before (or at) the start, relative to it.
        foreach (var alarm in ev.Alarms)
        {
            if (alarm.Trigger is { IsRelative: true, Duration: { } offset } t
                && !string.Equals(t.Related, "END", StringComparison.OrdinalIgnoreCase))
            {
                var span = offset.ToTimeSpanUnspecified();
                if (span <= TimeSpan.Zero) { e.ReminderMinutes = (int)Math.Round(-span.TotalMinutes); break; }
            }
        }
        return e;
    }

    // A CalDateTime's instant: UTC as is, a TZID in its zone, floating in the
    // importer's zone (or UTC).
    private static DateTime InstantOf(CalDateTime t, TimeZoneInfo? floating)
    {
        if (t.IsFloating)
        {
            var local = DateTime.SpecifyKind(t.Value, DateTimeKind.Unspecified);
            return floating is null
                ? DateTime.SpecifyKind(local, DateTimeKind.Utc)
                : DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, floating), DateTimeKind.Utc);
        }
        return DateTime.SpecifyKind(t.AsUtc, DateTimeKind.Utc);
    }

    private static TimeZoneInfo? ZoneOf(CalDateTime t, TimeZoneInfo? floating)
    {
        if (t.IsFloating) return floating;
        if (t.IsUtc || string.IsNullOrEmpty(t.TzId)) return null;
        return RRule.ResolveZone(t.TzId);
    }

    private static CalDateTime At(DateTime instant, TimeZoneInfo? zone, string? tzid)
    {
        var utc = DateTime.SpecifyKind(TimeUtil.AsUtc(instant), DateTimeKind.Utc);
        if (tzid is null || zone is null) return new CalDateTime(utc, "UTC", true);
        var local = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, zone), DateTimeKind.Unspecified);
        return new CalDateTime(local, tzid, true);
    }

    private static CalDateTime Utc(DateTime t) => new(DateTime.SpecifyKind(TimeUtil.AsUtc(t), DateTimeKind.Utc), "UTC", true);

    // The zone as iCalendar names it — IANA (a Windows id converted); null for
    // UTC or a zone without one, which then goes out as UTC.
    private static string? IanaId(TimeZoneInfo? zone)
    {
        if (zone is null || zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime) return null;
        if (zone.HasIanaId) return zone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
