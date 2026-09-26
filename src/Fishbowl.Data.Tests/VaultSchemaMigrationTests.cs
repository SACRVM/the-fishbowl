using Dapper;
using Fishbowl.Core;
using Fishbowl.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests;

// v7 adds vault_keyslots (secret vault v2, docs/superpowers/specs/
// 2026-09-25-secret-vault-design.md). Lives in the context DB — user AND
// space, same schema — so it moves with the folder on cold import. No data
// migration: the table is new, empty on first open.
public class VaultSchemaMigrationTests : IDisposable
{
    private readonly string _dataDir;

    public VaultSchemaMigrationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_schema_v7_" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_dataDir, "users"));
    }

    [Fact]
    public async Task ApplyV7_OnFreshUserDb_CreatesVaultKeyslotsAndSetsVersion7()
    {
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateConnection("fresh-user");

        var version = await db.ExecuteScalarAsync<long>("PRAGMA user_version");
        Assert.Equal(9, version);

        var table = await db.ExecuteScalarAsync<string?>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'vault_keyslots'");
        Assert.Equal("vault_keyslots", table);

        var columns = (await db.QueryAsync<string>(
            "SELECT name FROM pragma_table_info('vault_keyslots')")).ToList();
        Assert.Contains("id", columns);
        Assert.Contains("kind", columns);
        Assert.Contains("label", columns);
        Assert.Contains("kdf", columns);
        Assert.Contains("credential_id", columns);
        Assert.Contains("wrapped_key", columns);
        Assert.Contains("created_at", columns);
        Assert.Contains("last_used_at", columns);
    }

    [Fact]
    public async Task ApplyV7_OnFreshSpaceDb_AlsoCreatesVaultKeyslots()
    {
        // Same schema as the user DB — the spec calls out the space table as
        // the phase-4 hook, so it must exist even before spaces have a
        // sharing UI for it.
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateContextConnection(ContextRef.Space("fresh-space"));

        var version = await db.ExecuteScalarAsync<long>("PRAGMA user_version");
        Assert.Equal(9, version);

        var table = await db.ExecuteScalarAsync<string?>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'vault_keyslots'");
        Assert.Equal("vault_keyslots", table);
    }

    [Fact]
    public async Task ApplyV7_MigratesV6Db_PreservesExistingData()
    {
        var userId = "v6-user";
        var dbPath = Path.Combine(_dataDir, "users", userId, "personal.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        // Seed a minimal v6-shape DB (just enough to prove data survives the
        // upgrade) and pin it at user_version 6 so only v7 runs on open.
        using (var seed = new SqliteConnection($"Data Source={dbPath}"))
        {
            seed.Open();
            seed.Execute(@"
                CREATE TABLE notes (
                    id TEXT PRIMARY KEY, title TEXT NOT NULL, content TEXT,
                    content_secret BLOB, type TEXT NOT NULL DEFAULT 'note', tags TEXT,
                    created_by TEXT NOT NULL, created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL, pinned INTEGER DEFAULT 0,
                    archived INTEGER DEFAULT 0);");
            seed.Execute("CREATE VIRTUAL TABLE notes_fts USING fts5(title, content, tags);");
            // ReconcileUserV3 runs unconditionally on every open of an
            // already-v3+ DB (DatabaseFactory.EnsureUserInitialized) and
            // expects a tags table to reconcile against.
            seed.Execute(@"
                CREATE TABLE tags (
                    name TEXT PRIMARY KEY, color TEXT NOT NULL, created_at TEXT NOT NULL,
                    is_system INTEGER NOT NULL DEFAULT 0,
                    user_assignable INTEGER NOT NULL DEFAULT 1,
                    user_removable INTEGER NOT NULL DEFAULT 1);");
            seed.Execute(@"
                CREATE TABLE apps (
                    id TEXT PRIMARY KEY, slug TEXT NOT NULL UNIQUE, name TEXT NOT NULL,
                    created_by TEXT NOT NULL, created_at TEXT NOT NULL);");

            var now = DateTime.UtcNow.ToString("o");
            seed.Execute(
                "INSERT INTO notes(id, title, created_by, created_at, updated_at) " +
                "VALUES ('n1', 'kept', 'u', @now, @now)", new { now });
            seed.Execute(
                "INSERT INTO apps(id, slug, name, created_by, created_at) " +
                "VALUES ('a1', 'todos', 'Todos', 'u', @now)", new { now });

            seed.Execute("PRAGMA user_version = 6");
        }
        SqliteConnection.ClearAllPools();

        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateConnection(userId);

        var version = await db.ExecuteScalarAsync<long>("PRAGMA user_version");
        Assert.Equal(9, version);

        var note = await db.ExecuteScalarAsync<string?>("SELECT title FROM notes WHERE id = 'n1'");
        Assert.Equal("kept", note);

        var app = await db.ExecuteScalarAsync<string?>("SELECT slug FROM apps WHERE id = 'a1'");
        Assert.Equal("todos", app);

        var vaultCount = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vault_keyslots");
        Assert.Equal(0, vaultCount);
    }

    [Fact]
    public async Task ApplyV7_VaultKeyslotsTable_AcceptsInsertAndSelect()
    {
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateConnection("rt-user");

        var now = DateTime.UtcNow.ToString("o");
        var wrappedKey = new byte[44];
        await db.ExecuteAsync(
            "INSERT INTO vault_keyslots(id, kind, label, kdf, credential_id, wrapped_key, created_at, last_used_at) " +
            "VALUES ('s1', 'passphrase', 'MacBook', '{\"alg\":\"pbkdf2-sha256\"}', NULL, @wrappedKey, @now, NULL)",
            new { wrappedKey, now });

        var label = await db.ExecuteScalarAsync<string?>(
            "SELECT label FROM vault_keyslots WHERE id = 's1'");
        Assert.Equal("MacBook", label);
    }

    [Fact]
    public async Task ApplyV7_VaultKeyslots_RejectsUnknownKind()
    {
        // CHECK (kind IN ('passphrase', 'recovery', 'passkey')) at the schema
        // level — a defense-in-depth backstop behind VaultRepository.Validate.
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateConnection("check-user");

        var now = DateTime.UtcNow.ToString("o");
        await Assert.ThrowsAsync<SqliteException>(() => db.ExecuteAsync(
            "INSERT INTO vault_keyslots(id, kind, label, kdf, wrapped_key, created_at) " +
            "VALUES ('s1', 'smoke-signal', '', '{}', @wrappedKey, @now)",
            new { wrappedKey = new byte[44], now }));
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
