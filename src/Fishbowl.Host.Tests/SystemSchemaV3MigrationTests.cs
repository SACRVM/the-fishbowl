using Dapper;
using Fishbowl.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Host.Tests;

public class SystemSchemaV3MigrationTests : IDisposable
{
    private readonly string _dataDir;

    public SystemSchemaV3MigrationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_system_v3_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
    }

    [Fact]
    public async Task ApplyV3_OnFreshSystemDb_CreatesApiKeysTable()
    {
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        var version = await db.ExecuteScalarAsync<long>("PRAGMA user_version");
        Assert.True(version >= 3, $"Expected schema >= v3, got v{version}");

        var tables = (await db.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")).ToList();
        Assert.Contains("api_keys", tables);

        var indexes = (await db.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='index' ORDER BY name")).ToList();
        Assert.Contains("idx_api_keys_prefix", indexes);
    }

    [Fact]
    public async Task ApplyV3_FromV2_PreservesExistingTeams()
    {
        // Seed a v2 system.db (users + user_mappings + system_config + teams
        // + team_members). Factory must roll it to v3 without touching the
        // existing rows.
        var systemPath = Path.Combine(_dataDir, "system.db");
        using (var seed = new SqliteConnection($"Data Source={systemPath}"))
        {
            seed.Open();
            seed.Execute(@"
                CREATE TABLE users (
                    id TEXT PRIMARY KEY, name TEXT, email TEXT,
                    avatar_url TEXT, created_at TEXT NOT NULL);");
            seed.Execute(@"
                CREATE TABLE user_mappings (
                    provider TEXT NOT NULL, provider_id TEXT NOT NULL,
                    user_id TEXT NOT NULL REFERENCES users(id),
                    PRIMARY KEY(provider, provider_id));");
            seed.Execute(@"
                CREATE TABLE system_config (
                    key TEXT PRIMARY KEY, value TEXT, updated_at TEXT NOT NULL);");
            seed.Execute(@"
                CREATE TABLE teams (
                    id TEXT PRIMARY KEY, slug TEXT NOT NULL UNIQUE, name TEXT NOT NULL,
                    created_by TEXT NOT NULL REFERENCES users(id), created_at TEXT NOT NULL);");
            seed.Execute(@"
                CREATE TABLE team_members (
                    team_id TEXT NOT NULL REFERENCES teams(id),
                    user_id TEXT NOT NULL REFERENCES users(id),
                    role TEXT NOT NULL CHECK(role IN ('readonly','member','admin','owner')),
                    joined_at TEXT NOT NULL,
                    PRIMARY KEY(team_id, user_id));");

            var now = DateTime.UtcNow.ToString("o");
            seed.Execute(
                "INSERT INTO users(id, name, email, created_at) VALUES ('u1', 'Alice', 'a@b.c', @now)",
                new { now });
            seed.Execute(
                "INSERT INTO teams(id, slug, name, created_by, created_at) VALUES ('t1', 'fb', 'F', 'u1', @now)",
                new { now });
            seed.Execute("PRAGMA user_version = 2");
        }
        SqliteConnection.ClearAllPools();

        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        var version = await db.ExecuteScalarAsync<long>("PRAGMA user_version");
        Assert.True(version >= 3, $"Expected schema >= v3, got v{version}");

        var userCount = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM users WHERE id = 'u1'");
        Assert.Equal(1, userCount);
        var teamCount = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM teams WHERE id = 't1'");
        Assert.Equal(1, teamCount);
    }

    [Fact]
    public async Task ApiKeysTable_EnforcesContextTypeCheck()
    {
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        await db.ExecuteAsync(
            "INSERT INTO users(id, name, email, created_at) VALUES ('u1', 'U', 'u@u', @now)",
            new { now = DateTime.UtcNow.ToString("o") });

        await Assert.ThrowsAsync<SqliteException>(() =>
            db.ExecuteAsync(@"
                INSERT INTO api_keys(id, user_id, context_type, context_id, name,
                                     key_hash, key_prefix, scopes, created_at)
                VALUES ('k1', 'u1', 'bogus', 'u1', 'x', 'h', 'fb_live_abcd', '[]', @now)",
                new { now = DateTime.UtcNow.ToString("o") }));
    }

    [Fact]
    public async Task ApiKeysTable_RequiresValidUserFk()
    {
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        // Enable FK enforcement — SQLite leaves it off by default per-connection.
        await db.ExecuteAsync("PRAGMA foreign_keys = ON");

        await Assert.ThrowsAsync<SqliteException>(() =>
            db.ExecuteAsync(@"
                INSERT INTO api_keys(id, user_id, context_type, context_id, name,
                                     key_hash, key_prefix, scopes, created_at)
                VALUES ('k1', 'ghost', 'user', 'ghost', 'x', 'h', 'fb_live_abcd', '[]', @now)",
                new { now = DateTime.UtcNow.ToString("o") }));
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
