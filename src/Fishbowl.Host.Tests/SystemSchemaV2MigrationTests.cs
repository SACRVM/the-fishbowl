using Dapper;
using Fishbowl.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Host.Tests;

public class SystemSchemaV2MigrationTests : IDisposable
{
    private readonly string _dataDir;

    public SystemSchemaV2MigrationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_system_v2_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
    }

    [Fact]
    public async Task ApplyV2_OnFreshSystemDb_CreatesSpacesAndMembers()
    {
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        // Migrations chain: fresh DB rolls all the way up to the current head
        // (V8). V2 created teams/team_members; V8 renamed them to spaces/space_members.
        var version = await db.ExecuteScalarAsync<long>("PRAGMA user_version");
        Assert.True(version >= 2, $"expected version >= 2, got {version}");

        var tables = (await db.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")).ToList();
        Assert.Contains("spaces", tables);
        Assert.Contains("space_members", tables);
    }

    [Fact]
    public async Task ApplyV2_FromV1_PreservesExistingUsers()
    {
        // Seed a v1 system.db with a user, then run factory to trigger V2+.
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

            seed.Execute(
                "INSERT INTO users(id, name, email, created_at) VALUES ('u1', 'Alice', 'a@b.c', @now)",
                new { now = DateTime.UtcNow.ToString("o") });
            seed.Execute("PRAGMA user_version = 1");
        }
        SqliteConnection.ClearAllPools();

        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        var version = await db.ExecuteScalarAsync<long>("PRAGMA user_version");
        Assert.True(version >= 2, $"expected version >= 2, got {version}");

        var userExists = await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM users WHERE id = 'u1'");
        Assert.Equal(1, userExists);
    }

    [Fact]
    public async Task SpacesTable_EnforcesUniqueSlugs()
    {
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        // Seed a user to satisfy FK.
        await db.ExecuteAsync(
            "INSERT INTO users(id, name, email, created_at) VALUES ('owner', 'O', 'o@o', @now)",
            new { now = DateTime.UtcNow.ToString("o") });

        var now = DateTime.UtcNow.ToString("o");
        await db.ExecuteAsync(
            "INSERT INTO spaces(id, slug, name, created_by, created_at) VALUES ('t1', 'fishbowl', 'First', 'owner', @now)",
            new { now });

        await Assert.ThrowsAsync<SqliteException>(() =>
            db.ExecuteAsync(
                "INSERT INTO spaces(id, slug, name, created_by, created_at) VALUES ('t2', 'fishbowl', 'Second', 'owner', @now)",
                new { now }));
    }

    [Fact]
    public async Task SpaceMembers_RejectsInvalidRole()
    {
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        var now = DateTime.UtcNow.ToString("o");
        await db.ExecuteAsync(
            "INSERT INTO users(id, name, email, created_at) VALUES ('u1', 'U', 'u@u', @now)",
            new { now });
        await db.ExecuteAsync(
            "INSERT INTO spaces(id, slug, name, created_by, created_at) VALUES ('t1', 'slug', 'T', 'u1', @now)",
            new { now });

        await Assert.ThrowsAsync<SqliteException>(() =>
            db.ExecuteAsync(
                "INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES ('t1', 'u1', 'god', @now)",
                new { now }));
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
