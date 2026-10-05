using Fishbowl.Core.Models;

namespace Fishbowl.Core.Util;

// Hard upper bounds for Event fields. Same "stop-the-broken-client"
// rationale as NoteLimits/ContactLimits/TodoLimits — the calendar isn't
// meant to hold long-form text, so caps are tight enough to catch
// pathological writes without rejecting reasonable user input.
public static class EventLimits
{
    public const string Resource = "event";

    public const int MaxTitleLength = 1024;

    // 16 KB description. Long-form belongs in a Note linked from the
    // event (future feature), not stuffed into the calendar row.
    public const int MaxDescriptionLength = 16 * 1024;

    // RFC 5545 RRULE strings are short by spec — the longest sane one
    // (RDATE + EXDATE + complex BYSETPOS) still fits comfortably.
    public const int MaxRRuleLength = 4 * 1024;

    public const int MaxLocationLength = 512;

    // External provenance tag: provider id + provider name.
    public const int MaxExternalIdLength = 512;
    public const int MaxExternalSourceLength = 64;

    // IANA zone ids are short; the longest is around 32 characters.
    public const int MaxTimeZoneLength = 64;

    // The longest window a range read covers. Every occurrence of a series
    // inside it becomes its own clone, so an open-ended span is a cost the
    // caller picks for the server; a year and a month is more than the
    // calendar (six weeks), the digest (a day), /upcoming (60 days) and
    // list_events' upcoming_days (365) ask for.
    public const int MaxRangeDays = 400;

    // Range reads pad their window by a few days (all-day slack); both ends
    // stay that far inside DateTime's limits so the padding can't overflow.
    private static readonly DateTime EarliestRangeEnd = DateTime.MinValue.AddDays(7);
    private static readonly DateTime LatestRangeEnd = DateTime.MaxValue.AddDays(-7);

    /// <summary>Null when [from, to) is a window a range read accepts, else
    /// the refusal: <c>range_invalid</c> (inverted or not a real date) or
    /// <c>range_too_long</c> (more than <see cref="MaxRangeDays"/>).</summary>
    public static (string Code, string Message)? CheckRange(DateTime from, DateTime to)
    {
        from = TimeUtil.AsUtc(from);
        to = TimeUtil.AsUtc(to);
        if (from < EarliestRangeEnd || to > LatestRangeEnd || to <= from)
            return ("range_invalid", "`to` must be after `from`, and both must be real dates.");
        if (to - from > TimeSpan.FromDays(MaxRangeDays))
            return ("range_too_long", $"A range may span at most {MaxRangeDays} days.");
        return null;
    }

    public static ResourceValidationError? Validate(Event evt)
    {
        if (evt.Title is { Length: > MaxTitleLength })
            return new ResourceValidationError(Resource, "title", $"exceeds {MaxTitleLength} characters");

        if (evt.Description is { Length: > MaxDescriptionLength })
            return new ResourceValidationError(Resource, "description", $"exceeds {MaxDescriptionLength} characters");

        if (evt.RRule is { Length: > MaxRRuleLength })
            return new ResourceValidationError(Resource, "rrule", $"exceeds {MaxRRuleLength} characters");

        if (evt.Location is { Length: > MaxLocationLength })
            return new ResourceValidationError(Resource, "location", $"exceeds {MaxLocationLength} characters");

        if (evt.ExternalId is { Length: > MaxExternalIdLength })
            return new ResourceValidationError(Resource, "externalId", $"exceeds {MaxExternalIdLength} characters");

        if (evt.ExternalSource is { Length: > MaxExternalSourceLength })
            return new ResourceValidationError(Resource, "externalSource", $"exceeds {MaxExternalSourceLength} characters");

        if (evt.TimeZone is { Length: > MaxTimeZoneLength })
            return new ResourceValidationError(Resource, "timeZone", $"exceeds {MaxTimeZoneLength} characters");

        return null;
    }
}
