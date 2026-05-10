namespace Fishbowl.Core.Repositories;

public record DiscordLinkRedemption(string UserId);

// One-shot link codes that bind a logged-in Fishbowl user to a Discord
// account. The web UI mints a code, the user DMs it to the bot, the bot
// calls RedeemAsync to discover whose account this DM belongs to. Codes
// expire fast (10 min) and burn on first redemption — finding a stale
// code on a screen behind someone shouldn't open a side door.
public interface IDiscordLinkRepository
{
    // Generates a new short, human-typeable code, stores it, returns it.
    // Also returns expires_at so the API caller can show the user a
    // countdown without a second round-trip.
    Task<(string Code, DateTime ExpiresAt)> CreateCodeAsync(string userId, CancellationToken ct = default);

    // Atomic redeem: verifies the code is unredeemed and unexpired, marks
    // it redeemed, returns the bound user_id. Returns null on miss/stale —
    // the bot must not differentiate "wrong code" vs "expired" in its
    // reply to the Discord side (don't help an attacker enumerate).
    Task<DiscordLinkRedemption?> RedeemAsync(string code, CancellationToken ct = default);

    // Bulk-removes expired/unredeemed codes. Cheap; called from the
    // hosted service on startup so the table doesn't grow unbounded
    // for users who mint codes but never redeem.
    Task<int> PruneExpiredAsync(CancellationToken ct = default);
}
