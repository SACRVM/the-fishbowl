using System.Security.Cryptography;
using Dapper;
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class DiscordLinkRepository : IDiscordLinkRepository
{
    // 8 base32-ish chars (no I/O/0/1) => ~33 bits of entropy. The user has to
    // type this on their phone, so we trade some entropy for legibility. The
    // 10-minute expiry makes brute-force unrealistic at any reasonable rate.
    private const int CodeLength = 8;
    private static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(10);
    private static readonly char[] Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<DiscordLinkRepository> _logger;

    public DiscordLinkRepository(
        DatabaseFactory dbFactory,
        ILogger<DiscordLinkRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<DiscordLinkRepository>.Instance;
    }

    public async Task<(string Code, DateTime ExpiresAt)> CreateCodeAsync(string userId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();

        // Replace any pending codes the user already has — they only need one
        // at a time, and it keeps the table tidy if a user clicks "generate"
        // twice in a row.
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM discord_link_codes WHERE user_id = @userId AND redeemed_at IS NULL",
            new { userId }, cancellationToken: ct));

        var now = DateTime.UtcNow;
        var expiresAt = now + CodeTtl;

        // Loop in case of an alphabet collision (vanishingly unlikely, but
        // PRIMARY KEY would throw and that'd surface as a 500 instead of a
        // retried code).
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var code = GenerateCode();
            try
            {
                await db.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO discord_link_codes (code, user_id, created_at, expires_at, redeemed_at)
                    VALUES (@code, @userId, @createdAt, @expiresAt, NULL)",
                    new
                    {
                        code,
                        userId,
                        createdAt = now.ToString("o"),
                        expiresAt = expiresAt.ToString("o"),
                    }, cancellationToken: ct));

                _logger.LogInformation("Discord link code minted for user {UserId}", userId);
                return (code, expiresAt);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // PRIMARY KEY collision — try a fresh code.
            }
        }

        throw new InvalidOperationException("Failed to mint a unique Discord link code after 5 attempts.");
    }

    public async Task<DiscordLinkRedemption?> RedeemAsync(string code, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        using var tx = db.BeginTransaction();

        var nowIso = DateTime.UtcNow.ToString("o");

        // Single-row update keyed by the unredeemed/unexpired predicate keeps
        // the redeem atomic — even if two DMs land at the same time, only one
        // wins.
        var userId = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(@"
            SELECT user_id FROM discord_link_codes
            WHERE code = @code AND redeemed_at IS NULL AND expires_at > @now",
            new { code, now = nowIso }, transaction: tx, cancellationToken: ct));

        if (string.IsNullOrEmpty(userId))
        {
            tx.Rollback();
            return null;
        }

        await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE discord_link_codes SET redeemed_at = @now WHERE code = @code",
            new { code, now = nowIso }, transaction: tx, cancellationToken: ct));

        tx.Commit();
        _logger.LogInformation("Discord link code redeemed for user {UserId}", userId);
        return new DiscordLinkRedemption(userId);
    }

    public async Task<int> PruneExpiredAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var nowIso = DateTime.UtcNow.ToString("o");
        var affected = await db.ExecuteAsync(new CommandDefinition(@"
            DELETE FROM discord_link_codes
            WHERE redeemed_at IS NOT NULL OR expires_at <= @now",
            new { now = nowIso }, cancellationToken: ct));
        if (affected > 0)
            _logger.LogDebug("Pruned {Count} stale Discord link codes", affected);
        return affected;
    }

    private static string GenerateCode()
    {
        Span<byte> bytes = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return new string(chars);
    }
}
