using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace Fishbowl.Api.Accounts;

// Password guessing is throttled twice: per address (the host's rate-limit
// policy, partitioned by AddressKey) and per account name here, so spreading
// the guesses over many addresses buys no more of them. Login and
// change-password share the budget. The host sets the numbers (Testing is
// permissive).
public sealed class SignInThrottle : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public SignInThrottle(int permitLimit, TimeSpan window)
    {
        _limiter = PartitionedRateLimiter.Create<string, string>(name =>
            RateLimitPartition.GetFixedWindowLimiter(name, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = 0,
            }));
    }

    // One attempt at this username; false once its budget for the window is
    // spent. Known or not — the answer must not tell which.
    public bool TryAcquire(string? username)
    {
        var key = (username ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length > 64) key = key[..64];   // usernames are ≤ 64; a longer one is a guess at nothing
        using var lease = _limiter.AttemptAcquire(key);
        return lease.IsAcquired;
    }

    // The rate-limit partition of a client address: IPv4 (incl. IPv4-mapped
    // IPv6) as it is, IPv6 by its /64 — one host or line usually holds a
    // whole /64, so per-address buckets would be free for the asking.
    public static string AddressKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    public void Dispose() => _limiter.Dispose();
}
