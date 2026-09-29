using System.Security.Cryptography;
using System.Text;

namespace Fishbowl.Core.Models;

// An invitation link to a space (system schema v14, `space_invites`): one
// use, an expiry and the role it hands out. Only the token's SHA-256 is
// stored — the link itself is shown once, to whoever made it.
public class SpaceInvite
{
    public string Id { get; set; } = string.Empty;
    public string SpaceId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? UsedBy { get; set; }
    public DateTime? UsedAt { get; set; }
}

// Why an invitation link doesn't work (anymore).
public enum InviteProblem { None, Unknown, Used, Expired }

public sealed record InviteRedemption(string SpaceId, SpaceRole Role);

public static class SpaceInvites
{
    public const int DefaultDays = 7;
    public const int MaxDays = 30;

    // The link's path: /invite/<token>.
    public const string PathPrefix = "/invite/";

    // 256 bits, base64url — the token is the capability.
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    // A token that can't be one of ours is refused before any lookup.
    public static bool LooksValid(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
}
