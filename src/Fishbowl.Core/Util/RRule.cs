using System.Globalization;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

namespace Fishbowl.Core.Util;

// iCal RRULE (RFC 5545) for reminder expansion and calendar-range reads,
// evaluated by Ical.Net — every rule a calendar app can send (ordinal and
// monthly BYDAY, BYMONTHDAY, BYSETPOS, sub-daily frequencies, …), so the
// server and a synced phone agree on when something happens (sync spec,
// 2026-10-06). A text that isn't a rule fails TryParse and callers degrade to
// treating the event as non-recurring (a single occurrence at DTSTART).
//
// Expansion runs in the event's own time zone when it has one (events carry
// the IANA zone they were written in, user schema v11): occurrences keep
// DTSTART's wall-clock time there, so a "09:00" weekly event stays 09:00
// across DST, and each occurrence converts back to a UTC instant. Without a
// zone (older rows, clients that send none) the math runs on UTC instants,
// so occurrences carry DTSTART's UTC time-of-day. A wall-clock time that
// doesn't exist (the spring-forward gap) moves forward by the gap; an
// ambiguous one (the fall-back hour) takes its first occurrence.

public enum RRuleFreq { Secondly, Minutely, Hourly, Daily, Weekly, Monthly, Yearly }

public sealed class RRuleSpec
{
    public RRuleFreq Freq { get; init; }
    public int Interval { get; init; } = 1;
    public IReadOnlyList<DayOfWeek>? ByDay { get; init; } // the weekdays named (ordinals not shown)
    public DateTime? Until { get; init; }                 // UTC, inclusive
    public int? Count { get; init; }
    /// <summary>The rule as given, without an "RRULE:" prefix — what Ical.Net evaluates.</summary>
    public string Text { get; init; } = "";
}

public static class RRule
{
    // Bounds for pathological series: a rule that never matches stops after
    // this many empty steps instead of spinning; one expansion hands over at
    // most MaxOccurrences (an hourly rule over a year's view stays usable).
    private static readonly EvaluationOptions Options = new() { MaxUnmatchedIncrementsLimit = 1000 };
    private const int MaxOccurrences = 10_000;

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

        // COUNT and UNTIL together: RFC 5545 forbids it and Ical.Net refuses
        // it, but older API clients may have stored one — COUNT goes to
        // Ical.Net and UNTIL is applied on top (both bound the series, as
        // before), so such an event keeps recurring.
        DateTime? extraUntil = null;
        var parts = body.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var untilPart = parts.FirstOrDefault(x => x.Trim().StartsWith("UNTIL=", StringComparison.OrdinalIgnoreCase));
        if (untilPart is not null && parts.Any(x => x.Trim().StartsWith("COUNT=", StringComparison.OrdinalIgnoreCase)))
        {
            if (!DateTime.TryParseExact(untilPart.Trim()["UNTIL=".Length..], UntilFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var u))
                return false;
            extraUntil = DateTime.SpecifyKind(u, DateTimeKind.Utc);
            body = string.Join(';', parts.Where(x => !ReferenceEquals(x, untilPart)));
        }

        RecurrencePattern p;
        try { p = new RecurrencePattern(body); }
        catch (ArgumentException) { return false; }
        catch (FormatException) { return false; }

        var freq = p.Frequency switch
        {
            FrequencyType.Secondly => RRuleFreq.Secondly,
            FrequencyType.Minutely => RRuleFreq.Minutely,
            FrequencyType.Hourly => RRuleFreq.Hourly,
            FrequencyType.Daily => RRuleFreq.Daily,
            FrequencyType.Weekly => RRuleFreq.Weekly,
            FrequencyType.Monthly => RRuleFreq.Monthly,
            FrequencyType.Yearly => RRuleFreq.Yearly,
            _ => (RRuleFreq?)null,
        };
        if (freq is null || p.Interval < 1 || p.Count is < 1) return false;
        // Ical.Net skips an UNTIL it can't read; a rule that names one must have it.
        if (body.Contains("UNTIL=", StringComparison.OrdinalIgnoreCase) && p.Until is null) return false;

        var byDay = p.ByDay.Select(d => d.DayOfWeek).Distinct().ToList();
        spec = new RRuleSpec
        {
            Freq = freq.Value,
            Interval = p.Interval,
            ByDay = byDay.Count > 0 ? byDay : null,
            Until = extraUntil ?? (p.Until is { } until ? DateTime.SpecifyKind(until.AsUtc, DateTimeKind.Utc) : null),
            Count = p.Count,
            Text = body,
        };
        return true;
    }

    // An event's IANA (or Windows) zone id to a TimeZoneInfo; null when
    // absent or unknown on this host, which means UTC expansion.
    public static TimeZoneInfo? ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out var zone) ? zone : null;
    }

    // Occurrence starts within the half-open window [windowStart, windowEnd),
    // chronological, as UTC instants. COUNT is honored from the series
    // start, so occurrences before the window still consume it. With a
    // zone, occurrences are generated in its wall-clock time.
    public static IEnumerable<DateTime> Expand(
        DateTime dtStart, RRuleSpec spec, DateTime windowStart, DateTime windowEnd,
        TimeZoneInfo? zone = null)
    {
        var from = DateTime.SpecifyKind(TimeUtil.AsUtc(windowStart), DateTimeKind.Utc);
        var to = DateTime.SpecifyKind(TimeUtil.AsUtc(windowEnd), DateTimeKind.Utc);
        if (to <= from) yield break;

        var utcStart = DateTime.SpecifyKind(TimeUtil.AsUtc(dtStart), DateTimeKind.Utc);
        var tzid = IanaId(zone);
        var start = tzid is null
            ? new CalDateTime(utcStart, "UTC", true)
            : new CalDateTime(DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utcStart, zone!), DateTimeKind.Unspecified), tzid, true);

        IEnumerator<Occurrence> occurrences;
        try
        {
            var ev = new CalendarEvent { DtStart = start, RecurrenceRule = new RecurrencePattern(spec.Text) };
            occurrences = ev.GetOccurrences(new CalDateTime(from, "UTC", true), Options)
                .TakeWhileBefore(new CalDateTime(to, "UTC", true))
                .GetEnumerator();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or EvaluationException) { yield break; }

        using (occurrences)
        {
            for (var produced = 0; produced < MaxOccurrences; produced++)
            {
                bool more;
                try { more = occurrences.MoveNext(); }
                // A rule that stops matching (EvaluationLimitExceededException) or
                // that Ical.Net can't walk ends the series here — never the caller.
                catch (EvaluationException) { yield break; }
                catch (ArgumentException) { yield break; }
                if (!more) yield break;
                var occ = DateTime.SpecifyKind(occurrences.Current.Period.StartTime.AsUtc, DateTimeKind.Utc);
                if (occ >= to) yield break;
                if (spec.Until is DateTime u && occ > u) yield break;   // also covers the COUNT + UNTIL case
                if (occ >= from) yield return occ;
            }
        }
    }

    // The zone as Ical.Net (NodaTime's tz database) names it — IANA; a
    // Windows id is converted. Null (UTC expansion) for UTC itself or a zone
    // without an IANA name.
    private static string? IanaId(TimeZoneInfo? zone)
    {
        if (zone is null || zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime) return null;
        if (zone.HasIanaId) return zone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;
    }
}
