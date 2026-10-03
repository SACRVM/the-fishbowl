using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fishbowl.Core.Desktop;

// A space's own apps (space-apps spec phase 4): each folder `.apps/<folder>/`
// with an `app.json` is an app of that space — a tile on its desktop, opened
// in the kit's sandboxed frame, never pinned (the code is the space's, a
// Designer changes it, the next open runs it).
//
// The frame has an opaque origin and sends no cookies, so the code is served
// under an unguessable URL: /apps/code/<spaceId>/<folder>/<sig>/<file>, sig =
// HMAC-SHA256 over "<spaceId>/<folder>" with the instance's Apps:CodeKey. A
// member gets it with the desktop; whoever holds it can read that app's code
// — which every member may read anyway — and nothing else: data reaches an
// app only through the bridge, as the signed-in user.
public static partial class SpaceApps
{
    public const string IdPrefix = "space.";
    public const string ManifestFile = "app.json";
    public const string CodeKeyConfig = "Apps:CodeKey";
    public const int MaxManifestBytes = 64 * 1024;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex FolderShape();

    // A custom element name: lower-case, with a dash.
    [GeneratedRegex("^[a-z][a-z0-9]*-[a-z0-9-]*$")]
    private static partial Regex TagShape();

    // A file inside the app's folder: plain segments, no dot-dot, no backslash.
    [GeneratedRegex(@"^[A-Za-z0-9_-][A-Za-z0-9._-]*(/[A-Za-z0-9_-][A-Za-z0-9._-]*)*$")]
    private static partial Regex FileShape();

    public static bool IsFolder(string? folder) => folder is not null && FolderShape().IsMatch(folder);

    public static bool IsFile(string? path) => path is not null && path.Length <= 200 && FileShape().IsMatch(path);

    public static string IdOf(string folder) => IdPrefix + folder;

    /// <summary>The folder of a space app id ("space.booking" → "booking"), or null.</summary>
    public static string? FolderOf(string? id) =>
        id is not null && id.StartsWith(IdPrefix, StringComparison.Ordinal) && IsFolder(id[IdPrefix.Length..])
            ? id[IdPrefix.Length..] : null;

    public static string Sign(byte[] key, string spaceId, string folder)
    {
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{spaceId}/{folder}"));
        return Convert.ToBase64String(mac, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool Verify(byte[] key, string spaceId, string folder, string? sig) =>
        sig is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(key, spaceId, folder)), Encoding.ASCII.GetBytes(sig));

    // What the code route serves, by extension — scripts, styles, data,
    // images and fonts. Never HTML: these answers come from Fishbowl's own
    // origin, and the frame document is Fishbowl's (the kit's harness).
    public static readonly IReadOnlyDictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".woff2"] = "font/woff2",
        [".woff"] = "font/woff",
        [".ttf"] = "font/ttf",
    };

    public static string? TypeOf(string path) => Types.TryGetValue(Path.GetExtension(path), out var t) ? t : null;

    /// <summary>
    /// Reads an app.json. Null when it isn't an app: no `tag` that is a custom
    /// element name, or an `entry` that isn't a .js/.mjs file in the folder.
    /// </summary>
    public static SpaceAppManifest? Parse(string folder, JsonElement json) => Parse(folder, json, out _);

    /// <summary>As <see cref="Parse(string, JsonElement)"/>, saying why not (for the error store).</summary>
    public static SpaceAppManifest? Parse(string folder, JsonElement json, out string? problem)
    {
        problem = null;
        if (json.ValueKind != JsonValueKind.Object) { problem = "app.json must be a JSON object."; return null; }
        string? Str(string name, int max) =>
            json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= max
                ? v.GetString() : null;

        var tag = Str("tag", 100);
        if (tag is null || !TagShape().IsMatch(tag))
        {
            problem = "app.json needs \"tag\": the app's custom element name, lower-case with a dash (e.g. \"booking-app\").";
            return null;
        }
        var entry = Str("entry", 200) ?? Str("src", 200) ?? "app.js";
        if (entry.StartsWith("./", StringComparison.Ordinal)) entry = entry[2..];
        if (!IsFile(entry) || !(entry.EndsWith(".js", StringComparison.Ordinal) || entry.EndsWith(".mjs", StringComparison.Ordinal)))
        {
            problem = $"\"entry\" must be a .js or .mjs file inside the app's folder (got \"{entry}\").";
            return null;
        }
        var name = Str("name", 100);
        return new SpaceAppManifest(
            Folder: folder,
            Name: string.IsNullOrWhiteSpace(name) ? folder : name.Trim(),
            Tag: tag,
            Entry: entry,
            Version: Str("version", 40),
            Description: Str("description", 500),
            Icon: Str("icon", 40),
            Width: Str("width", 20),
            Height: Str("height", 20));
    }
}

public sealed record SpaceAppManifest(
    string Folder, string Name, string Tag, string Entry,
    string? Version, string? Description, string? Icon, string? Width, string? Height);
