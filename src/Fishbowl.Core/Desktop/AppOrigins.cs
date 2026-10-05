using System.Text.RegularExpressions;

namespace Fishbowl.Core.Desktop;

// Where an app may come from. An origin is scheme + host (+ port), nothing
// else — no path, query, fragment, user info or wildcard. HTTPS only; plain
// HTTP is accepted for loopback hosts so an app can be developed (and
// tested) against a local server — and for Fishbowl's own origin when it
// runs without TLS (anyHttp), which only its own space apps live on.
public static partial class AppOrigins
{
    // What survives into a CSP source list: no whitespace, quotes, semicolons
    // or commas can ever get past this.
    [GeneratedRegex(@"^https?://[a-z0-9.-]+(:[0-9]{1,5})?$|^http://\[::1\](:[0-9]{1,5})?$")]
    private static partial Regex OriginShape();

    public static bool IsLoopback(string host) =>
        host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    // Accepts an origin or any absolute URL; returns its origin, lower-case.
    public static bool TryNormalize(string? value, out string origin) => TryNormalize(value, out origin, anyHttp: false);

    public static bool TryNormalize(string? value, out string origin, bool anyHttp)
    {
        origin = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;
        return TryOriginOf(uri, out origin, anyHttp);
    }

    public static bool TryOriginOf(Uri uri, out string origin) => TryOriginOf(uri, out origin, anyHttp: false);

    /// <param name="anyHttp">Plain HTTP on any host — only for Fishbowl's
    /// OWN origin (a LAN install without TLS serves its space apps that
    /// way), never for an app's.</param>
    public static bool TryOriginOf(Uri uri, out string origin, bool anyHttp)
    {
        origin = "";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        if (host.Length == 0) return false;
        if (scheme == "http")
        {
            if (!anyHttp && !IsLoopback(host)) return false;
        }
        else if (scheme != "https")
        {
            return false;
        }
        var hostPart = uri.HostNameType == UriHostNameType.IPv6 && !host.StartsWith('[') ? $"[{host}]" : host;
        var candidate = uri.IsDefaultPort ? $"{scheme}://{hostPart}" : $"{scheme}://{hostPart}:{uri.Port}";
        if (!IsSafe(candidate)) return false;
        origin = candidate;
        return true;
    }

    // Strict: the value must already BE an origin (no path beyond "/").
    public static bool TryParseExact(string? value, out string origin)
    {
        origin = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        return TryOriginOf(uri, out origin);
    }

    public static bool IsSafe(string origin) => OriginShape().IsMatch(origin);
}
