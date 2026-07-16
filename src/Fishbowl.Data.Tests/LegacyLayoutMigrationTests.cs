using Dapper;
using Fishbowl.Core;
using Fishbowl.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests;

// Folder-per-context layout was introduced after the flat `users/{id}.db`
// layout. Old installs auto-migrate on first open: legacy file is moved
// to `users/{id}/personal.db`, no data loss.
//
// Space (formerly team) folder migration is handled separately by
// MigrateTeamsFolderToSpaces — see SpacesFolderMigrationTests.
public class LegacyLayoutMigrationTests : IDisposable
{
    private readonly string _dataDir;

    public LegacyLayoutMigrationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_legacy_layout_" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_dataDir, "users"));
    }

    [Fact]
    public void OpenContext_LegacyUserFile_MovedIntoFolder()
    {
        const string userId = "alice";
        var legacy = Path.Combine(_dataDir, "users", $"{userId}.db");
        var newPath = Path.Combine(_dataDir, "users", userId, "personal.db");

        SeedNoteAtLegacyPath(legacy, userId, noteId: "n1", title: "carry-me-over");
        SqliteConnection.ClearAllPools();

        var factory = new DatabaseFactory(_dataDir);
        using (var db = factory.CreateContextConnection(ContextRef.User(userId)))
        {
            // Same row count — file was moved, not recreated.
            var count = db.ExecuteScalar<long>("SELECT COUNT(*) FROM notes");
            Assert.Equal(1, count);

            var title = db.ExecuteScalar<string>("SELECT title FROM notes WHERE id = 'n1'");
            Assert.Equal("carry-me-over", title);
        }

        Assert.True(File.Exists(newPath), $"expected new path {newPath}");
        Assert.False(File.Exists(legacy), $"expected legacy path {legacy} to have moved");
    }

    [Fact]
    public void OpenContext_NewPathExistsAndLegacyAlsoExists_NewWins()
    {
        const string userId = "bob";
        var legacy = Path.Combine(_dataDir, "users", $"{userId}.db");
        var newPath = Path.Combine(_dataDir, "users", userId, "personal.db");

        // The new path has the *real* data; the legacy path is a stale leftover
        // (e.g. operator restored a backup but forgot to delete the old file).
        // We must never overwrite the live file.
        SeedNoteAtLegacyPath(newPath, userId, noteId: "real", title: "the-real-one");
        SeedNoteAtLegacyPath(legacy, userId, noteId: "stale", title: "stale-data");
        SqliteConnection.ClearAllPools();

        var factory = new DatabaseFactory(_dataDir);
        using (var db = factory.CreateContextConnection(ContextRef.User(userId)))
        {
            var titles = db.Query<string>("SELECT title FROM notes ORDER BY id").ToList();
            Assert.Single(titles);
            Assert.Equal("the-real-one", titles[0]);
        }

        Assert.True(File.Exists(legacy), "legacy file must be left in place when new path already exists");
    }

    private static void SeedNoteAtLegacyPath(string path, string userId, string noteId, string title)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Bare v1 schema is enough — the factory will roll forward on open.
        using var seed = new SqliteConnection($"Data Source={path}");
        seed.Open();
        seed.Execute(@"
            CREATE TABLE notes (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, content TEXT,
                content_secret BLOB, type TEXT NOT NULL DEFAULT 'note', tags TEXT,
                created_by TEXT NOT NULL, created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL, pinned INTEGER DEFAULT 0,
                archived INTEGER DEFAULT 0);");
        seed.Execute("CREATE VIRTUAL TABLE notes_fts USING fts5(title, content, tags);");

        var now = DateTime.UtcNow.ToString("o");
        seed.Execute(@"
            INSERT INTO notes(id, title, content, type, tags, created_by, created_at, updated_at)
            VALUES (@id, @t, '', 'note', '[]', @uid, @now, @now)",
            new { id = noteId, t = title, uid = userId, now });

        seed.Execute("PRAGMA user_version = 1");
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
