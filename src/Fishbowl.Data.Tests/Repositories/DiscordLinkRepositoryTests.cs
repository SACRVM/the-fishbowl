using Dapper;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

public class DiscordLinkRepositoryTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;
    private readonly DiscordLinkRepository _repo;
    private const string Alice = "u_alice";

    public DiscordLinkRepositoryTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_link_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
        _repo = new DiscordLinkRepository(_factory);

        using var db = _factory.CreateSystemConnection();
        var now = DateTime.UtcNow.ToString("o");
        db.Execute(
            "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
            new { id = Alice, n = "Alice", e = "a@a", now });
    }

    [Fact]
    public async Task CreateCodeAsync_GeneratesNonEmptyCodeAndFutureExpiry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, expiresAt) = await _repo.CreateCodeAsync(Alice, ct);

        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.True(expiresAt > DateTime.UtcNow.AddMinutes(8));
        Assert.True(expiresAt <= DateTime.UtcNow.AddMinutes(11));
        // Codes are uppercase by construction so the bot can normalise input.
        Assert.Equal(code, code.ToUpperInvariant());
    }

    [Fact]
    public async Task CreateCodeAsync_ReplacesEarlierUnredeemedCodeForSameUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var (firstCode, _) = await _repo.CreateCodeAsync(Alice, ct);
        var (secondCode, _) = await _repo.CreateCodeAsync(Alice, ct);

        Assert.NotEqual(firstCode, secondCode);

        // First code should now be invalid — only one live code per user.
        Assert.Null(await _repo.RedeemAsync(firstCode, ct));
        var redeemed = await _repo.RedeemAsync(secondCode, ct);
        Assert.NotNull(redeemed);
        Assert.Equal(Alice, redeemed!.UserId);
    }

    [Fact]
    public async Task RedeemAsync_HappyPath_ReturnsUserId()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _) = await _repo.CreateCodeAsync(Alice, ct);

        var redeemed = await _repo.RedeemAsync(code, ct);

        Assert.NotNull(redeemed);
        Assert.Equal(Alice, redeemed!.UserId);
    }

    [Fact]
    public async Task RedeemAsync_TwoTimes_OnlyFirstSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _) = await _repo.CreateCodeAsync(Alice, ct);

        Assert.NotNull(await _repo.RedeemAsync(code, ct));
        Assert.Null(await _repo.RedeemAsync(code, ct));
    }

    [Fact]
    public async Task RedeemAsync_UnknownCode_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await _repo.RedeemAsync("DEADBEEF", ct));
    }

    [Fact]
    public async Task RedeemAsync_ExpiredCode_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _) = await _repo.CreateCodeAsync(Alice, ct);

        // Force the row's expires_at into the past — sidesteps having to wait
        // 10 minutes while still exercising the predicate path.
        using (var db = _factory.CreateSystemConnection())
        {
            db.Execute(
                "UPDATE discord_link_codes SET expires_at = @past WHERE code = @code",
                new { past = DateTime.UtcNow.AddMinutes(-1).ToString("o"), code });
        }

        Assert.Null(await _repo.RedeemAsync(code, ct));
    }

    [Fact]
    public async Task PruneExpiredAsync_RemovesRedeemedAndExpired_KeepsLive()
    {
        var ct = TestContext.Current.CancellationToken;

        var (live, _) = await _repo.CreateCodeAsync(Alice, ct);
        // Mint another, redeem it (should be pruned).
        var (used, _) = await _repo.CreateCodeAsync(Alice, ct);
        // CreateCodeAsync drops the previous live row, so re-issue 'live'
        // after the redeemed one to leave both in the table.
        await _repo.RedeemAsync(used, ct);
        var (current, _) = await _repo.CreateCodeAsync(Alice, ct);

        var pruned = await _repo.PruneExpiredAsync(ct);
        Assert.True(pruned >= 1, $"Expected to prune the redeemed row, pruned={pruned}");

        // The currently-live code still works.
        Assert.NotNull(await _repo.RedeemAsync(current, ct));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }
}
