using Xunit;
using Microsoft.Data.Sqlite;
using Fishbowl.Data;
using Dapper;
using System.IO;
using System.Data;

namespace Fishbowl.Tests;

public class DatabaseFactoryTests : IDisposable
{
    private readonly string _tempDbDir;

    public DatabaseFactoryTests()
    {
        _tempDbDir = Path.Combine(Path.GetTempPath(), "fishbowl_db_tests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDbDir);
    }

    [Fact]
    public void CreateConnection_CreatesPhysicalFile_Test()
    {
        // Arrange
        var userId = "test_user_1";
        var factory = new DatabaseFactory(_tempDbDir);

        // Act
        using var connection = factory.CreateConnection(userId);

        // Assert
        // Folder-per-context layout: users/{id}/personal.db
        var dbPath = Path.Combine(_tempDbDir, "users", userId, "personal.db");
        Assert.True(File.Exists(dbPath));
    }

    [Fact]
    public void CreateSystemConnection_CreatesAndInitializesSystemDb_Test()
    {
        // Arrange
        var factory = new DatabaseFactory(_tempDbDir);

        // Act
        using var connection = factory.CreateSystemConnection();

        // Assert
        var dbPath = Path.Combine(_tempDbDir, "system.db");
        Assert.True(File.Exists(dbPath));

        var tables = connection.Query<string>("SELECT name FROM sqlite_master WHERE type='table'").ToList();
        Assert.Contains("users", tables);
        Assert.Contains("user_mappings", tables);
        Assert.Contains("system_config", tables);
        Assert.Contains("teams", tables);
        Assert.Contains("team_members", tables);

        Assert.Contains("api_keys", tables);
        Assert.Contains("notification_channels", tables);
        Assert.Contains("discord_link_codes", tables);

        // V5 + V6 added local-auth columns + the must-change flag to users.
        var userColumns = connection.Query<string>(
            "SELECT name FROM pragma_table_info('users')").ToList();
        Assert.Contains("password_hash", userColumns);
        Assert.Contains("password_salt", userColumns);
        Assert.Contains("is_admin", userColumns);
        Assert.Contains("must_change_password", userColumns);

        var version = connection.ExecuteScalar<long>("PRAGMA user_version");
        Assert.Equal(6, version);
    }

    [Fact]
    public void EnsureInitialized_CreatesSchema_Test()
    {
        // Arrange
        var userId = "test_user_2";
        var factory = new DatabaseFactory(_tempDbDir);

        // Act
        using var connection = factory.CreateConnection(userId);

        // Assert
        var tables = connection.Query<string>("SELECT name FROM sqlite_master WHERE type='table'").ToList();

        Assert.Contains("notes", tables);
        Assert.Contains("events", tables);
        Assert.Contains("sync_sources", tables);
        Assert.Contains("reminders", tables);
        Assert.Contains("todos", tables);
    }

    [Fact]
    public void EnsureInitialized_SetsUserVersion_Test()
    {
        // Arrange
        var userId = "test_user_3";
        var factory = new DatabaseFactory(_tempDbDir);

        // Act
        using var connection = factory.CreateConnection(userId);

        // Assert
        var version = connection.ExecuteScalar<int>("PRAGMA user_version");
        Assert.Equal(5, version);
    }

    [Fact]
    public async Task WithUserTransaction_RollsBackOnException_Test()
    {
        var factory = new DatabaseFactory(_tempDbDir);
        const string userId = "tx_user";

        // Pre-create the DB and a sentinel row
        using (var conn = factory.CreateConnection(userId))
        {
            await conn.ExecuteAsync(
                "INSERT INTO notes (id, title, created_by, created_at, updated_at) VALUES ('sentinel', 'sentinel', @u, @t, @t)",
                new { u = userId, t = DateTime.UtcNow.ToString("o") });
        }

        // Act — throw inside the transaction after an insert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await factory.WithUserTransactionAsync(userId, async (db, tx, ct) =>
            {
                await db.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO notes (id, title, created_by, created_at, updated_at) VALUES ('rollback-me', 't', @u, @t, @t)",
                        new { u = userId, t = DateTime.UtcNow.ToString("o") },
                        transaction: tx, cancellationToken: ct));
                throw new InvalidOperationException("boom");
            }, TestContext.Current.CancellationToken);
        });

        // Assert — only the sentinel survives
        using (var conn = factory.CreateConnection(userId))
        {
            var count = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM notes");
            Assert.Equal(1, count);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDbDir))
        {
            Directory.Delete(_tempDbDir, true);
        }
    }
}
