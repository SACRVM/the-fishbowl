using System.IO.Compression;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Data.Files;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Files;

// Admin A3 below HTTP: an account's archive (what's in it, nothing when it
// never stored anything), the parked folder, retention.
public class UserArchiverTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _db;
    private readonly SystemRepository _system;
    private readonly UserArchiver _archiver;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public UserArchiverTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_user_archive_" + Path.GetRandomFileName());
        _db = new DatabaseFactory(_dataDir);
        _system = new SystemRepository(_db);
        _archiver = new UserArchiver(_db, _system);
        _system.SetConfigAsync(FileLimits.MinFreeBytesKey, "0").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string ArchiveRoot => Path.Combine(_dataDir, "archive", "users");

    private string SeedUserWithData()
    {
        var id = "u_" + Guid.NewGuid().ToString("N")[..10];
        using (var conn = _db.CreateContextConnection(ContextRef.User(id)))
            conn.Execute("INSERT INTO notes (id, title, created_by, created_at, updated_at) VALUES ('n', 't', @u, @now, @now)",
                new { u = id, now = DateTime.UtcNow.ToString("o") });
        var folder = _db.ResolveContextFolder(ContextRef.User(id));
        Directory.CreateDirectory(Path.Combine(folder, "files", ".trash", "01ABC"));
        File.WriteAllText(Path.Combine(folder, "files", "keep.txt"), "k");
        File.WriteAllText(Path.Combine(folder, "files", ".trash", "01ABC", "old.txt"), "o");
        return id;
    }

    [Fact]
    public async Task Archive_HoldsDbFilesAndTrash_AndLists()
    {
        var id = SeedUserWithData();
        var archived = await _archiver.ArchiveAsync(id, "Name", DateTime.UtcNow, "admin", Ct);
        Assert.NotNull(archived);
        Assert.Equal(id, archived!.UserId);
        Assert.NotNull(archived.ExpiresAt);   // default retention: 30 days

        using (var zip = ZipFile.OpenRead(Path.Combine(ArchiveRoot, archived.Id + ".zip")))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("manifest.json", names);
            Assert.Contains("personal.db", names);
            Assert.Contains("files/keep.txt", names);
            Assert.Contains("files/.trash/01ABC/old.txt", names);
        }
        Assert.Equal(archived.Id, Assert.Single(await _archiver.ListAsync(Ct)).Id);
    }

    [Fact]
    public async Task Archive_OfAnAccountThatNeverStored_IsNull()
    {
        Assert.Null(await _archiver.ArchiveAsync("never-" + Guid.NewGuid().ToString("N")[..6], null, DateTime.UtcNow, "admin", Ct));
        Assert.Empty(await _archiver.ListAsync(Ct));
    }

    [Fact]
    public async Task DeleteFolder_RemovesIt_AndPurgeSweepsParkedLeftovers()
    {
        var id = SeedUserWithData();
        await _archiver.DeleteUserFolderAsync(id, Ct);
        Assert.False(Directory.Exists(_db.ResolveContextFolder(ContextRef.User(id))));

        var parked = Path.Combine(_db.UsersRoot, ".deleted-someone");
        Directory.CreateDirectory(parked);
        File.WriteAllText(Path.Combine(parked, "x"), "x");
        await _archiver.PurgeAsync(Ct);
        Assert.False(Directory.Exists(parked));
    }

    [Fact]
    public async Task Purge_RespectsRetention_ZeroKeepsForever()
    {
        var archived = await _archiver.ArchiveAsync(SeedUserWithData(), null, DateTime.UtcNow, "admin", Ct);
        Backdate(archived!.Id, days: 40);

        await _system.SetConfigAsync(DataLifecycleLimits.ArchiveRetentionDaysKey, "0", Ct);
        Assert.Equal(0, await _archiver.PurgeAsync(Ct));
        Assert.Null(Assert.Single(await _archiver.ListAsync(Ct)).ExpiresAt);

        await _system.SetConfigAsync(DataLifecycleLimits.ArchiveRetentionDaysKey, "30", Ct);
        Assert.Equal(1, await _archiver.PurgeAsync(Ct));
        Assert.Empty(await _archiver.ListAsync(Ct));
    }

    private void Backdate(string archiveId, int days)
    {
        using var zip = ZipFile.Open(Path.Combine(ArchiveRoot, archiveId + ".zip"), ZipArchiveMode.Update);
        var entry = zip.GetEntry("manifest.json")!;
        string json;
        using (var r = new StreamReader(entry.Open())) json = r.ReadToEnd();
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["archivedAt"] = DateTime.UtcNow.AddDays(-days).ToString("o");
        entry.Delete();
        using var w = new StreamWriter(zip.CreateEntry("manifest.json").Open());
        w.Write(node.ToJsonString());
    }
}
