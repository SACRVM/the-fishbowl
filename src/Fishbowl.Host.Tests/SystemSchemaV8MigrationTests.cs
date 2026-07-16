using Dapper;
using Fishbowl.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Host.Tests;

// V8 renames the `teams`/`team_members` tables to `spaces`/`space_members`,
// renames the `team_id` column on space_members to `space_id`, recreates the
// membership index under its new name, and rewrites 'team' → 'space' in the
// api_keys context_type / owner_type discriminator columns.
//
// The on-disk folder move (teams/ → spaces/) is a separate concern tested in
// Fishbowl.Data.Tests.SpacesFolderMigrationTests.
public class SystemSchemaV8MigrationTests : IDisposable
{
    private readonly string _dataDir;

    public SystemSchemaV8MigrationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_system_v8_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
    }

    // ── Helper: produce a system.db frozen at V7 ────────────────────────────
    // The seed script mirrors the DDL that ApplySystemV1–V7 would produce so
    // the test is independent of future V9+ migrations. Important: we set
    // PRAGMA user_version = 7 to prevent V8 from running during seed, then
    // clear pools before handing control to DatabaseFactory.
    private string SeedV7Db()
    {
        var systemPath = Path.Combine(_dataDir, "system.db");
        using var seed = new SqliteConnection($"Data Source={systemPath}");
        seed.Open();

        // V1 — users / user_mappings / system_config
        seed.Execute(@"
            CREATE TABLE users (
                id TEXT PRIMARY KEY, name TEXT, email TEXT,
                avatar_url TEXT, created_at TEXT NOT NULL,
                password_hash TEXT, password_salt TEXT,
                is_admin INTEGER NOT NULL DEFAULT 0,
                must_change_password INTEGER NOT NULL DEFAULT 0);");
        seed.Execute(@"
            CREATE TABLE user_mappings (
                provider TEXT NOT NULL, provider_id TEXT NOT NULL,
                user_id TEXT NOT NULL REFERENCES users(id),
                PRIMARY KEY(provider, provider_id));");
        seed.Execute(@"
            CREATE TABLE system_config (
                key TEXT PRIMARY KEY, value TEXT, updated_at TEXT NOT NULL);");

        // V2 — teams / team_members (historical DDL; V8 will rename them)
        seed.Execute(@"
            CREATE TABLE teams (
                id TEXT PRIMARY KEY, slug TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL, created_by TEXT NOT NULL REFERENCES users(id),
                created_at TEXT NOT NULL);");
        seed.Execute(@"
            CREATE TABLE team_members (
                team_id TEXT NOT NULL REFERENCES teams(id),
                user_id TEXT NOT NULL REFERENCES users(id),
                role TEXT NOT NULL CHECK(role IN ('readonly','member','admin','owner')),
                joined_at TEXT NOT NULL,
                PRIMARY KEY (team_id, user_id));");
        seed.Execute("CREATE INDEX idx_team_members_user ON team_members(user_id);");

        // V3 — api_keys with old context_type = ('user','team')
        seed.Execute(@"
            CREATE TABLE api_keys (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL REFERENCES users(id),
                context_type TEXT NOT NULL CHECK(context_type IN ('user','team','app')),
                context_id TEXT NOT NULL,
                owner_type TEXT CHECK(owner_type IS NULL OR owner_type IN ('user','team')),
                owner_id TEXT,
                name TEXT NOT NULL,
                key_hash TEXT NOT NULL, key_prefix TEXT NOT NULL, scopes TEXT NOT NULL,
                created_at TEXT NOT NULL, last_used_at TEXT, revoked_at TEXT,
                CHECK((context_type = 'app') = (owner_type IS NOT NULL AND owner_id IS NOT NULL)));");
        seed.Execute(
            "CREATE INDEX idx_api_keys_prefix ON api_keys(key_prefix) WHERE revoked_at IS NULL;");

        // V4 — notification_channels / discord_link_codes
        seed.Execute(@"
            CREATE TABLE notification_channels (
                id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id),
                channel_type TEXT NOT NULL, channel_id TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL,
                UNIQUE(user_id, channel_type));");
        seed.Execute(
            "CREATE INDEX idx_notif_channels_user ON notification_channels(user_id);");
        seed.Execute(@"
            CREATE TABLE discord_link_codes (
                code TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id),
                created_at TEXT NOT NULL, expires_at TEXT NOT NULL, redeemed_at TEXT);");
        seed.Execute(
            "CREATE INDEX idx_discord_link_codes_user ON discord_link_codes(user_id);");

        // Seed rows so the migration has data to rewrite.
        var now = DateTime.UtcNow.ToString("o");
        seed.Execute(
            "INSERT INTO users(id, name, email, created_at) VALUES ('u1', 'Alice', 'a@a', @now)",
            new { now });
        seed.Execute(
            "INSERT INTO teams(id, slug, name, created_by, created_at) VALUES ('t1', 'dev', 'Dev', 'u1', @now)",
            new { now });
        seed.Execute(
            "INSERT INTO team_members(team_id, user_id, role, joined_at) VALUES ('t1', 'u1', 'owner', @now)",
            new { now });
        // A 'team' context_type key and an 'app' key with 'team' owner_type.
        seed.Execute(@"
            INSERT INTO api_keys
              (id, user_id, context_type, context_id, owner_type, owner_id, name,
               key_hash, key_prefix, scopes, created_at)
            VALUES ('k1', 'u1', 'team', 't1', NULL, NULL, 'team-key',
                    'hash1', 'prefix1', '[]', @now)", new { now });
        seed.Execute(@"
            INSERT INTO api_keys
              (id, user_id, context_type, context_id, owner_type, owner_id, name,
               key_hash, key_prefix, scopes, created_at)
            VALUES ('k2', 'u1', 'app', 'app1', 'team', 't1', 'app-key',
                    'hash2', 'prefix2', '[]', @now)", new { now });
        // A user-context key — must pass through unchanged.
        seed.Execute(@"
            INSERT INTO api_keys
              (id, user_id, context_type, context_id, owner_type, owner_id, name,
               key_hash, key_prefix, scopes, created_at)
            VALUES ('k3', 'u1', 'user', 'u1', NULL, NULL, 'user-key',
                    'hash3', 'prefix3', '[]', @now)", new { now });

        seed.Execute("PRAGMA user_version = 7");

        SqliteConnection.ClearAllPools();
        return systemPath;
    }

    [Fact]
    public async Task V8_RenamesTables_TeamsBecomesSpaces()
    {
        SeedV7Db();
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        var tables = (await db.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")).ToList();

        Assert.Contains("spaces", tables);
        Assert.Contains("space_members", tables);
        Assert.DoesNotContain("teams", tables);
        Assert.DoesNotContain("team_members", tables);
    }

    [Fact]
    public async Task V8_ExistingRows_SurviveInRenamedTables()
    {
        SeedV7Db();
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        var spaceCount = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM spaces WHERE id = 't1'");
        Assert.Equal(1, spaceCount);

        var memberCount = await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM space_members WHERE space_id = 't1' AND user_id = 'u1'");
        Assert.Equal(1, memberCount);
    }

    [Fact]
    public async Task V8_ApiKeys_TeamContextType_RewrittenToSpace()
    {
        SeedV7Db();
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        // The 'team' context_type key must now read 'space'.
        var contextType = await db.ExecuteScalarAsync<string>(
            "SELECT context_type FROM api_keys WHERE id = 'k1'");
        Assert.Equal("space", contextType);
    }

    [Fact]
    public async Task V8_ApiKeys_TeamOwnerType_RewrittenToSpace()
    {
        SeedV7Db();
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        // The 'team' owner_type on the app key must now read 'space'.
        var ownerType = await db.ExecuteScalarAsync<string>(
            "SELECT owner_type FROM api_keys WHERE id = 'k2'");
        Assert.Equal("space", ownerType);
    }

    [Fact]
    public async Task V8_ApiKeys_UserContextType_Unchanged()
    {
        SeedV7Db();
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        var contextType = await db.ExecuteScalarAsync<string>(
            "SELECT context_type FROM api_keys WHERE id = 'k3'");
        Assert.Equal("user", contextType);
    }

    [Fact]
    public async Task V8_BumpsVersionTo8()
    {
        SeedV7Db();
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        var version = await db.ExecuteScalarAsync<long>("PRAGMA user_version");
        Assert.Equal(8, version);
    }

    [Fact]
    public async Task V8_SpaceMemberIndex_Recreated()
    {
        SeedV7Db();
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        var indexes = (await db.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='index' ORDER BY name")).ToList();

        Assert.Contains("idx_space_members_user", indexes);
        Assert.DoesNotContain("idx_team_members_user", indexes);
    }

    [Fact]
    public async Task V8_FreshDb_AlsoConvergesOnSpacesSchema()
    {
        // A fresh install goes straight to V8 without any 'teams' table ever
        // having existed on disk. Verify the resulting schema is identical in
        // shape to an upgraded V7 DB.
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateSystemConnection();

        var tables = (await db.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")).ToList();

        Assert.Contains("spaces", tables);
        Assert.Contains("space_members", tables);
        Assert.DoesNotContain("teams", tables);
        Assert.DoesNotContain("team_members", tables);

        var version = await db.ExecuteScalarAsync<long>("PRAGMA user_version");
        Assert.Equal(8, version);
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
