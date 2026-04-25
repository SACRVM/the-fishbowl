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

        return null;
    }
}
