using System.Text;
using System.Text.RegularExpressions;

namespace Fishbowl.Core.Files;

public enum NameRuleSet { Windows, Posix, MacOS }

// "We follow the host": the file-name rules of the server's OS, plus a thin
// Fishbowl floor that applies everywhere (no control characters, no bidi
// overrides, no '\', the reserved .trash / .~fb- names). Pure functions, so
// both rule sets are testable on either OS. Every refusal names its `rule`.
public static partial class FileNameRules
{
    public const string TrashFolder = ".trash";
    public const string TempPrefix = ".~fb-";

    public static NameRuleSet ForHost()
        => OperatingSystem.IsWindows() ? NameRuleSet.Windows
         : OperatingSystem.IsMacOS() ? NameRuleSet.MacOS
         : NameRuleSet.Posix;

    public static string Describe(NameRuleSet set) => set switch
    {
        NameRuleSet.Windows => "windows",
        NameRuleSet.MacOS => "macos",
        _ => "posix",
    };

    public static int MaxSegment(NameRuleSet set) => 255;          // UTF-16 units (Windows/macOS), UTF-8 bytes (Linux)

    public static int MaxPath(NameRuleSet set, bool longPaths) => set switch
    {
        NameRuleSet.Windows => longPaths ? 32_767 : 259,
        NameRuleSet.MacOS => 1_023,
        _ => 4_095,
    };

    private static readonly string[] Devices =
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    [GeneratedRegex(@"^[^.~]{1,6}~\d{1,6}(\.[^.]{0,3})?$")]
    private static partial Regex ShortNameAlias();

    // Is `name` one of Fishbowl's own reserved names at this position?
    public static bool IsReserved(string name, bool atRoot, bool caseSensitive)
    {
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (name.StartsWith(TempPrefix, cmp)) return true;
        return atRoot && string.Equals(name, TrashFolder, cmp);
    }

    // A name Fishbowl is about to CREATE (upload, mkdir, rename/copy target).
    public static (string Rule, string Message)? Check(string name, NameRuleSet set, bool atRoot, bool caseSensitive)
        => Evaluate(name, set, atRoot, caseSensitive, forCreate: true);

    // A segment that ADDRESSES an existing entry. Looser: a name that already
    // exists on disk (created out of band) stays reachable even if Fishbowl
    // wouldn't create it — only what the host can't hold, or what would
    // alias another entry, is refused.
    public static (string Rule, string Message)? CheckAddress(string name, NameRuleSet set, bool atRoot, bool caseSensitive)
        => Evaluate(name, set, atRoot, caseSensitive, forCreate: false);

    private static (string Rule, string Message)? Evaluate(string name, NameRuleSet set, bool atRoot, bool caseSensitive, bool forCreate)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ("empty", "A name can't be empty.");
        if (name is "." or "..")
            return ("dot_name", $"\"{name}\" isn't a usable name.");
        if (name.Contains('/') || name.Contains('\\'))
            return ("separator", "Names can't contain / or \\.");
        if (IsReserved(name, atRoot, caseSensitive))
            return ("reserved_fishbowl", $"\"{name}\" is reserved by Fishbowl.");

        var hostName = Describe(set) switch { "windows" => "Windows", "macos" => "macOS", _ => "Linux" };

        foreach (var c in name)
        {
            if (c < 0x20 || c == 0x7F || c is >= '\u0080' and <= '\u009F')
            {
                if (forCreate || set == NameRuleSet.Windows)
                    return ("control_char", "Names can't contain control characters.");
            }
            if (forCreate && c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩')
                return ("bidi_override", "Names can't contain text-direction override characters.");
        }

        if (set == NameRuleSet.Windows)
        {
            foreach (var c in name)
                if (c is '<' or '>' or ':' or '"' or '|' or '?' or '*')
                    return ("forbidden_char", $"\"{c}\" isn't allowed in names on this server ({hostName}).");

            var dot = name.IndexOf('.');
            var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
            foreach (var d in Devices)
                if (string.Equals(stem, d, StringComparison.OrdinalIgnoreCase))
                    return ("reserved_device", $"\"{name}\" is a reserved name on this server ({hostName}).");

            if (name.EndsWith('.') || name.EndsWith(' '))
                return ("trailing_dot_space", $"Names can't end with a dot or a space on this server ({hostName}).");

            if (ShortNameAlias().IsMatch(name))
                return ("short_name_alias", $"\"{name}\" looks like a short-name alias on this server ({hostName}).");
        }
        else if (set == NameRuleSet.MacOS && name.Contains(':'))
        {
            return ("forbidden_char", $"\":\" isn't allowed in names on this server ({hostName}).");
        }

        if (forCreate)
        {
            var length = set == NameRuleSet.Posix ? Encoding.UTF8.GetByteCount(name) : name.Length;
            if (length > MaxSegment(set))
                return ("segment_too_long", $"That name is too long for this server ({hostName}, max {MaxSegment(set)}).");
        }
        return null;
    }
}
