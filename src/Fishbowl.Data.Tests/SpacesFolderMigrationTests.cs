using Dapper;
using Fishbowl.Core;
using Fishbowl.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests;

// MigrateTeamsFolderToSpaces runs once at DatabaseFactory construction and
// slides any on-disk `teams/` content into the `spaces/` layout:
//
//   teams/{id}/team.db  →  spaces/{id}/space.db   (folder-per-context shape)
//   teams/{id}.db       →  spaces/{id}/space.db   (ancient flat shape)
//
// Nested `apps/` sub-folders under a team folder move atomically alongside
// the DB file because Directory.Move operates on the whole folder.
//
// Idempotent: an existing `spaces/{id}` target is never overwritten.
// The `teams/` root is deleted when it is empty after migration; it is left
// in place when it still contains unresolved files.
//
// User flat→folder migration is a separate concern — see LegacyLayoutMigrationTests.
public class SpacesFolderMigrationTests : IDisposable
{
    private readonly string _dataDir;

    public SpacesFolderMigrationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_spaces_folder_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void SeedMinimalDb(string dbPath, string spaceId, int userVersion = 1)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();

        // Bare v1 user schema — factory will roll forward.
        db.Execute(@"
            CREATE TABLE notes (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, content TEXT,
                content_secret BLOB, type TEXT NOT NULL DEFAULT 'note', tags TEXT,
                created_by TEXT NOT NULL, created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL, pinned INTEGER DEFAULT 0,
                archived INTEGER DEFAULT 0);");
        db.Execute("CREATE VIRTUAL TABLE notes_fts USING fts5(title, content, tags);");

        var now = DateTime.UtcNow.ToString("o");
        db.Execute(@"
            INSERT INTO notes(id, title, content, type, tags, created_by, created_at, updated_at)
            VALUES (@id, @t, '', 'note', '[]', @uid, @now, @now)",
            new { id = "n1", t = $"note-in-{spaceId}", uid = spaceId, now });

        db.Execute($"PRAGMA user_version = {userVersion}");
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void FolderPerContext_TeamDb_MovedToSpacesFolder()
    {
        // Seed teams/{id}/team.db
        const string id = "01HZ_SPACE1";
        var oldTeamDir = Path.Combine(_dataDir, "teams", id);
        var oldTeamDb = Path.Combine(oldTeamDir, "team.db");
        SeedMinimalDb(oldTeamDb, id);
        SqliteConnection.ClearAllPools();

        var factory = new DatabaseFactory(_dataDir);

        var expectedDb = Path.Combine(_dataDir, "spaces", id, "space.db");
        Assert.True(File.Exists(expectedDb),
            $"Expected DB to be moved to {expectedDb}");
        Assert.False(File.Exists(oldTeamDb),
            $"Legacy team.db should be gone from {oldTeamDb}");

        // Content must survive intact — open via the factory context path.
        using var db = factory.CreateContextConnection(ContextRef.Space(id));
        var title = db.ExecuteScalar<string>($"SELECT title FROM notes WHERE id = 'n1'");
        Assert.Equal($"note-in-{id}", title);
    }

    [Fact]
    public void AncientFlatTeamDb_MovedToSpacesFolder()
    {
        // Seed teams/{id}.db (no sub-folder)
        const string id = "01HZ_SPACE2";
        var teamsDir = Path.Combine(_dataDir, "teams");
        Directory.CreateDirectory(teamsDir);
        var flatDb = Path.Combine(teamsDir, $"{id}.db");
        SeedMinimalDb(flatDb, id);
        SqliteConnection.ClearAllPools();

        var factory = new DatabaseFactory(_dataDir);

        var expectedDb = Path.Combine(_dataDir, "spaces", id, "space.db");
        Assert.True(File.Exists(expectedDb),
            $"Expected flat team.db to be moved to {expectedDb}");
        Assert.False(File.Exists(flatDb),
            $"Legacy flat {flatDb} should be gone");
    }

    [Fact]
    public void Migration_Idempotent_ExistingSpaceNotOverwritten()
    {
        // Pre-seed BOTH the teams/{id}/team.db AND spaces/{id}/space.db.
        // The space file (which contains the real data) must win.
        const string id = "01HZ_SPACE3";
        var oldTeamDb = Path.Combine(_dataDir, "teams", id, "team.db");
        var newSpaceDb = Path.Combine(_dataDir, "spaces", id, "space.db");

        SeedMinimalDb(oldTeamDb, id);
        SeedMinimalDb(newSpaceDb, id);

        // Overwrite the notes in the real space DB so we can distinguish.
        using (var real = new SqliteConnection($"Data Source={newSpaceDb}"))
        {
            real.Open();
            real.Execute("UPDATE notes SET title = 'real-space-data' WHERE id = 'n1'");
        }
        SqliteConnection.ClearAllPools();

        var factory = new DatabaseFactory(_dataDir);

        using var db = factory.CreateContextConnection(ContextRef.Space(id));
        var title = db.ExecuteScalar<string>("SELECT title FROM notes WHERE id = 'n1'");
        Assert.True(title == "real-space-data",
            "existing space.db must not be overwritten by the stale team.db");
    }

    [Fact]
    public void Migration_AppsSubfolder_MovesAtomicallyWithTeam()
    {
        // Seed teams/{id}/ with team.db AND an apps/ subfolder.
        const string id = "01HZ_SPACE4";
        var teamDir = Path.Combine(_dataDir, "teams", id);
        var oldTeamDb = Path.Combine(teamDir, "team.db");
        SeedMinimalDb(oldTeamDb, id);

        var appsDir = Path.Combine(teamDir, "apps", "myapp");
        Directory.CreateDirectory(appsDir);
        File.WriteAllText(Path.Combine(appsDir, "app.db"), "placeholder");
        SqliteConnection.ClearAllPools();

        var factory = new DatabaseFactory(_dataDir);

        // The apps subfolder must have moved alongside.
        var expectedAppsDir = Path.Combine(_dataDir, "spaces", id, "apps", "myapp");
        Assert.True(Directory.Exists(expectedAppsDir),
            $"apps/ subfolder should move atomically with the team folder to {expectedAppsDir}");
    }

    [Fact]
    public void EmptyTeamsRoot_DeletedAfterMigration()
    {
        // Seed one team folder so the teams/ root exists, then migrate.
        const string id = "01HZ_SPACE5";
        var oldTeamDb = Path.Combine(_dataDir, "teams", id, "team.db");
        SeedMinimalDb(oldTeamDb, id);
        SqliteConnection.ClearAllPools();

        // Confirm teams/ was created.
        Assert.True(Directory.Exists(Path.Combine(_dataDir, "teams")));

        _ = new DatabaseFactory(_dataDir);

        // After migrating the single sub-folder the root becomes empty; it
        // should be removed so the migration doesn't re-trigger next boot.
        var teamsRoot = Path.Combine(_dataDir, "teams");
        Assert.False(Directory.Exists(teamsRoot),
            $"teams/ root should be deleted when empty after migration, but still exists at {teamsRoot}");
    }

    [Fact]
    public void NoTeamsFolder_MigrationSkippedSilently()
    {
        // Baseline: no teams/ folder at all.  Factory should boot without error
        // and spaces/ should be empty (just the dir itself).
        Assert.False(Directory.Exists(Path.Combine(_dataDir, "teams")));

        var factory = new DatabaseFactory(_dataDir);

        var spacesRoot = Path.Combine(_dataDir, "spaces");
        Assert.True(Directory.Exists(spacesRoot),
            "spaces/ dir must be created by the factory regardless");
        // Nothing migrated in — spaces/ is empty.
        var entries = Directory.EnumerateFileSystemEntries(spacesRoot).ToList();
        Assert.Empty(entries);
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
