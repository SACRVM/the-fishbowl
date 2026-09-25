namespace Fishbowl.Core.Util;

// A user's date & time format. Browsers never expose the OS regional
// format to web pages (only the UI language), so it is an explicit
// setting — mirrored in js/lib/format.js, which does the formatting.
//   iso  2026-09-25 20:00   (the default)
//   de   25.09.2026 20:00
//   uk   25/09/2026 20:00
//   us   09/25/2026 8:00 PM
public static class DateFormats
{
    public static readonly string[] Names = { "iso", "de", "uk", "us" };

    public static bool IsKnown(string? name) => name is not null && Array.IndexOf(Names, name) >= 0;
}
