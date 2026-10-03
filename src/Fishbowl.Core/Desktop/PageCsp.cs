namespace Fishbowl.Core.Desktop;

// The Content-Security-Policy of Fishbowl's own pages — the SPA shell and the
// server-rendered /login, /setup, /pending. The point is script-src: only
// Fishbowl's own files run (no inline script, no eval), so HTML that slips
// past sanitising can't execute. Installed apps run in their own frame, never
// in the page, so nobody else's origin joins it.
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

    public static string Build()
    {
        return string.Join("; ",
            "default-src 'self'",
            "script-src 'self'",
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
