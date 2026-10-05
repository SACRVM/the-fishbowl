using System.IO.Compression;
using System.Text;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Data.Files;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Files;

// The file store's server-wide limits as bytes really arrive (chunked bodies,
// writes one after another inside the usage cache's window), trash ids in any
// spelling, trashed app code kept for Designers, a transfer that would replace
// its own source's folder, the rename temp the sweeper leaves alone, and an
// archive restore checked against quotas and the server's cap.
public class FileStoreLimitsAndTrashTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _db;
    private readonly SystemRepository _system;
    private readonly FileService _files;
    private readonly ContextRef _me = ContextRef.User("limits_user");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FileStoreLimitsAndTrashTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_limits_" + Path.GetRandomFileName());
        _db = new DatabaseFactory(_dataDir);
        _system = new SystemRepository(_db);
        _files = new FileService(_db, _system);
        _system.SetConfigAsync(FileLimits.MinFreeBytesKey, "0").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Root(ContextRef? ctx = null) => Path.Combine(_db.ResolveContextFolder(ctx ?? _me), "files");

    // `declared: false` = a chunked body, no Content-Length.
    private Task<FileUploadResult> Upload(string path, int bytes, bool declared = true, ContextRef? ctx = null)
    {
        var data = Encoding.ASCII.GetBytes(new string('x', bytes));
        return _files.UploadAsync(ctx ?? _me, path, new MemoryStream(data), declared ? data.Length : null, null, true, true, "u", Ct);
    }

    private static async Task<FileStoreException> Refused(Func<Task> act) => await Assert.ThrowsAsync<FileStoreException>(act);

    // ───── the server's cap and floor ─────

    [Fact]
    public async Task InstanceCap_HoldsForAChunkedUpload_Test()
    {
        await _system.SetConfigAsync(FileLimits.InstanceCapBytesKey, "1000", Ct);
        var ex = await Refused(() => Upload("big.bin", 2000, declared: false));
        Assert.Equal(413, ex.Status);
        Assert.Equal("instance", ex.Field);
        Assert.False(File.Exists(Path.Combine(Root(), "big.bin")));
        Assert.Empty(Directory.EnumerateFiles(Root(), ".~fb-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InstanceCap_CountsEachWrite_WithinTheUsageCacheWindow_Test()
    {
        await _system.SetConfigAsync(FileLimits.InstanceCapBytesKey, "1000", Ct);
        await Upload("a.bin", 600);
        // The 60 s measurement still says "empty" — the first write must count.
        Assert.Equal("instance", (await Refused(() => Upload("b.bin", 600))).Field);
        Assert.Equal("instance", (await Refused(() => _files.CopyAsync(_me, "a.bin", "c.bin", "u", Ct))).Field);
        var other = ContextRef.User("limits_other");
        Assert.Equal("instance", (await Refused(() => Upload("d.bin", 600, ctx: other))).Field);

        // Freed room is room again.
        await _files.DeletePermanentlyAsync(_me, "a.bin", "u", Ct);
        await Upload("b.bin", 600);
    }

    // ───── trash ids and app code ─────

    [Fact]
    public async Task Trash_AnyIdSpelling_FindsTheSameEntry_Test()
    {
        await Upload("docs/a.txt", 3);
        var t = await _files.TrashAsync(_me, "docs/a.txt", "u", Ct);
        // Back where it was — not to the root as an entry without a row.
        var restored = await _files.RestoreAsync(_me, t.Id.ToLowerInvariant(), FileConflictMode.Fail, "u", Ct);
        Assert.Equal("docs/a.txt", restored.Path);
        Assert.Empty(await _files.ListTrashAsync(_me, Ct));

        var again = await _files.TrashAsync(_me, "docs/a.txt", "u", Ct);
        await _files.PurgeTrashAsync(_me, again.Id.ToLowerInvariant(), Ct);
        Assert.Empty(await _files.ListTrashAsync(_me, Ct));
        using var db = _db.CreateContextConnection(_me);
        Assert.Equal(0, db.ExecuteScalar<long>("SELECT COUNT(*) FROM file_trash"));
    }

    [Fact]
    public async Task EmptyTrash_KeepAppCode_LeavesWhatCameFromApps_Test()
    {
        var space = ContextRef.Space(Ulid.NewUlid().ToString());
        await Upload(".apps/board/app.js", 5, ctx: space);
        await Upload("notes.txt", 5, ctx: space);
        await _files.TrashAsync(space, ".apps/board/app.js", "u", Ct);
        await _files.TrashAsync(space, "notes.txt", "u", Ct);

        Assert.Equal(1, await _files.EmptyTrashAsync(space, keepAppCode: true, Ct));
        Assert.Equal(".apps/board/app.js", Assert.Single(await _files.ListTrashAsync(space, Ct)).OriginalPath);
        Assert.Equal(1, await _files.EmptyTrashAsync(space, Ct));
        Assert.Empty(await _files.ListTrashAsync(space, Ct));
    }

    // ───── transfer ─────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transfer_ReplacingTheSourcesOwnFolder_IsRefused_BeforeAnythingIsTrashed_Test(bool move)
    {
        await Upload("Photos/Photos/cat.jpg", 4);
        var result = await _files.TransferAsync(_me, new[] { "Photos/Photos" }, _me, "", move, FileConflictMode.Replace, "u", Ct);
        Assert.Equal("replaces_own_source", Assert.Single(result).Error);
        Assert.True(File.Exists(Path.Combine(Root(), "Photos", "Photos", "cat.jpg")));
        Assert.Empty(await _files.ListTrashAsync(_me, Ct));

        // Keep both still works.
        var renamed = await _files.TransferAsync(_me, new[] { "Photos/Photos" }, _me, "", move, FileConflictMode.Rename, "u", Ct);
        Assert.Equal("Photos (2)", Assert.Single(renamed).To);
    }

    // ───── rename temp ─────

    [Fact]
    public async Task Maintenance_LeavesARenamesTempName_SweepsOnlyUploadParts_Test()
    {
        await Upload("keep.txt", 1);
        var renaming = Path.Combine(Root(), DiskFileStore.RenameTempName());
        var part = Path.Combine(Root(), DiskFileStore.TempName());
        File.WriteAllText(renaming, "someone's file");
        File.WriteAllText(part, "x");
        File.SetLastWriteTimeUtc(renaming, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(part, DateTime.UtcNow.AddHours(-2));

        await _files.RunMaintenanceAsync(_me, Ct);
        Assert.True(File.Exists(renaming));
        Assert.False(File.Exists(part));

        // A case-only rename leaves nothing behind.
        await _files.MoveAsync(_me, "keep.txt", "KEEP.txt", "u", Ct);
        Assert.Single(Directory.EnumerateFiles(Root(), "*.rename"));
    }

    // ───── archive restore ─────

    private string NewUser(long? quota = null)
    {
        var id = "u_" + Guid.NewGuid().ToString("N")[..10];
        using var sys = _db.CreateSystemConnection();
        sys.Execute("INSERT INTO users(id, name, email, created_at, quota_bytes) VALUES (@id, 'U', @e, @now, @quota)",
            new { id, e = id + "@test", now = DateTime.UtcNow.ToString("o"), quota });
        return id;
    }

    // An archived space of `bytes` file bytes, its folder gone; returns the
    // archive id and what it unpacks to (all of it, and under files/).
    private async Task<(string Id, long Total, long Files)> ArchivedSpaceAsync(SpaceArchiver archiver, string owner, int bytes)
    {
        var space = await new SpaceRepository(_db).CreateAsync(owner, "Garden " + Guid.NewGuid().ToString("N")[..4], Ct);
        await Upload("plans.bin", bytes, ctx: ContextRef.Space(space.Id));
        var archived = await archiver.ArchiveAsync(space, owner, Ct);
        await archiver.DeleteSpaceFolderAsync(space.Id, Ct);
        long total = 0, files = 0;
        using (var zip = ZipFile.OpenRead(Path.Combine(_dataDir, "archive", "spaces", archived.Id + ".zip")))
            foreach (var e in zip.Entries.Where(e => e.FullName != "manifest.json"))
            {
                total += e.Length;
                if (e.FullName.StartsWith("files/", StringComparison.Ordinal)) files += e.Length;
            }
        return (archived.Id, total, files);
    }

    [Fact]
    public async Task Restore_IsBilledToTheRestoringOwner_EachTime_Test()
    {
        var archiver = new SpaceArchiver(_db, _system, files: _files);
        var owner = NewUser(quota: 0);
        var (id, total, _) = await ArchivedSpaceAsync(archiver, owner, 50_000);
        var used = (await _files.GetUsageAsync(ContextRef.User(owner), Ct)).OwnerBytes;
        using (var sys = _db.CreateSystemConnection())
            sys.Execute("UPDATE users SET quota_bytes = @q WHERE id = @owner", new { q = used + total + total / 2, owner });

        Assert.NotNull(await archiver.RestoreAsync(id, owner, Ct));
        var again = await Refused(() => archiver.RestoreAsync(id, owner, Ct));
        Assert.Equal(413, again.Status);
        Assert.Equal("user_quota", again.Field);
        Assert.Empty(Directory.EnumerateDirectories(_db.SpacesRoot, ".restoring-*"));
    }

    [Fact]
    public async Task Restore_RespectsTheWorkspaceQuota_AndTheServersCap_Test()
    {
        var archiver = new SpaceArchiver(_db, _system, files: _files);
        var owner = NewUser(quota: 0);
        var (id, _, files) = await ArchivedSpaceAsync(archiver, owner, 50_000);
        var spacesBefore = Directory.EnumerateDirectories(_db.SpacesRoot).Count();

        await _system.SetConfigAsync(FileLimits.QuotaBytesKey, (files - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Ct);
        Assert.Equal("quota", (await Refused(() => archiver.RestoreAsync(id, owner, Ct))).Field);
        await _system.SetConfigAsync(FileLimits.QuotaBytesKey, "0", Ct);

        await _system.SetConfigAsync(FileLimits.InstanceCapBytesKey, "1", Ct);
        Assert.Equal("instance", (await Refused(() => archiver.RestoreAsync(id, owner, Ct))).Field);
        Assert.Equal(spacesBefore, Directory.EnumerateDirectories(_db.SpacesRoot).Count());
    }
}
