namespace Fishbowl.Core.Util;

// Stored timestamps are UTC instants (ISO-8601 "o" format). Depending on
// which parser touched them they surface as Kind=Utc (roundtrip parse of a
// "Z" string), Kind=Local (plain DateTime.Parse converts zoned strings to
// local time), or Kind=Unspecified (string without a zone suffix).
// Normalizing before any window/equality math keeps scheduler and
// recurrence expansion deterministic regardless of host timezone.
public static class TimeUtil
{
    public static DateTime AsUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        // Unspecified: the stored value was written as a UTC instant, the
        // parser just dropped the marker — stamp it back on, don't convert.
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };
}
