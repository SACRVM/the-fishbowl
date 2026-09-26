namespace Fishbowl.Core.Desktop;

// The Content-Security-Policy of a sandboxed app's frame document. The frame
// has an opaque origin (sandbox without allow-same-origin); this policy says
// what it may load and whom it may talk to: Fishbowl's own static files
// ('self' — the kit and the guest bridge), the app's origin, and exactly the
// servers its manifest declared under `connect` (nothing when it declared
// none). Every origin is re-checked against AppOrigins' strict shape before
// it lands in the header, so a stored value can never smuggle a directive.
public static class FrameCsp
{
    public static string Build(string appOrigin, IEnumerable<string>? connect)
    {
        if (!AppOrigins.IsSafe(appOrigin))
            throw new ArgumentException("Not a safe origin.", nameof(appOrigin));
        var connectList = (connect ?? Array.Empty<string>()).ToArray();
        foreach (var c in connectList)
        {
            if (!AppOrigins.IsSafe(c))
                throw new ArgumentException("Not a safe origin.", nameof(connect));
        }
        var connectSrc = connectList.Length == 0 ? "'none'" : string.Join(' ', connectList.Distinct(StringComparer.Ordinal));

        return string.Join("; ",
            "default-src 'none'",
            $"script-src 'self' {appOrigin}",
            $"style-src 'self' 'unsafe-inline' {appOrigin}",
            $"img-src 'self' data: blob: {appOrigin}",
            $"font-src 'self' {appOrigin}",
            $"media-src 'self' blob: {appOrigin}",
            $"connect-src {connectSrc}",
            "frame-ancestors 'self'",
            "form-action 'none'",
            "base-uri 'none'");
    }
}
