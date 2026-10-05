using System.Text.RegularExpressions;

namespace Fishbowl.Core.Desktop;

// The Content-Security-Policy of a sandboxed app's frame document — the
// kit's harness contract (kit/js/lib/app-bridge.js, THE HARNESS) as a real
// header. The frame has an opaque origin (sandbox without allow-same-origin);
// this policy says what it may load and whom it may talk to: the host's kit
// (scripts, styles, fonts), the folder of the app's entry, and exactly the
// servers its manifest declared under `connect` (plus blob:/data:, which the
// guest runtime uses for bytes). Every origin and the entry folder are
// re-checked against a strict shape before they land in the header, so a
// stored value can never smuggle a directive.
public static partial class FrameCsp
{
    // A URL path the header may carry: no whitespace, quotes, separators.
    [GeneratedRegex(@"^[A-Za-z0-9._~%!$&()*+=:@/-]*$")]
    private static partial Regex SafePath();

    /// <param name="appOrigin">The app's origin (its manifest's).</param>
    /// <param name="connect">The origins the app declared (and was granted).</param>
    /// <param name="hostOrigin">Fishbowl's own origin; the kit paths hang off it.
    /// Null writes 'self' (tests, and a host that can't tell).</param>
    /// <param name="entryUrl">The app's entry script; its folder is what the
    /// frame may load app files from. Null allows the whole app origin.</param>
    public static string Build(string appOrigin, IEnumerable<string>? connect, string? hostOrigin = null, string? entryUrl = null)
    {
        if (!AppOrigins.IsSafe(appOrigin))
            throw new ArgumentException("Not a safe origin.", nameof(appOrigin));
        if (hostOrigin is not null && !AppOrigins.IsSafe(hostOrigin))
            throw new ArgumentException("Not a safe origin.", nameof(hostOrigin));
        var connectList = (connect ?? Array.Empty<string>()).ToArray();
        foreach (var c in connectList)
        {
            if (!AppOrigins.IsSafe(c))
                throw new ArgumentException("Not a safe origin.", nameof(connect));
        }

        var appDir = appOrigin;
        if (entryUrl is not null)
        {
            // An app's entry is https (loopback http for development) — or,
            // for a space's own app, on Fishbowl's own origin, plain HTTP too
            // on a LAN install without TLS. The entry stays on appOrigin.
            if (!Uri.TryCreate(entryUrl, UriKind.Absolute, out var entry)
                || !(AppOrigins.TryOriginOf(entry, out var entryOrigin)
                     || (hostOrigin is not null && AppOrigins.TryOriginOf(entry, out entryOrigin, anyHttp: true) && entryOrigin == hostOrigin))
                || entryOrigin != appOrigin)
                throw new ArgumentException("The entry must live on the app's origin.", nameof(entryUrl));
            var folder = entry.AbsolutePath[..(entry.AbsolutePath.LastIndexOf('/') + 1)];
            if (!SafePath().IsMatch(folder))
                throw new ArgumentException("The entry's folder can't go into a header.", nameof(entryUrl));
            appDir = appOrigin + folder;
        }

        string Kit(string sub) => hostOrigin is null ? "'self'" : $"{hostOrigin}/kit/{sub}/";
        var connectSrc = string.Join(' ', connectList.Distinct(StringComparer.Ordinal).Concat(new[] { "blob:", "data:" }));
        var ancestors = hostOrigin ?? "'self'";

        return string.Join("; ",
            "default-src 'none'",
            $"script-src {Kit("js")} {appDir}",
            $"style-src {Kit("css")} {appDir} 'unsafe-inline'",
            $"font-src {Kit("fonts")} {appDir}",
            $"img-src {appDir} blob: data:",
            $"media-src {appDir} blob: data:",
            $"connect-src {connectSrc}",
            $"frame-ancestors {ancestors}",
            "form-action 'none'",
            "base-uri 'none'");
    }
}
