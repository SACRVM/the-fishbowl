using System;
using System.Text.RegularExpressions;

namespace Fishbowl.Core.Util;

public static class TagName
{
    // The longest name a tag may have (NoteLimits.MaxTagLength follows it).
    public const int MaxLength = 50;

    private static readonly Regex Allowed = new($"^[a-z0-9_:-]{{1,{MaxLength}}}$", RegexOptions.Compiled);

    public static string Normalize(string raw)
    {
        if (raw is null) throw new ArgumentNullException(nameof(raw));

        var trimmed = raw.Trim().ToLowerInvariant();
        if (!Allowed.IsMatch(trimmed))
        {
            throw new ArgumentException(
                $"Tag '{raw}' is invalid: must be 1–{MaxLength} chars of [a-z0-9_:-] after trim/lowercase.",
                nameof(raw));
        }
        return trimmed;
    }

    public static bool IsValid(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return Allowed.IsMatch(raw.Trim().ToLowerInvariant());
    }
}
