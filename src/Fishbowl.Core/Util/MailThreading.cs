using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fishbowl.Core.Util;

/// <summary>
/// The pure parts of mail threading (mail spec, decision 4). Threads come from
/// the headers — Message-ID, In-Reply-To, References — across accounts and
/// both directions; a reply without them falls back to its subject (these
/// helpers) plus a shared participant within 30 days (the repository).
/// </summary>
public static partial class MailThreading
{
    /// <summary>How far back the subject fallback looks.</summary>
    public static readonly TimeSpan SubjectWindow = TimeSpan.FromDays(30);

    // Reply and forward prefixes as mail clients write them, in the languages
    // Fishbowl speaks and the common neighbours: Re, Aw/Antw (de), Fwd/Fw,
    // Wg (de "weitergeleitet"), Sv (nordic), Tr (fr "transféré"), with an
    // optional count ("Re[2]:", "Re(2):").
    [GeneratedRegex(@"^\s*(?:(?:re|aw|antw|fwd?|wg|sv|tr|vs)\s*(?:\[\d+\]|\(\d+\))?\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Prefixes();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>True when the subject starts with a reply or forward prefix.</summary>
    public static bool IsReplySubject(string? subject) =>
        !string.IsNullOrEmpty(subject) && Prefixes().IsMatch(subject);

    /// <summary>The subject a conversation shares: prefixes off, spaces folded,
    /// lower case. Null for an empty one — an empty subject threads nothing.</summary>
    public static string? SubjectKey(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var s = Spaces().Replace(Prefixes().Replace(subject, ""), " ").Trim().ToLowerInvariant();
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// A message's key: its Message-ID without brackets, or — for a message
    /// that has none — a stable hash of what identifies it (sender, date,
    /// subject, size), so the same message on two folders is still one.
    /// </summary>
    public static string MessageKey(string? messageId, string? from, DateTimeOffset? date, string? subject, long? size)
    {
        var id = CleanId(messageId);
        if (id is not null) return id;
        var basis = $"{from?.ToLowerInvariant()}|{date?.ToUniversalTime():o}|{subject}|{size}";
        return "fb-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis)))[..32].ToLowerInvariant() + "@fishbowl";
    }

    /// <summary>A Message-ID as stored: trimmed, without its angle brackets.</summary>
    public static string? CleanId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var v = id.Trim();
        if (v.StartsWith('<') && v.EndsWith('>')) v = v[1..^1].Trim();
        return v.Length == 0 ? null : v;
    }

    /// <summary>The ids a message points at: References, then In-Reply-To —
    /// cleaned, unique, never itself.</summary>
    public static IReadOnlyList<string> Parents(string key, string? inReplyTo, IEnumerable<string>? references)
    {
        var all = (references ?? Enumerable.Empty<string>()).Append(inReplyTo ?? "")
            .Select(CleanId).Where(r => r is not null && r != key).Select(r => r!);
        return all.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>A short line for the list: the text folded to one line.</summary>
    public static string? Snippet(string? text, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = Spaces().Replace(text, " ").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }
}
