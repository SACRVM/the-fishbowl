using System.Globalization;

namespace Fishbowl.Core.Util;

// Minimal iCal RRULE (RFC 5545) subset shared by reminder expansion and
// calendar-range reads: FREQ=DAILY|WEEKLY|MONTHLY|YEARLY, INTERVAL,
// BYDAY (weekly only, no ordinal prefixes), COUNT, UNTIL. Anything outside
// the subset fails TryParse and callers degrade to treating the event as
// non-recurring (single occurrence at DTSTART) — the pre-expansion
// behavior, so an exotic imported rule never silences reminders entirely.
//
// All expansion math runs on UTC instants: occurrences carry DTSTART's UTC
// time-of-day. A "09:00 local" weekly event therefore shifts by an hour
// across DST changes — known MVP limitation, matching how values are
// stored (ISO-8601 UTC, no timezone identifiers on events).

public enum RRuleFreq { Daily, Weekly, Monthly, Yearly }

public sealed class RRuleSpec
{
    public RRuleFreq Freq { get; init; }
    public int Interval { get; init; } = 1;
    public IReadOnlyList<DayOfWeek>? ByDay { get; init; } // weekly only
    public DateTime? Until { get; init; }                 // UTC, inclusive
    public int? Count { get; init; }
}

public static class RRule
{
    // Hard stop for pathological series (a decades-old DAILY rule still
    // iterates from DTSTART every expansion). Bounds CPU; real windows are
    // minutes (scheduler) or weeks (calendar view), so hitting the cap
    // means the series start is absurdly far from the query window.
    private const int MaxIterations = 100_000;

    private static readonly string[] UntilFormats =
    {
        "yyyyMMdd'T'HHmmss'Z'",
        "yyyyMMdd'T'HHmmss",
        "yyyyMMdd",
    };

    public static bool TryParse(string? text, out RRuleSpec spec)
    {
        spec = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var body = text.Trim();
        if (body.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
            body = body["RRULE:".Length..];

        RRuleFreq? freq = null;
        var interval = 1;
        List<DayOfWeek>? byDay = null;
        DateTime? until = null;
        int? count = null;

        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) return false;
            var key = part[..eq].Trim().ToUpperInvariant();
            var value = part[(eq + 1)..].Trim().ToUpperInvariant();
            if (value.Length == 0) return false;

            switch (key)
            {
                case "FREQ":
                    freq = value switch
                    {
                        "DAILY" => RRuleFreq.Daily,
                        "WEEKLY" => RRuleFreq.Weekly,
                        "MONTHLY" => RRuleFreq.Monthly,
                        "YEARLY" => RRuleFreq.Yearly,
                        _ => null,
                    };
                    if (freq is null) return false;
                    break;

                case "INTERVAL":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out interval)
                        || interval < 1)
                        return false;
                    break;

                case "BYDAY":
                    byDay = new List<DayOfWeek>();
                    foreach (var token in value.Split(','))
                    {
                        // Ordinal prefixes ("2MO", "-1FR") are out of subset.
                        DayOfWeek? dow = token switch
                        {
                            "MO" => DayOfWeek.Monday,
                            "TU" => DayOfWeek.Tuesday,
                            "WE" => DayOfWeek.Wednesday,
                            "TH" => DayOfWeek.Thursday,
                            "FR" => DayOfWeek.Friday,
                            "SA" => DayOfWeek.Saturday,
                            "SU" => DayOfWeek.Sunday,
                            _ => null,
                        };
                        if (dow is null) return false;
                        if (!byDay.Contains(dow.Value)) byDay.Add(dow.Value);
                    }
                    if (byDay.Count == 0) return false;
                    break;

                case "UNTIL":
                    if (!DateTime.TryParseExact(value, UntilFormats, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var u))
                        return false;
                    until = u;
                    break;

