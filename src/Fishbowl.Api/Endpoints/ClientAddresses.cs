using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace Fishbowl.Api.Endpoints;

/// <summary>
/// "Connect an agent" on the API keys page: where a program reaches this
/// server, and whether a claude.ai connector can. A connector is called from
/// Anthropic's servers, not from the browser, so it needs Fishbowl at an https
/// address the internet reaches — which neither the browser nor the server can
/// test; both go by the name.
/// </summary>
public static class ClientAddresses
{
    // Names only this computer or its network resolve.
    private static readonly string[] LocalSuffixes = [".localhost", ".local", ".lan", ".home.arpa", ".internal"];

    /// <summary>
    /// A connector can be offered: the request came over https to a host that
    /// isn't <see cref="IsLocal"/>.
    /// </summary>
    public static bool ConnectorPossible(string scheme, HostString host) =>
        scheme == "https" && !IsLocal(host.Host);

    /// <summary>
    /// True for a host only this computer or its network reaches: loopback,
    /// the private and link-local ranges (and 100.64/10, where Tailscale puts
    /// a tailnet), a name without a dot, or one of the local-only suffixes.
    /// </summary>
    public static bool IsLocal(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        if (h.Length == 0) return true;
        if (IPAddress.TryParse(h, out var ip)) return IsLocal(ip);
        return !h.Contains('.') || LocalSuffixes.Any(s => h.EndsWith(s, StringComparison.Ordinal));
    }

    private static bool IsLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] is 0 or 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal || ip.Equals(IPAddress.IPv6Any);
    }

    /// <summary>
    /// The origin a program should use. Mostly the request's own; but the dev
    /// host's https port carries the self-signed dev certificate, which Claude
    /// Code refuses — so when the request came over https to loopback and the
    /// server also listens on plain http there, that is the program's way in.
    /// </summary>
    public static string ProgramOrigin(string scheme, HostString host, IEnumerable<string> serverAddresses)
    {
        var origin = $"{scheme}://{host}";
        if (scheme != "https" || !IsLoopback(host.Host)) return origin;
        foreach (var address in serverAddresses)
        {
            // Kestrel reports wildcard binds as "*" or "+", which Uri can't read.
            var normal = address.Replace("://*", "://0.0.0.0").Replace("://+", "://0.0.0.0");
            if (!Uri.TryCreate(normal, UriKind.Absolute, out var uri) || uri.Scheme != "http") continue;
            if (IsLoopback(uri.Host) || uri.Host is "0.0.0.0" or "[::]")
                return $"http://{host.Host}:{uri.Port}";
        }
        return origin;
    }

    private static bool IsLoopback(string host)
    {
        var h = host.Trim('[', ']').ToLowerInvariant();
        return h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal)
            || (IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip));
    }
}
