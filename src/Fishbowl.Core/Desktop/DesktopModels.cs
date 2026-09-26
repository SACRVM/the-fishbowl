using System.Text.RegularExpressions;

namespace Fishbowl.Core.Desktop;

public static class AppModes
{
    // The default: an opaque-origin frame, host-granted capabilities, the
    // entry pinned by its SRI hash.
    public const string Sandboxed = "sandboxed";
    // Same-realm, acts as the user, follows the origin's current code.
    // Personal desktops only.
    public const string Trusted = "trusted";

    public static bool IsValid(string? mode) => mode is Sandboxed or Trusted;
}

public static class AppPermissions
{
    public const string Files = "files";
    public const string Identity = "identity";

    public static readonly string[] All = { Files, Identity };
}

public static partial class DesktopTiles
{
    public const string Medium = "medium";
    public const string Wide = "wide";
    public const string Large = "large";

    public static readonly string[] Sizes = { Medium, Wide, Large };

    // "builtin:notes", "app:color-bucket" — the registry key the UI uses.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9:._-]{0,99}$")]
    private static partial Regex KeyShape();

    public static bool IsValidKey(string? key) => key is not null && KeyShape().IsMatch(key);
}

// One tile's arrangement. Position is REAL like todos: a drag writes one row,
// the midpoint of its new neighbours.
public sealed record DesktopTile(string Key, double? Position, string? Size, string? Color, bool Hidden);

// An installed app as stored. Manifest is the JSON the owner's browser read
// at install (or at the last re-pin); the server never fetched it.
public sealed record DesktopApp(
    string Id,
    string ManifestUrl,
    string Manifest,
    string Origin,
    string EntryUrl,
    string? EntryIntegrity,
    string? Version,
    string Mode,
    IReadOnlyList<string> Granted,
    DateTime InstalledAt,
    DateTime UpdatedAt);

public static partial class AppDataFolders
{
    public const string Root = "Apps";

    [GeneratedRegex(@"[^A-Za-z0-9 ._-]+")]
    private static partial Regex Unsafe();

    // The app's own folder in the workspace's Files: "Apps/<name>". The name
    // is reduced to characters every host file system accepts; an empty or
    // dot-only result falls back to the app id (which is always safe).
    public static string For(string name, string id)
    {
        var clean = Unsafe().Replace(name ?? "", " ").Trim().TrimEnd('.').Trim();
        if (clean.Length > 64) clean = clean[..64].TrimEnd();
        if (clean.Length == 0 || clean.All(c => c == '.')) clean = id;
        return Root + "/" + clean;
    }
}

public sealed class DesktopValidationException : Exception
{
    public string Code { get; }
    public string? Field { get; }

    public DesktopValidationException(string code, string message, string? field = null) : base(message)
    {
        Code = code;
        Field = field;
    }
}