                case "COUNT":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var c)
                        || c < 1)
                        return false;
                    count = c;
                    break;

                case "WKST":
                    // Accepted and ignored — only shifts INTERVAL>1 weekly
                    // block boundaries, and we anchor blocks on DTSTART's
                    // Monday-start week (WKST=MO, the RFC default).
                    break;

                default:
                    return false; // BYMONTHDAY, BYSETPOS, … — out of subset
            }
        }

        if (freq is null) return false;
        if (byDay is not null && freq != RRuleFreq.Weekly) return false;

        spec = new RRuleSpec
        {
            Freq = freq.Value,
            Interval = interval,
            ByDay = byDay,
            Until = until,
            Count = count,
        };
        return true;
    }

    // Occurrence starts within the half-open window [windowStart, windowEnd),
    // chronological. COUNT is honored from the series start, so occurrences
    // before the window still consume it.
    public static IEnumerable<DateTime> Expand(
        DateTime dtStart, RRuleSpec spec, DateTime windowStart, DateTime windowEnd)
    {
        if (windowEnd <= windowStart) yield break;

        var produced = 0;
        foreach (var occ in Occurrences(dtStart, spec))
        {
            if (spec.Count is int c && produced >= c) yield break;
            if (spec.Until is DateTime u && occ > u) yield break;
            if (occ >= windowEnd) yield break;
            produced++;
            if (occ >= windowStart) yield return occ;
        }
    }

    // Unbounded chronological occurrence stream from DTSTART (capped at
    // MaxIterations); Expand applies COUNT/UNTIL/window on top.
    private static IEnumerable<DateTime> Occurrences(DateTime dtStart, RRuleSpec spec)
    {
        switch (spec.Freq)
        {
            case RRuleFreq.Daily:
                for (var i = 0L; i < MaxIterations; i++)
                {
                    if (!TryAddDays(dtStart, i * spec.Interval, out var occ)) yield break;
                    yield return occ;
                }
                break;

            case RRuleFreq.Weekly when spec.ByDay is null:
                for (var i = 0L; i < MaxIterations; i++)
                {
                    if (!TryAddDays(dtStart, i * 7 * spec.Interval, out var occ)) yield break;
                    yield return occ;
                }
                break;

            case RRuleFreq.Weekly:
                {
                    // Monday-start week blocks INTERVAL weeks apart, anchored on
                    // DTSTART's week. Pattern-generated only: if DTSTART's
                    // weekday isn't in BYDAY, DTSTART itself doesn't occur.
                    var offsets = spec.ByDay!
                        .Select(MondayOffset)
                        .Distinct()
                        .OrderBy(o => o)
                        .ToArray();
                    var blockAnchor = dtStart.Date.AddDays(-MondayOffset(dtStart.DayOfWeek));
                    var timeOfDay = dtStart.TimeOfDay;
                    for (var block = 0L; block < MaxIterations; block++)
                    {
                        if (!TryAddDays(blockAnchor, block * 7 * spec.Interval, out var monday)) yield break;
                        foreach (var offset in offsets)
                        {
                            if (!TryAddDays(monday, offset, out var day)) yield break;
                            var occ = day.Add(timeOfDay);
                            if (occ < dtStart) continue;
                            yield return occ;
                        }
                    }
                    break;
                }

            case RRuleFreq.Monthly:
                {
                    // Same day-of-month as DTSTART; months without that day are
                    // skipped without consuming COUNT (RFC 5545: invalid dates
                    // MUST be ignored).
                    var day = dtStart.Day;
                    var anchor = new DateTime(dtStart.Year, dtStart.Month, 1, 0, 0, 0, dtStart.Kind);
                    for (var i = 0L; i < MaxIterations; i++)
                    {
                        var months = i * spec.Interval;
                        if (anchor.Year + months / 12 + 1 > 9999) yield break;
                        var month = anchor.AddMonths((int)months);
                        if (DateTime.DaysInMonth(month.Year, month.Month) < day) continue;
                        yield return month.AddDays(day - 1).Add(dtStart.TimeOfDay);
                    }
                    break;
                }

            case RRuleFreq.Yearly:
                {
                    // Same month + day as DTSTART; Feb 29 only lands on leap years.
                    var day = dtStart.Day;
                    var anchor = new DateTime(dtStart.Year, dtStart.Month, 1, 0, 0, 0, dtStart.Kind);
                    for (var i = 0L; i < MaxIterations; i++)
                    {
                        var years = i * spec.Interval;
                        if (anchor.Year + years > 9999) yield break;
                        var month = anchor.AddYears((int)years);
                        if (DateTime.DaysInMonth(month.Year, month.Month) < day) continue;
                        yield return month.AddDays(day - 1).Add(dtStart.TimeOfDay);
                    }
                    break;
                }
        }
    }

    private static int MondayOffset(DayOfWeek d) => ((int)d + 6) % 7;

    private static bool TryAddDays(DateTime d, long days, out DateTime result)
    {
        var ticks = d.Ticks + days * TimeSpan.TicksPerDay;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            result = default;
            return false;
        }
        result = new DateTime(ticks, d.Kind);
        return true;
    }
}
