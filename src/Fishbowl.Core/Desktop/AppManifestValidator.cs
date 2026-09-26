using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fishbowl.Core.Desktop;

// What the server checks about a manifest the owner's browser posted
// (sac.apps.inspect read it — the server never fetches a foreign URL).
// The kit's contract: id, name, kind, tag, entry; plus Fishbowl's
// capability fields (permissions, connect, opens). The entry must live on
// the manifest's own origin, so a frame's script-src names exactly one
// foreign origin.
public sealed record ValidatedManifest(
    string Id,
    string Name,
    string Kind,
    string Tag,
    string Origin,
    string ManifestUrl,
    string EntryUrl,
    string? Version,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Connect,
    IReadOnlyList<string> Opens,
    string Json);

public static partial class AppManifestValidator
{
    public const int MaxManifestBytes = 64 * 1024;
    public const int MaxListEntries = 50;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex IdShape();

    // A valid custom element name, restricted to ASCII: starts with a
    // letter, contains a hyphen.
    [GeneratedRegex(@"^[a-z][a-z0-9._]*-[a-z0-9._-]*$")]
    private static partial Regex TagShape();

    [GeneratedRegex(@"^[a-z0-9!#$&^_.+-]{1,64}/([a-z0-9!#$&^_.+-]{1,64}|\*)$|^\.[a-z0-9][a-z0-9_-]{0,15}$")]
    private static partial Regex OpensShape();

    // sha256-/sha384-/sha512- + standard base64 of the digest.
    [GeneratedRegex(@"^(sha256-[A-Za-z0-9+/]{43}=|sha384-[A-Za-z0-9+/]{64}|sha512-[A-Za-z0-9+/]{86}==)$")]
    private static partial Regex IntegrityShape();

    private static readonly string[] ReservedTags =
    {
        "annotation-xml", "color-profile", "font-face", "font-face-src",
        "font-face-uri", "font-face-format", "font-face-name", "missing-glyph",
    };

    public static bool IsValidIntegrity(string? value) => value is not null && IntegrityShape().IsMatch(value);

    public static ValidatedManifest Validate(string? manifestUrl, JsonElement manifest)
    {
        if (!Uri.TryCreate(manifestUrl?.Trim(), UriKind.Absolute, out var manifestUri)
            || !AppOrigins.TryOriginOf(manifestUri, out var origin))
            throw Invalid("invalid_manifest_url", "The manifest URL must be an absolute https URL.", "manifestUrl");
        if (manifestUri.Fragment.Length > 0)
            throw Invalid("invalid_manifest_url", "The manifest URL can't carry a fragment.", "manifestUrl");

        if (manifest.ValueKind != JsonValueKind.Object)
            throw Invalid("invalid_manifest", "The manifest must be a JSON object.", "manifest");
        var json = manifest.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxManifestBytes)
            throw Invalid("manifest_too_large", "The manifest is larger than 64 KB.", "manifest");

        var id = RequiredString(manifest, "id");
        if (!IdShape().IsMatch(id))
            throw Invalid("invalid_manifest", "id must be lower-case letters, digits and hyphens (max 64).", "id");

        var name = RequiredString(manifest, "name").Trim();
        if (name.Length is 0 or > 80)
            throw Invalid("invalid_manifest", "name must be 1–80 characters.", "name");

        var kind = RequiredString(manifest, "kind");
        if (kind is not ("window" or "view"))
            throw Invalid("invalid_manifest", "kind must be \"window\" or \"view\".", "kind");

        var tag = RequiredString(manifest, "tag");
        if (!TagShape().IsMatch(tag) || ReservedTags.Contains(tag))
            throw Invalid("invalid_manifest", "tag must be a valid custom element name (lower-case, with a hyphen).", "tag");

        var entry = RequiredString(manifest, "entry");
        if (!Uri.TryCreate(manifestUri, entry, out var entryUri)
            || !AppOrigins.TryOriginOf(entryUri, out var entryOrigin)
            || entryOrigin != origin)
            throw Invalid("invalid_manifest", "entry must resolve to a script on the manifest's own origin.", "entry");
        if (entryUri.Fragment.Length > 0)
            throw Invalid("invalid_manifest", "entry can't carry a fragment.", "entry");

        var version = OptionalString(manifest, "version", 32);
        OptionalString(manifest, "icon", 64);
        OptionalString(manifest, "description", 500);

        var permissions = StringList(manifest, "permissions");
        foreach (var p in permissions)
        {
            if (!AppPermissions.All.Contains(p))
                throw Invalid("invalid_manifest", $"Unknown permission \"{p}\" (known: {string.Join(", ", AppPermissions.All)}).", "permissions");
        }

        var connect = new List<string>();
        foreach (var c in StringList(manifest, "connect"))
        {
            if (!AppOrigins.TryParseExact(c, out var normalized))
                throw Invalid("invalid_manifest", "Every connect entry must be an https origin (scheme and host only).", "connect");
            if (!connect.Contains(normalized)) connect.Add(normalized);
        }

        var opens = StringList(manifest, "opens").Select(o => o.ToLowerInvariant()).ToArray();
        foreach (var o in opens)
        {
            if (!OpensShape().IsMatch(o))
                throw Invalid("invalid_manifest", "opens entries are MIME types (type/sub, type/*) or extensions (.ext).", "opens");
        }

        return new ValidatedManifest(id, name, kind, tag, origin, manifestUri.AbsoluteUri, entryUri.AbsoluteUri,
            version, permissions.Distinct().ToArray(), connect, opens.Distinct().ToArray(), json);
    }

    // The capabilities the owner granted must be ones the manifest asked for.
    public static IReadOnlyList<string> ValidateGrants(IEnumerable<string>? granted, IReadOnlyList<string> permissions)
    {
        var list = (granted ?? Array.Empty<string>()).Distinct().ToArray();
        foreach (var g in list)
        {
            if (!permissions.Contains(g))
                throw Invalid("invalid_grant", $"\"{g}\" wasn't asked for by the manifest.", "granted");
        }
        return list;
    }

    private static DesktopValidationException Invalid(string code, string message, string field) => new(code, message, field);

    private static string RequiredString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
            throw Invalid("invalid_manifest", $"{name} is required.", name);
        return v.GetString()!;
    }

    private static string? OptionalString(JsonElement obj, string name, int max)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String)
            throw Invalid("invalid_manifest", $"{name} must be a string.", name);
        var s = v.GetString()!;
        if (s.Length > max)
            throw Invalid("invalid_manifest", $"{name} is longer than {max} characters.", name);
        return s;
    }

    private static string[] StringList(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return Array.Empty<string>();
        if (v.ValueKind != JsonValueKind.Array)
            throw Invalid("invalid_manifest", $"{name} must be an array of strings.", name);
        if (v.GetArrayLength() > MaxListEntries)
            throw Invalid("invalid_manifest", $"{name} has more than {MaxListEntries} entries.", name);
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw Invalid("invalid_manifest", $"{name} must be an array of strings.", name);
            list.Add(item.GetString()!.Trim());
        }
        return list.ToArray();
    }
}
