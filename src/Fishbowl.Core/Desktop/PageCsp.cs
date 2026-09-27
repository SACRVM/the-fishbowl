namespace Fishbowl.Core.Desktop;

// The Content-Security-Policy of Fishbowl's own pages — the SPA shell and the
// server-rendered /login, /setup, /pending. The point is script-src: only
// Fishbowl's own files run (no inline script, no eval), so HTML that slips
// past sanitising can't execute. Trusted apps run in the page's realm, so
// their origins join script-src.
//
// connect-src stays open to https (and loopback http by name, which AppOrigins
// accepts for app development; CSP can't express an IPv6 literal): installing an app reads its app.json and
// hashes its entry in this browser, from a URL the owner types at runtime —
// a header can't know it in advance. img-src takes https for images in notes.
// Styles allow inline: the kit's components carry their <style> in shadow
// roots and markup uses style attributes.
public static class PageCsp
{
    private const string Loopback = "http://localhost:* http://127.0.0.1:*";

    /// <param name="trustedOrigins">Origins of the viewer's trusted apps;
    /// anything that isn't a safe origin is dropped.</param>
    public static string Build(IEnumerable<string>? trustedOrigins = null)
    {
        var trusted = (trustedOrigins ?? Array.Empty<string>())
            .Where(AppOrigins.IsSafe)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var scriptSrc = string.Join(' ', new[] { "'self'" }.Concat(trusted));

        return string.Join("; ",
            "default-src 'self'",
            $"script-src {scriptSrc}",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data: blob: https:",
            "font-src 'self' data:",
            "media-src 'self' blob:",
            $"connect-src 'self' https: {Loopback}",
            "frame-src 'self' blob:",
            "worker-src 'self' blob:",
            "object-src 'none'",
            "base-uri 'none'",
            "form-action 'self'",
            "frame-ancestors 'self'");
    }
}
