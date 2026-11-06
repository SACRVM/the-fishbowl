using System.Text.RegularExpressions;

namespace Fishbowl.Core.Util;

/// <summary>
/// A note's stable external key (<c>notes.slug</c>, user/space schema v31):
/// what an agent's Git mirror names the note's file by, so its history goes
/// on. Up to 200 characters of letters, digits, <c>. _ -</c> in segments
/// joined by <c>/</c> — a relative path without <c>.</c>/<c>..</c> segments
/// or an empty one. Kept as written; unique in the workspace in any case (a
/// mirror on a case-insensitive disk can't hold two that differ only there).
/// </summary>
public static partial class NoteSlug
{
    public const int MaxLength = 200;

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex Shape();

    /// <summary>Why <paramref name="slug"/> can't be one, or null.</summary>
    public static string? Problem(string slug)
    {
        if (slug.Length > MaxLength) return $"a slug is at most {MaxLength} characters";
        if (!Shape().IsMatch(slug))
            return "a slug is letters, digits, '.', '_' and '-', in segments joined by '/'";
        foreach (var segment in slug.Split('/'))
            if (segment is "." or "..") return "a slug has no '.' or '..' segment";
        return null;
    }

    /// <summary>The slug as stored, or a refusal (400) when it can't be one.</summary>
    public static string Validate(string slug)
    {
        var trimmed = slug.Trim();
        if (Problem(trimmed) is { } problem)
            throw new ResourceValidationException(new ResourceValidationError(
                NoteLimits.Resource, "slug", problem, ResourceValidationKind.Invalid, "slug_invalid"));
        return trimmed;
    }
}
