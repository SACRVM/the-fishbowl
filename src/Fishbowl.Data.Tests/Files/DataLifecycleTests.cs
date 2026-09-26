using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Core.Models;
using Fishbowl.Data.Files;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Files;

// Files phase 3 below HTTP: archived spaces (archive, restore, purge) and the
// per-user quota that covers the personal workspace plus owned spaces.
public class DataLifecycleTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _db;
    private readonly SystemRepository _system;
    private readonly SpaceRepository _spaces;
    private readonly MessageRepository _messages;
    private readonly FileService _files;
    private readonly SpaceArchiver _archiver;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DataLifecycleTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_lifecycle_" + Path.GetRandomFileName());
        _db = new DatabaseFactory(_dataDir);
        _system = new SystemRepository(_db);
        _spaces = new SpaceRepository(_db);
        _messages = new MessageRepository(_db);
        _files = new FileService(_db, _system, null, _messages);
        _archiver = new SpaceArchiver(_db, _system);
        _system.SetConfigAsync(FileLimits.MinFreeBytesKey, "0").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string NewUser(long? quota = null)
    {
        var id = "u_" + Guid.NewGuid().ToString("N")[..10];
        using var sys = _db.CreateSystemConnection();
        sys.Execute("INSERT INTO users(id, name, email, created_at, quota_bytes) VALUES (@id, 'U', @e, @now, @quota)",
            new { id, e = id + "@test", now = DateTime.UtcNow.ToString("o"), quota });
        return id;
    }

    private Task<FileUploadResult> Put(ContextRef ctx, string path, int bytes, string actor = "t")
    {
        var data = Encoding.ASCII.GetBytes(new string('x', bytes));
        return _files.UploadAsync(ctx, path, new MemoryStream(data), data.Length, null, true, true, actor, Ct);
    }

    private string ArchiveRoot => Path.Combine(_dataDir, "archive", "spaces");

    // ───────────────────────────── archive / restore ─────────────────────────────

    [Fact]
    public async Task Archive_ThenRestore_NewSpace_SoleOwner_EpochRotated_Test()
    {
        var owner = NewUser();
        var member = NewUser();
        var space = await _spaces.CreateAsync(owner, "Garden Club", Ct);
        using (var sys = _db.CreateSystemConnection())
            sys.Execute("INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@s, @u, 'member', @now)",
                new { s = space.Id, u = member, now = DateTime.UtcNow.ToString("o") });
        var ctx = ContextRef.Space(space.Id);
        await Put(ctx, "plans/beds.txt", 12);
        await Put(ctx, "old.txt", 3);
        await _files.TrashAsync(ctx, "old.txt", "t", Ct);
        string oldEpoch;
        using (var conn = _db.CreateContextConnection(ctx))
            oldEpoch = conn.ExecuteScalar<string>("SELECT epoch FROM file_state WHERE id = 1")!;

        var archived = await _archiver.ArchiveAsync(space, owner, Ct);
        Assert.StartsWith(space.Id + "-", archived.Id);
        Assert.NotNull(archived.ExpiresAt);   // default retention: 30 days
        Assert.InRange((archived.ExpiresAt!.Value - archived.ArchivedAt).TotalDays, 29.9, 30.1);

        // The ZIP: manifest (members kept for the record), DB, files incl. trash.
        using (var zip = ZipFile.OpenRead(Path.Combine(ArchiveRoot, archived.Id + ".zip")))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("manifest.json", names);
            Assert.Contains("space.db", names);
            Assert.Contains("files/plans/beds.txt", names);
            Assert.Contains(names, n => n.StartsWith("files/.trash/"));
            using var m = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
            Assert.Equal(2, m.RootElement.GetProperty("members").GetArrayLength());
            Assert.Equal(owner, m.RootElement.GetProperty("ownerId").GetString());
        }

        // Delete the space the way the API does, then take its slug.
        Assert.True(await _spaces.DeleteAsync(space.Id, owner, Ct));
        await _archiver.DeleteSpaceFolderAsync(space.Id, Ct);
        Assert.False(Directory.Exists(_db.ResolveContextFolder(ctx)));
        var squatter = await _spaces.CreateAsync(member, "Garden Club", Ct);
        Assert.Equal(space.Slug, squatter.Slug);

        // Listed to the owner (and the archiver), not to a former member.
        Assert.Single(await _archiver.ListAsync(owner, Ct));
        Assert.Empty(await _archiver.ListAsync(member, Ct));
        Assert.Null(await _archiver.RestoreAsync(archived.Id, member, Ct));

        var restored = await _archiver.RestoreAsync(archived.Id, owner, Ct);
        Assert.NotNull(restored);
        Assert.NotEqual(space.Id, restored!.Id);
        Assert.Equal(space.Slug + "-2", restored.Slug);
        Assert.Equal(SpaceRole.Owner, await _spaces.GetMembershipAsync(restored.Id, owner, Ct));
        Assert.Null(await _spaces.GetMembershipAsync(restored.Id, member, Ct));

        var rctx = ContextRef.Space(restored.Id);
        using (var conn = _db.CreateContextConnection(rctx))
            Assert.NotEqual(oldEpoch, conn.ExecuteScalar<string>("SELECT epoch FROM file_state WHERE id = 1"));
        var entries = (await _files.ListAsync(rctx, "plans", null, null, Ct)).Entries;
        Assert.Contains(entries, e => e.Name == "beds.txt" && e.Size == 12);
        Assert.Single(await _files.ListTrashAsync(rctx, Ct));
        Assert.Empty(Directory.EnumerateDirectories(_db.SpacesRoot, ".restoring-*"));
    }

    [Fact]
    public async Task Archive_IdIsAFileName_NeverAPath_Test()
    {
        var owner = NewUser();
        Assert.Null(await _archiver.GetPathAsync("../system", owner, Ct));
        Assert.Null(await _archiver.GetPathAsync(".hidden", owner, Ct));
        Assert.False(await _archiver.DeleteAsync("..\\x", owner, Ct));
    }

    [Fact]
    public async Task Purge_RespectsRetention_IncludingZeroKeepsForever_Test()
    {
        var owner = NewUser();
        var fresh = await _archiver.ArchiveAsync(await _spaces.CreateAsync(owner, "Fresh", Ct), owner, Ct);
        var old = await _archiver.ArchiveAsync(await _spaces.CreateAsync(owner, "Old", Ct), owner, Ct);
        Backdate(old.Id, days: 40);

        await _system.SetConfigAsync(DataLifecycleLimits.ArchiveRetentionDaysKey, "0", Ct);
        Assert.Equal(0, await _archiver.PurgeAsync(Ct));
        Assert.Equal(2, (await _archiver.ListAsync(owner, Ct)).Count);
        Assert.All(await _archiver.ListAsync(owner, Ct), a => Assert.Null(a.ExpiresAt));

        await _system.SetConfigAsync(DataLifecycleLimits.ArchiveRetentionDaysKey, "30", Ct);
        Assert.Equal(1, await _archiver.PurgeAsync(Ct));
        Assert.Equal(fresh.Id, Assert.Single(await _archiver.ListAsync(owner, Ct)).Id);
    }

    // Rewrites an archive's manifest as if it had been made `days` ago.
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

    [Fact]
    public async Task DeleteSpaceFolder_Missing_IsANoOp_Test()
        => await _archiver.DeleteSpaceFolderAsync(Ulid.NewUlid().ToString(), Ct);

    // ───────────────────────────── quota ─────────────────────────────

    [Fact]
    public async Task Quota_CoversPersonalAndOwnedSpaces_WarnsOnce_ThenRefuses_Test()
    {
        var user = NewUser();
        var me = ContextRef.User(user);
        var space = await _spaces.CreateAsync(user, "Mine", Ct);
        var sctx = ContextRef.Space(space.Id);
        // Materialise both workspaces first so the baseline includes their DBs.
        await Put(me, "seed.txt", 1);
        await Put(sctx, "seed.txt", 1);
        var baseline = DiskFileStore.Measure(_db.ResolveContextFolder(me)).Bytes
                       + DiskFileStore.Measure(_db.ResolveContextFolder(sctx)).Bytes;
        var quota = baseline + 1_000_000;
        using (var sys = _db.CreateSystemConnection())
            sys.Execute("UPDATE users SET quota_bytes = @quota WHERE id = @user", new { quota, user });

        var usage = await _files.GetUsageAsync(me, Ct);
        Assert.Equal(quota, usage.OwnerQuotaBytes);
        Assert.InRange(usage.OwnerBytes, baseline - 1, baseline + 64 * 1024);

        await Put(me, "a.bin", 400_000);
        Assert.Equal(0, await _messages.CountUnreadAsync(user, Ct));

        // The space bills its owner: this crosses 90% of the one quota.
        var toCross = (int)(quota * 0.9 - (usage.OwnerBytes + 400_000)) + 10_000;
        await Put(sctx, "b.bin", toCross);
        var warnings = (await _messages.ListAsync(user, false, Ct)).Where(m => m.Kind == MessageKinds.QuotaWarning).ToList();
        Assert.Single(warnings);
        Assert.Contains("quotaBytes", warnings[0].Data);

        await Put(me, "c.bin", 10);   // still over 90%: no second message
        Assert.Single(await _messages.ListAsync(user, false, Ct), m => m.Kind == MessageKinds.QuotaWarning);

        var over = await Assert.ThrowsAsync<FileStoreException>(() => Put(sctx, "d.bin", 400_000));
        Assert.Equal(413, over.Status);
        Assert.Equal("user_quota", over.Field);
        Assert.Contains("space owner", over.Message);
        var overPersonal = await Assert.ThrowsAsync<FileStoreException>(() => Put(me, "e.bin", 400_000));
        Assert.Equal("user_quota", overPersonal.Field);
        Assert.False(File.Exists(Path.Combine(_db.ResolveContextFolder(me), "files", "e.bin")));
    }

    [Fact]
    public async Task Quota_Zero_IsUnlimited_And_NoUserRow_IsUnchecked_Test()
    {
        var unlimited = NewUser(quota: 0);
        await Put(ContextRef.User(unlimited), "big.bin", 300_000);
        Assert.Equal(0, (await _files.GetUsageAsync(ContextRef.User(unlimited), Ct)).OwnerQuotaBytes);
        Assert.Equal(0, await _messages.CountUnreadAsync(unlimited, Ct));
        // A workspace with no user row (a cold import mid-flight) isn't blocked.
        await Put(ContextRef.User("nobody_" + Guid.NewGuid().ToString("N")[..6]), "x.bin", 10);
    }

    [Fact]
    public async Task Quota_WarningLatch_ClearsBelowThreshold_AndFiresAgain_Test()
    {
        var user = NewUser();
        var me = ContextRef.User(user);
        await Put(me, "seed.txt", 1);
        var baseline = DiskFileStore.Measure(_db.ResolveContextFolder(me)).Bytes;
        var quota = baseline + 100_000;
        using (var sys = _db.CreateSystemConnection())
            sys.Execute("UPDATE users SET quota_bytes = @quota WHERE id = @user", new { quota, user });

        await Put(me, "big.bin", 95_000);
        Assert.Equal(1, await _messages.CountUnreadAsync(user, Ct));
        await _files.DeletePermanentlyAsync(me, "big.bin", "t", Ct);
        await _files.GetUsageAsync(me, Ct);   // re-measures, clears the latch
        await Put(me, "big2.bin", 95_000);
        Assert.Equal(2, await _messages.CountUnreadAsync(user, Ct));
    }
}
