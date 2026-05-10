using Dapper;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

public class NotificationChannelRepositoryTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;
    private readonly NotificationChannelRepository _repo;
    private const string Alice = "u_alice";
    private const string Bob = "u_bob";

    public NotificationChannelRepositoryTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_notif_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
        _repo = new NotificationChannelRepository(_factory);

        using var db = _factory.CreateSystemConnection();
        var now = DateTime.UtcNow.ToString("o");
        db.Execute(
            "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
            new[]
            {
                new { id = Alice, n = "Alice", e = "a@a", now },
                new { id = Bob,   n = "Bob",   e = "b@b", now },
            });
    }

    [Fact]
    public async Task UpsertAsync_FirstTime_InsertsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var row = await _repo.UpsertAsync(Alice, "discord", "999000111222", ct);

        Assert.False(string.IsNullOrEmpty(row.Id));
        Assert.Equal(Alice, row.UserId);
        Assert.Equal("discord", row.ChannelType);
        Assert.Equal("999000111222", row.ChannelId);
        Assert.True(row.Enabled);
    }

    [Fact]
    public async Task UpsertAsync_SecondTime_UpdatesChannelIdAndReEnables()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _repo.UpsertAsync(Alice, "discord", "111", ct);
        await _repo.SetEnabledAsync(Alice, "discord", false, ct);

        var second = await _repo.UpsertAsync(Alice, "discord", "222", ct);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("222", second.ChannelId);
        Assert.True(second.Enabled);
    }

    [Fact]
    public async Task FindUserByChannelAsync_ReturnsOwner_OrNull()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.UpsertAsync(Alice, "discord", "abc", ct);

        Assert.Equal(Alice, await _repo.FindUserByChannelAsync("discord", "abc", ct));
        Assert.Null(await _repo.FindUserByChannelAsync("discord", "no-such", ct));
        Assert.Null(await _repo.FindUserByChannelAsync("telegram", "abc", ct));
    }

    [Fact]
    public async Task ListForUserAsync_ScopesByUser()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.UpsertAsync(Alice, "discord", "a-disc", ct);
        await _repo.UpsertAsync(Bob, "discord", "b-disc", ct);

        var aliceList = (await _repo.ListForUserAsync(Alice, ct)).ToList();
        Assert.Single(aliceList);
        Assert.Equal("discord", aliceList[0].ChannelType);
        Assert.Equal("a-disc", aliceList[0].ChannelId);
    }

    [Fact]
    public async Task RemoveAsync_DeletesRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.UpsertAsync(Alice, "discord", "abc", ct);

        Assert.True(await _repo.RemoveAsync(Alice, "discord", ct));
        Assert.Null(await _repo.GetAsync(Alice, "discord", ct));
        // Idempotent — returns false the second time, no throw.
        Assert.False(await _repo.RemoveAsync(Alice, "discord", ct));
    }

    [Fact]
    public async Task UniqueConstraint_PreventsTwoActiveChannelsOfSameType()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.UpsertAsync(Alice, "discord", "111", ct);
        await _repo.UpsertAsync(Alice, "discord", "222", ct);

        var rows = (await _repo.ListForUserAsync(Alice, ct)).ToList();
        Assert.Single(rows);
        Assert.Equal("222", rows[0].ChannelId);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }
}
