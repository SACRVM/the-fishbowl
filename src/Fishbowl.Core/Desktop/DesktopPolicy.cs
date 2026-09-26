namespace Fishbowl.Core.Desktop;

// Instance-wide limits on installing apps (system_config, set by an admin;
// docs/superpowers/specs/2026-09-26-desktop-apps-design.md § Policy).
//
//   Apps:Install        who may install into their personal desktop
//                       (everyone | admins | off). A space desktop is always
//                       its owner's call, and only `off` stops it.
//   Apps:Trusted        who may pick the trusted mode (everyone | admins |
//                       off) — personal desktops only, always.
//   Apps:AllowedOrigins optional comma-separated origin allow-list (empty =
//                       any origin).
//   Apps:StoreOwners    GitHub owners the App Store tab lists.
//   Apps:StoreTopic     the repo topic that marks an app.
public static class DesktopPolicy
{
    public const string InstallKey = "Apps:Install";
    public const string TrustedKey = "Apps:Trusted";
    public const string AllowedOriginsKey = "Apps:AllowedOrigins";
    public const string StoreOwnersKey = "Apps:StoreOwners";
    public const string StoreTopicKey = "Apps:StoreTopic";

    public const string Everyone = "everyone";
    public const string Admins = "admins";
    public const string Off = "off";

    public static readonly string[] Levels = { Everyone, Admins, Off };

    public const string DefaultStoreOwners = "SACRVM";
    public const string DefaultStoreTopic = "sacrvm-app";

    public static string ParseLevel(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            Admins => Admins,
            Off => Off,
            _ => Everyone,
        };

    public static bool Allows(string level, bool isAdmin) => level switch
    {
        Off => false,
        Admins => isAdmin,
        _ => true,
    };

    // Normalised origins ("https://owner.github.io"); entries that aren't a
    // valid origin are dropped (the config validator refuses them on write).
    public static IReadOnlyList<string> ParseOrigins(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(o => AppOrigins.TryNormalize(o, out var n) ? n : null)
                .Where(o => o is not null)
                .Select(o => o!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    public static bool IsOriginAllowed(string origin, IReadOnlyList<string> allowList) =>
        allowList.Count == 0 || allowList.Contains(origin, StringComparer.Ordinal);

    public static IReadOnlyList<string> ParseList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}
