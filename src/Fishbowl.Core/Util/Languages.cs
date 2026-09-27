namespace Fishbowl.Core.Util;

// The UI languages Fishbowl ships a table for (js/i18n/*.js). A user's choice
// is one of these codes or null for automatic — the browser's language when
// there is a table for it, else English. Independent of DateFormats.
public static class Languages
{
    public static readonly string[] Codes = { "en", "de" };

    public static bool IsKnown(string? code) => code is not null && Array.IndexOf(Codes, code) >= 0;
}
