using Dapper;
using Fishbowl.Core;
using Fishbowl.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests;

public class ContextRefTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;

    public ContextRefTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_ctxref_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
    }

    [Fact]
    public void CreateContextConnection_User_OpensUsersDbAndMigrates()
    {
        using var db = _factory.CreateContextConnection(ContextRef.User("alice"));
        var version = db.ExecuteScalar<long>("PRAGMA user_version");
        Assert.Equal(5, version);

        var file = Path.Combine(_dataDir, "users", "alice", "personal.db");
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void CreateContextConnection_Team_OpensTeamsDbAndMigrates()
    {
        using var db = _factory.CreateContextConnection(ContextRef.Team("fishbowl-dev"));
        var version = db.ExecuteScalar<long>("PRAGMA user_version");
        Assert.Equal(5, version);

        var file = Path.Combine(_dataDir, "teams", "fishbowl-dev", "team.db");
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void CreateContextConnection_Team_HasSameSchemaAsUser()
    {
        using var teamDb = _factory.CreateContextConnection(ContextRef.Team("project-x"));
        var tables = teamDb.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").ToList();

        Assert.Contains("notes", tables);
        Assert.Contains("events", tables);
        Assert.Contains("todos", tables);
        Assert.Contains("tags", tables);
        Assert.Contains("sync_sources", tables);
        Assert.Contains("reminders", tables);
    }

    [Fact]
    public void CreateContextConnection_Team_SeedsSystemTags()
    {
        using var db = _factory.CreateContextConnection(ContextRef.Team("seed-team"));
        var reserved = db.Query<string>(
            "SELECT name FROM tags WHERE is_system = 1 ORDER BY name").ToList();

        Assert.Equal(new[] { "review:pending", "source:mcp" }, reserved.ToArray());
    }

    [Fact]
    public void LegacyCreateConnection_StillMapsToUserContext()
    {
        using var legacy = _factory.CreateConnection("bob");
        using var ctx = _factory.CreateContextConnection(ContextRef.User("bob"));

        // Different connection objects but same underlying file.
        var legacyPath = ((SqliteConnection)legacy).DataSource;
        var ctxPath = ((SqliteConnection)ctx).DataSource;
        Assert.Equal(legacyPath, ctxPath);
    }

    [Fact]
    public async Task WithContextTransactionAsync_Team_CommitsOnSuccess()
    {
        var ctx = ContextRef.Team("tx-team");

        await _factory.WithContextTransactionAsync(ctx, async (db, tx, ct) =>
        {
            await db.ExecuteAsync(new CommandDefinition(
                "INSERT INTO events(id, title, start_at, created_by, created_at, updated_at) VALUES (@id, @t, @s, @cb, @c, @u)",
                new
                {
                    id = "e1",
                    t = "kickoff",
                    s = DateTime.UtcNow.ToString("o"),
                    cb = "owner",
                    c = DateTime.UtcNow.ToString("o"),
                    u = DateTime.UtcNow.ToString("o")
                },
                transaction: tx, cancellationToken: ct));
        }, TestContext.Current.CancellationToken);

        using var verify = _factory.CreateContextConnection(ctx);
        var count = await verify.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM events");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task WithContextTransactionAsync_RollsBackOnException()
    {
        var ctx = ContextRef.Team("rollback-team");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _factory.WithContextTransactionAsync(ctx, async (db, tx, ct) =>
            {
                await db.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO events(id, title, start_at, created_by, created_at, updated_at) VALUES ('bad', 't', @s, 'u', @s, @s)",
                    new { s = DateTime.UtcNow.ToString("o") },
                    transaction: tx, cancellationToken: ct));
                throw new InvalidOperationException("abort");
            }, TestContext.Current.CancellationToken);
        });

        using var verify = _factory.CreateContextConnection(ctx);
        var count = await verify.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM events");
        Assert.Equal(0, count);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }
}
