using System.Globalization;
using Fishbowl.Core.Models;

namespace Fishbowl.Core.Util;

// All-day events are dates, not instants (user schema v12). They carry
// `start_date` / `end_date` as `YYYY-MM-DD`, the end EXCLUSIVE like iCal's
// DTEND;VALUE=DATE: a one-day event on the 24th is 2026-09-24 .. 2026-09-25.
// `start_at` / `end_at` stay filled as `<date>T00:00:00Z` anchors so
// ordering and the ISO range queries keep working, but they are no longer
// real moments: a date means the same calendar day in every time zone.
public static class AllDayDates
{
    public const string Format = "yyyy-MM-dd";

    public static bool TryParse(string? text, out DateOnly date)
        => DateOnly.TryParseExact(text?.Trim(), Format, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);

    public static string ToText(DateOnly date) => date.ToString(Format, CultureInfo.InvariantCulture);

    // The UTC-midnight anchor stored in start_at / end_at.
    public static DateTime Anchor(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    public static DateOnly FromAnchor(DateTime anchor) => DateOnly.FromDateTime(TimeUtil.AsUtc(anchor));

    // The calendar date an instant was meant as. With the writer's zone that
    // is exact. Without one, "+12 h, then the UTC date" is right for every
    // zone from UTC−12 to UTC+12 as long as the instant is (near) local
    // midnight, which is how all-day events were stored before v12. UTC+13/+14
    // (Tonga, Kiribati) can land a day late — documented, not solvable
    // without the zone.
    public static DateOnly DateOf(DateTime instant, TimeZoneInfo? zone)
    {
        var utc = DateTime.SpecifyKind(TimeUtil.AsUtc(instant), DateTimeKind.Utc);
        if (zone is not null) return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
        return DateOnly.FromDateTime(utc.AddHours(12));
    }

    // Whole days between an old-style start and end instant: the rounded
    // duration, at least one. Independent of the zone, so "00:00 → 23:59",
    // "00:00 → next 00:00", a missing end and a zero-length end are all one
    // day; two local days are two.
    public static int SpanDays(DateTime start, DateTime? end)
    {
        if (end is not DateTime e) return 1;
        var days = (int)Math.Round((TimeUtil.AsUtc(e) - TimeUtil.AsUtc(start)).TotalDays, MidpointRounding.AwayFromZero);
        return Math.Max(1, days);
    }

    // The UTC instant of local midnight on `date` in `zone` (UTC without
    // one). A midnight skipped by DST moves forward to the first valid time.
    public static DateTime LocalMidnightUtc(DateOnly date, TimeZoneInfo? zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (zone is null) return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    // Brings an incoming event into the stored shape. All-day: dates from
    // startDate/endDate when given, else derived from startAt/endAt (older
    // clients, agents), then the anchors are written from the dates.
    // Timed: the date fields are cleared. Throws ArgumentException on a
    // malformed or inverted date range.
    public static void Normalize(Event evt)
    {
        if (!evt.AllDay)
        {
            evt.StartDate = null;
            evt.EndDate = null;
            return;
        }

        var zone = RRule.ResolveZone(evt.TimeZone);

        DateOnly start;
        if (!string.IsNullOrWhiteSpace(evt.StartDate))
        {
            if (!TryParse(evt.StartDate, out start))
                throw new ArgumentException("startDate must be YYYY-MM-DD", nameof(evt));
        }
        else
        {
            start = DateOf(evt.StartAt, zone);
        }

        DateOnly end;
        if (!string.IsNullOrWhiteSpace(evt.EndDate))
        {
            if (!TryParse(evt.EndDate, out end))
                throw new ArgumentException("endDate must be YYYY-MM-DD", nameof(evt));
            if (end <= start)
                throw new ArgumentException("endDate must be after startDate (it is exclusive)", nameof(evt));
        }
        else
        {
            end = start.AddDays(SpanDays(evt.StartAt, evt.EndAt));
        }

        evt.StartDate = ToText(start);
        evt.EndDate = ToText(end);
        evt.StartAt = Anchor(start);
        evt.EndAt = Anchor(end);
    }

    // True when an all-day event covers at least one day of [from, toExclusive).
    public static bool Overlaps(Event evt, DateOnly from, DateOnly toExclusive)
    {
        if (!TryParse(evt.StartDate, out var s)) return false;
        var e = TryParse(evt.EndDate, out var parsed) ? parsed : s.AddDays(1);
        return s < toExclusive && e > from;
    }
}
