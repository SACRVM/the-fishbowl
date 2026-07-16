using System.Text;
using System.Text.RegularExpressions;

namespace Fishbowl.Core.Util;

public static class SlugGenerator
{
    private static readonly Regex Collapse = new("[^a-z0-9]+", RegexOptions.Compiled);

    // Lowercase, ASCII-alphanumerics-and-hyphens, deduped hyphens, trimmed.
    // Empty after normalisation → returns "space" so a caller can still append
    // a disambiguation suffix rather than generating an invalid empty slug.
    public static string FromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "space";
        var normalized = name.Trim().ToLowerInvariant();

        // Best-effort diacritic fold (é → e, ß → ss-ish). We're not doing full
        // Unicode normalisation; for non-Latin input, the Collapse regex
        // strips everything and the caller hits the "space" fallback.
        var decomposed = normalized.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat != System.Globalization.UnicodeCategory.NonSpacingMark) builder.Append(ch);
        }
        var ascii = builder.ToString();

        var collapsed = Collapse.Replace(ascii, "-").Trim('-');
        if (collapsed.Length == 0) return "space";
        if (collapsed.Length > 60) collapsed = collapsed[..60].TrimEnd('-');
        return collapsed;
    }

    // Finds a slug that doesn't collide with an existing set. Tries the base
    // slug first, then -2, -3, … until free.
    public static string DedupeAgainst(string baseSlug, Func<string, bool> exists)
    {
        if (!exists(baseSlug)) return baseSlug;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseSlug}-{i}";
            if (!exists(candidate)) return candidate;
        }
        throw new InvalidOperationException($"Could not find a unique slug derived from '{baseSlug}'.");
    }
}
