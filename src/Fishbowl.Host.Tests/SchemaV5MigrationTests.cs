using Dapper;
using Fishbowl.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Host.Tests;

// v5 adds contacts + contacts_fts. No data migration from existing rows —
// the feature is new, tables are empty on first open.
public class SchemaV5MigrationTests : IDisposable
{
    private readonly string _dataDir;

    public SchemaV5MigrationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_schema_v5_" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_dataDir, "users"));
    }

    [Fact]
    public async Task ApplyV5_OnFreshDb_CreatesContactsAndSetsVersion5()
    {
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateConnection("fresh-user");

        // V6 bumps the current version (apps registry), but the v5 assertions
        // below still hold: a fresh DB walks through every migration in order.
        var version = await db.ExecuteScalarAsync<long>("PRAGMA user_version");
        Assert.Equal(6, version);

        var table = await db.ExecuteScalarAsync<string?>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'contacts'");
        Assert.Equal("contacts", table);

        var fts = await db.ExecuteScalarAsync<string?>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'contacts_fts'");
        Assert.Equal("contacts_fts", fts);

        var index = await db.ExecuteScalarAsync<string?>(
            "SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'idx_contacts_name_lower'");
        Assert.Equal("idx_contacts_name_lower", index);
    }

    [Fact]
    public async Task ApplyV5_MigratesV4Db_PreservesExistingData()
    {
        var userId = "v4-user";
        var dbPath = Path.Combine(_dataDir, "users", $"{userId}.db");

        // Seed a minimal v4 DB with a note + tag row so we can assert neither
        // is disturbed by the v5 upgrade.
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
            seed.Execute(@"
                CREATE TABLE tags (
                    name TEXT PRIMARY KEY, color TEXT NOT NULL, created_at TEXT NOT NULL,
                    is_system INTEGER NOT NULL DEFAULT 0,
                    user_assignable INTEGER NOT NULL DEFAULT 1,
                    user_removable INTEGER NOT NULL DEFAULT 1);");

            var now = DateTime.UtcNow.ToString("o");
            seed.Execute(
                "INSERT INTO notes(id, title, created_by, created_at, updated_at) " +
                "VALUES ('n1', 'kept', 'u', @now, @now)", new { now });
            seed.Execute(
                "INSERT INTO tags(name, color, created_at) VALUES ('work', 'blue', @now)",
                new { now });

            seed.Execute("PRAGMA user_version = 4");
        }
        SqliteConnection.ClearAllPools();

        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateConnection(userId);

        var version = await db.ExecuteScalarAsync<long>("PRAGMA user_version");
        Assert.Equal(6, version);

        var note = await db.ExecuteScalarAsync<string?>(
            "SELECT title FROM notes WHERE id = 'n1'");
        Assert.Equal("kept", note);

        var tag = await db.ExecuteScalarAsync<string?>(
            "SELECT name FROM tags WHERE name = 'work'");
        Assert.Equal("work", tag);

        var contactsCount = await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM contacts");
        Assert.Equal(0, contactsCount);
    }

    [Fact]
    public async Task ApplyV5_ContactsTable_AcceptsInsertAndSelect()
    {
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateConnection("rt-user");

        var now = DateTime.UtcNow.ToString("o");
        await db.ExecuteAsync(
            "INSERT INTO contacts(id, name, email, phone, notes, archived, " +
            "                     created_by, created_at, updated_at) " +
            "VALUES ('c1', 'Alice', 'alice@example.com', '555', 'met at conf', 0, " +
            "        'u', @now, @now)", new { now });

        var email = await db.ExecuteScalarAsync<string?>(
            "SELECT email FROM contacts WHERE id = 'c1'");
        Assert.Equal("alice@example.com", email);
    }

    [Fact]
    public async Task ApplyV5_ContactsFts_IsQueryable()
    {
        // Sanity: the virtual table exists and accepts FTS5 MATCH queries
        // against the expected columns (name/email/phone/notes).
        var factory = new DatabaseFactory(_dataDir);
        using var db = factory.CreateConnection("fts-user");

        await db.ExecuteAsync(
            "INSERT INTO contacts_fts(rowid, name, email, phone, notes) " +
            "VALUES (1, 'Alice Example', 'alice@e', '555', 'venue contact')");

        var hits = (await db.QueryAsync<string>(
            "SELECT name FROM contacts_fts WHERE contacts_fts MATCH 'venue'")).ToList();
        Assert.Contains("Alice Example", hits);
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
