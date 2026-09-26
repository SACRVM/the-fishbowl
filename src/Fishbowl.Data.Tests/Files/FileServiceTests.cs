using System.Security.Cryptography;
using System.Text;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Data.Files;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Files;

// The file store end to end below HTTP: DiskFileStore + FileService over a
// real temp data dir (spec § Testing — path safety, storage, journal).
public class FileServiceTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _db;
    private readonly SystemRepository _system;
    private readonly FileService _files;
    private readonly ContextRef _me = ContextRef.User("files_user");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FileServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_files_" + Path.GetRandomFileName());
        _db = new DatabaseFactory(_dataDir);
        _system = new SystemRepository(_db);
        _files = new FileService(_db, _system);
        // The dev disk may be fuller than 1 GiB free; the guard has its own test.
        _system.SetConfigAsync(FileLimits.MinFreeBytesKey, "0").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Root(ContextRef? ctx = null) => Path.Combine(_db.ResolveContextFolder(ctx ?? _me), "files");
    private bool CaseSensitive => FileVolume.IsCaseSensitive(_db.ResolveContextFolder(_me));

    private Task<FileUploadResult> Put(string path, string text, bool create = true, string? ifMatch = null, ContextRef? ctx = null, bool parents = false)
        => Put(path, Encoding.UTF8.GetBytes(text), create, ifMatch, ctx, parents);

    private Task<FileUploadResult> Put(string path, byte[] bytes, bool create = true, string? ifMatch = null, ContextRef? ctx = null, bool parents = false)
        => _files.UploadAsync(ctx ?? _me, path, new MemoryStream(bytes), bytes.Length, ifMatch, create, parents, "files_user", Ct);

    private static async Task<FileStoreException> Refused(Func<Task> act)
        => await Assert.ThrowsAsync<FileStoreException>(act);

    private async Task<List<string>> Names(string folder = "", ContextRef? ctx = null)
        => (await _files.ListAsync(ctx ?? _me, folder, null, null, Ct)).Entries.Select(e => e.Name).ToList();

    private async Task<List<FileChange>> AllChanges(ContextRef? ctx = null)
    {
        await _system.SetConfigAsync(FileLimits.ReconcileIntervalSecondsKey, "0", Ct);
        var head = await _files.GetChangesAsync(ctx ?? _me, null, null, Ct);
        var epoch = head.Cursor[..head.Cursor.LastIndexOf(':')];
        var page = await _files.GetChangesAsync(ctx ?? _me, epoch + ":0", 1000, Ct);
        return page.Changes.ToList();
    }

    // ───── path safety ─────

    [Theory]
    [InlineData("..")]
    [InlineData("a/../../x")]
    [InlineData("../x")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\?\\C:\\x")]
    [InlineData("a\\b")]
    [InlineData("..\\x")]
    [InlineData("C:\\x")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData(".trash")]
    [InlineData(".trash/x")]
    [InlineData(".~fb-x")]
    [InlineData("a/.~fb-y.part")]
    public async Task Resolver_RefusesTraversalAndReservedNames(string path)
    {
        var ex = await Refused(() => Put(path, "x"));
        Assert.Equal(400, ex.Status);
        ex = await Refused(() => _files.StatAsync(_me, path, Ct));
        Assert.Equal(400, ex.Status);
    }

    [Theory]
    [InlineData("C:x")]
    [InlineData("a:stream")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("name.")]
    public async Task Resolver_WindowsOnlyNames(string path)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows name rules");
        var ex = await Refused(() => Put(path, "x"));
        Assert.Equal(400, ex.Status);
        // Drive syntax ("C:x", "a:stream") is already refused as rooted.
        Assert.Contains(ex.Code, new[] { "invalid_name", "invalid_path" });
        if (ex.Code == "invalid_name") Assert.NotNull(ex.Rule);
    }

    [Fact]
    public async Task Resolver_LinuxAcceptsWhatWindowsRefuses()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) Assert.Skip("Linux name rules");
        Assert.True((await Put("CON", "x")).Created);
        Assert.True((await Put("a:b", "x")).Created);
    }

    [Fact]
    public async Task Links_AreListedButNeverFollowed()
    {
        Directory.CreateDirectory(Root());
        var outside = Path.Combine(_dataDir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "nope");
        try { Directory.CreateSymbolicLink(Path.Combine(Root(), "out"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("This account can't create symlinks.");
        }
        Directory.CreateDirectory(Path.Combine(Root(), "inside"));
        try { Directory.CreateSymbolicLink(Path.Combine(Root(), "in"), Path.Combine(Root(), "inside")); }
        catch (IOException) { }

        foreach (var p in new[] { "out", "out/secret.txt", "in", "in/x.txt" })
        {
            var ex = await Refused(() => _files.OpenReadAsync(_me, p, Ct));
            Assert.Equal("link_not_followed", ex.Code);
        }
        await Refused(() => Put("out/new.txt", "x"));
        var list = await _files.ListAsync(_me, "", null, null, Ct);
        Assert.Equal(FileKinds.Link, list.Entries.Single(e => e.Name == "out").Kind);
        Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
    }

    // ───── uploads ─────

    [Fact]
    public async Task Upload_CreateReplace_AndPreconditions()
    {
        var first = await Put("notes.txt", "one");
        Assert.True(first.Created);
        Assert.Equal("text/plain", first.Entry.Mime);

        Assert.Equal(412, (await Refused(() => Put("notes.txt", "again"))).Status);                   // If-None-Match: *
        Assert.Equal(412, (await Refused(() => Put("notes.txt", "x", create: false, ifMatch: "\"3-1\""))).Status);
        Assert.Equal(428, (await Refused(() => _files.UploadAsync(_me, "n2.txt", new MemoryStream(), 0, null, false, false, "u", Ct))).Status);

        var replaced = await Put("notes.txt", "two!", create: false, ifMatch: $"\"{first.Entry.Etag}\"");
        Assert.False(replaced.Created);
        Assert.Equal("two!", await File.ReadAllTextAsync(Path.Combine(Root(), "notes.txt"), Ct));
        Assert.Equal(404, (await Refused(() => Put("missing/f.txt", "x"))).Status);
        Assert.True((await Put("made/deep/f.txt", "x", parents: true)).Created);
    }

    [Fact]
    public async Task Upload_StreamingHashMatches()
    {
        var bytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        var result = await Put("big.bin", bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Entry.Sha256);
        Assert.Equal(bytes.Length, result.Entry.Size);
        Assert.Equal(FileSniffer.OctetStream, result.Entry.Mime);
    }

    private sealed class FailingStream(int failAfter) : Stream
    {
        private int _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= failAfter) throw new IOException("client went away");
            var n = Math.Min(count, failAfter - _read);
            buffer.AsSpan(offset, n).Fill((byte)'x');
            _read += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Upload_Aborted_LeavesNothingBehind()
    {
        await _files.CreateFolderAsync(_me, "in", "u", Ct);
        var before = (await AllChanges()).Count;
        await Assert.ThrowsAsync<IOException>(() =>
            _files.UploadAsync(_me, "in/half.bin", new FailingStream(200_000), null, null, true, false, "u", Ct));
        Assert.Empty(Directory.GetFiles(Path.Combine(Root(), "in")));
        Assert.Equal(before, (await AllChanges()).Count);
    }

    [Fact]
    public async Task Limits_MaxFileBytes_ByLengthAndMidStream_AndQuota()
    {
        await _system.SetConfigAsync(FileLimits.MaxFileBytesKey, "10", Ct);
        var byLength = await Refused(() => Put("a.bin", new byte[20]));
        Assert.Equal((413, "size"), (byLength.Status, byLength.Field));
        var midStream = await Refused(() => _files.UploadAsync(_me, "b.bin", new MemoryStream(new byte[20]), null, null, true, false, "u", Ct));
        Assert.Equal((413, "size"), (midStream.Status, midStream.Field));
        Assert.Empty(Directory.GetFiles(Root()));

        // Config changes apply without a restart.
        await _system.SetConfigAsync(FileLimits.MaxFileBytesKey, "1000", Ct);
        await _system.SetConfigAsync(FileLimits.QuotaBytesKey, "15", Ct);
        Assert.True((await Put("c.bin", new byte[10])).Created);
        var quota = await Refused(() => Put("d.bin", new byte[10]));
        Assert.Equal((413, "quota"), (quota.Status, quota.Field));
    }

    [Fact]
    public async Task MinFree_Guard_Is507()
    {
        await _system.SetConfigAsync(FileLimits.MinFreeBytesKey, long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), Ct);
        Assert.Equal(507, (await Refused(() => Put("x.txt", "x"))).Status);
    }

    [Fact]
    public async Task Names_AreStoredNfc_AndClashByTheVolumesCaseRule()
    {
        await Put("e\u0301.txt", "x");                        // NFD in
        Assert.Contains("\u00e9.txt", await Names());           // NFC on disk

        await Put("a.txt", "x");
        if (CaseSensitive) Assert.True((await Put("A.txt", "y")).Created);
        else Assert.Equal(412, (await Refused(() => Put("A.txt", "y"))).Status);
    }

    // ───── move / copy / rename ─────

    [Fact]
    public async Task Move_Rename_CaseOnly_AndNotIntoItself()
    {
        await _files.CreateFolderAsync(_me, "a", "u", Ct);
        await _files.CreateFolderAsync(_me, "a/b", "u", Ct);
        await Put("a/b/f.txt", "x");
        Assert.Equal(409, (await Refused(() => _files.CreateFolderAsync(_me, "a", "u", Ct))).Status);

        await _files.MoveAsync(_me, "a", "c", "u", Ct);
        Assert.Equal(new[] { "f.txt" }, await Names("c/b"));
        Assert.Equal("into_own_subtree", (await Refused(() => _files.MoveAsync(_me, "c", "c/b/c", "u", Ct))).Code);
        Assert.Equal(409, (await Refused(() => _files.MoveAsync(_me, "c/b/f.txt", "c/b", "u", Ct))).Status);

        await _files.MoveAsync(_me, "c/b/f.txt", "c/b/F.txt", "u", Ct);
        Assert.Equal(new[] { "F.txt" }, await Names("c/b"));

        var changes = await AllChanges();
        Assert.Single(changes, c => c.Op == "move" && c.OldPath == "a" && c.Path == "c" && c.Kind == FileKinds.Folder);
    }

    [Fact]
    public async Task Copy_IsARealCopy_FolderRecursive_JournalledPerNode()
    {
        await _files.CreateFolderAsync(_me, "src", "u", Ct);
        await Put("src/one.txt", "1");
        await _files.CreateFolderAsync(_me, "src/sub", "u", Ct);
        await Put("src/sub/two.txt", "2");

        await _files.CopyAsync(_me, "src", "dst", "u", Ct);
        await File.WriteAllTextAsync(Path.Combine(Root(), "dst", "one.txt"), "changed", Ct);
        Assert.Equal("1", await File.ReadAllTextAsync(Path.Combine(Root(), "src", "one.txt"), Ct));
        Assert.Equal("2", await File.ReadAllTextAsync(Path.Combine(Root(), "dst", "sub", "two.txt"), Ct));
        Assert.Equal("into_own_subtree", (await Refused(() => _files.CopyAsync(_me, "src", "src/sub/x", "u", Ct))).Code);

        var creates = (await AllChanges()).Where(c => c.Op == "create" && c.Path.StartsWith("dst", StringComparison.Ordinal)).Select(c => c.Path).ToList();
        Assert.Equal(new[] { "dst", "dst/one.txt", "dst/sub", "dst/sub/two.txt" }, creates.Order(StringComparer.Ordinal));
    }

    // ───── trash ─────

    [Fact]
    public async Task Trash_Restore_Conflicts_Purge()
    {
        await _files.CreateFolderAsync(_me, "docs", "u", Ct);
        await Put("docs/lease.pdf", "%PDF-1.4 x");
        var t = await _files.TrashAsync(_me, "docs/lease.pdf", "u", Ct);
        Assert.Empty(await Names("docs"));
        Assert.Equal("docs/lease.pdf", Assert.Single(await _files.ListTrashAsync(_me, Ct)).OriginalPath);
        Assert.Single(await AllChanges(), c => c.Op == "delete" && c.Trashed);

        // Missing parents come back.
        await _files.TrashAsync(_me, "docs", "u", Ct);
        await _files.RestoreAsync(_me, t.Id, FileConflictMode.Fail, "u", Ct);
        Assert.Equal(new[] { "lease.pdf" }, await Names("docs"));

        var again = await _files.TrashAsync(_me, "docs/lease.pdf", "u", Ct);
        await Put("docs/lease.pdf", "new");
        Assert.Equal(409, (await Refused(() => _files.RestoreAsync(_me, again.Id, FileConflictMode.Fail, "u", Ct))).Status);
        var restored = await _files.RestoreAsync(_me, again.Id, FileConflictMode.Rename, "u", Ct);
        Assert.Equal("lease (restored).pdf", restored.Name);

        await _files.TrashAsync(_me, "docs/lease.pdf", "u", Ct);
        var usage = await _files.GetUsageAsync(_me, Ct);
        Assert.True(usage.TrashBytes > 0);
        Assert.True(await _files.EmptyTrashAsync(_me, Ct) >= 1);
        Assert.Empty(await _files.ListTrashAsync(_me, Ct));
        Assert.Equal(404, (await Refused(() => _files.RestoreAsync(_me, again.Id, FileConflictMode.Fail, "u", Ct))).Status);
        Assert.Equal(404, (await Refused(() => _files.RestoreAsync(_me, "../../etc", FileConflictMode.Fail, "u", Ct))).Status);
    }

    [Fact]
    public async Task DeletePermanently_IsJournalledAsNotTrashed()
    {
        await Put("gone.txt", "x");
        await _files.DeletePermanentlyAsync(_me, "gone.txt", "u", Ct);
        Assert.False(File.Exists(Path.Combine(Root(), "gone.txt")));
        Assert.Single(await AllChanges(), c => c.Op == "delete" && !c.Trashed && c.Path == "gone.txt");
    }

    // ───── journal, cursor, reconcile ─────

    [Fact]
    public async Task OutOfBandChanges_ShowUpAsSourceDisk()
    {
        await Put("known.txt", "x");
        await _files.CreateFolderAsync(_me, "box", "u", Ct);
        File.WriteAllText(Path.Combine(Root(), "box", "dropped.txt"), "from the desktop");
        Assert.Contains("dropped.txt", await Names("box"));                  // on list
        File.WriteAllText(Path.Combine(Root(), "known.txt"), "edited outside!");
        File.Delete(Path.Combine(Root(), "box", "dropped.txt"));
        Directory.CreateDirectory(Path.Combine(Root(), "newdir", "inner"));

        var changes = await AllChanges();                                     // full scan
        Assert.Contains(changes, c => c is { Op: "create", Path: "box/dropped.txt", Source: "disk" });
        Assert.Contains(changes, c => c is { Op: "modify", Path: "known.txt", Source: "disk" });
        Assert.Contains(changes, c => c is { Op: "delete", Path: "box/dropped.txt", Source: "disk" });
        Assert.Contains(changes, c => c is { Op: "create", Path: "newdir/inner", Source: "disk" });
    }

    [Fact]
    public async Task Cursor_ExpiredForeignOrAhead_Is410_AndCompactionRaisesTheFloor()
    {
        await Put("one.txt", "1");
        var head = await _files.GetChangesAsync(_me, null, null, Ct);
        var epoch = head.Cursor[..head.Cursor.LastIndexOf(':')];
        await Put("two.txt", "2");

        var page = await _files.GetChangesAsync(_me, head.Cursor, null, Ct);
        Assert.Equal("two.txt", Assert.Single(page.Changes).Path);

        foreach (var bad in new[] { "garbage", $"{Ulid.NewUlid()}:1", $"{epoch}:999999" })
            Assert.Equal(410, (await Refused(() => _files.GetChangesAsync(_me, bad, null, Ct))).Status);

        // Age every entry past the retention, run the daily tick: the floor
        // moves up and an old cursor is gone.
        using (var db = _db.CreateContextConnection(_me))
            db.Execute("UPDATE file_changes SET at = '2000-01-01T00:00:00.0000000Z'");
        await _files.RunMaintenanceAsync(_me, Ct);
        Assert.Equal(410, (await Refused(() => _files.GetChangesAsync(_me, head.Cursor, null, Ct))).Status);
        var fresh = await _files.GetChangesAsync(_me, null, null, Ct);
        Assert.Empty((await _files.GetChangesAsync(_me, fresh.Cursor, null, Ct)).Changes);
    }

    [Fact]
    public async Task SnapshotPlusFeed_ConvergesWithTheDisk()
    {
        await _system.SetConfigAsync(FileLimits.ReconcileIntervalSecondsKey, "0", Ct);
        await _files.CreateFolderAsync(_me, "a", "u", Ct);
        await Put("a/1.txt", "1");
        var (cursor, snap) = await _files.SnapshotAsync(_me, Ct);
        var client = snap.ToDictionary(e => e.Path, e => e.Kind, StringComparer.Ordinal);

        // API and out-of-band changes mixed.
        await Put("a/2.txt", "2");
        await _files.MoveAsync(_me, "a", "b", "u", Ct);
        await _files.CopyAsync(_me, "b", "c", "u", Ct);
        await _files.TrashAsync(_me, "c/1.txt", "u", Ct);
        File.WriteAllText(Path.Combine(Root(), "b", "oob.txt"), "x");
        Directory.CreateDirectory(Path.Combine(Root(), "d", "e"));
        File.WriteAllText(Path.Combine(Root(), "d", "e", "deep.txt"), "x");
        File.Delete(Path.Combine(Root(), "b", "2.txt"));

        var page = await _files.GetChangesAsync(_me, cursor, 1000, Ct);
        foreach (var c in page.Changes)
        {
            switch (c.Op)
            {
                case "create" or "modify": client[c.Path] = c.Kind; break;
                case "delete":
                    foreach (var k in client.Keys.Where(k => k == c.Path || k.StartsWith(c.Path + "/", StringComparison.Ordinal)).ToList()) client.Remove(k);
                    break;
                case "move":
                    foreach (var k in client.Keys.Where(k => k == c.OldPath || k.StartsWith(c.OldPath + "/", StringComparison.Ordinal)).ToList())
                    {
                        var kind = client[k];
                        client.Remove(k);
                        client[c.Path + k[c.OldPath!.Length..]] = kind;
                    }
                    break;
            }
        }
        var disk = DiskFileStore.Walk(new FilePathResolver(Root(), FileNameRules.ForHost(), CaseSensitive, true), "")
            .ToDictionary(d => d.Rel, d => d.Kind, StringComparer.Ordinal);
        Assert.Equal(disk.OrderBy(x => x.Key, StringComparer.Ordinal), client.OrderBy(x => x.Key, StringComparer.Ordinal));
    }

    // ───── sniffing ─────

    [Theory]
    [InlineData("pic.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, "image/png")]
    [InlineData("pic.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2 }, "image/jpeg")]
    [InlineData("doc.pdf", new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', (byte)'1' }, "application/pdf")]
    [InlineData("song.mp3", new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0 }, "audio/mpeg")]
    [InlineData("blob.dat", new byte[] { 0, 1, 2, 3, 0, 5 }, FileSniffer.OctetStream)]
    public void Sniff_ByMagic(string name, byte[] head, string mime)
        => Assert.Equal(mime, FileSniffer.Sniff(head, name));

    [Theory]
    [InlineData("readme.md", "# Title\nText", "text/markdown")]
    [InlineData("notes.txt", "plain words", "text/plain")]
    [InlineData("fake.png", "<!DOCTYPE html><script>alert(1)</script>", "text/html")]
    [InlineData("fake.pdf", "<html><body>hi</body></html>", "text/html")]
    [InlineData("fake.txt", "<svg xmlns='http://www.w3.org/2000/svg'/>", "image/svg+xml")]
    [InlineData("data.pdf", "not really a pdf", FileSniffer.OctetStream)]
    public void Sniff_Text_MarkupIsNeverTakenForSafe(string name, string text, string mime)
    {
        Assert.Equal(mime, FileSniffer.Sniff(Encoding.UTF8.GetBytes(text), name));
        if (mime is "text/html" or "image/svg+xml") Assert.False(FileSniffer.IsInlineSafe(mime));
    }

    // ───── maintenance ─────

    [Fact]
    public async Task Maintenance_SweepsStaleParts_AndPurgesOldTrash()
    {
        await Put("keep.txt", "x");
        var stale = Path.Combine(Root(), ".~fb-stale.part");
        var fresh = Path.Combine(Root(), ".~fb-fresh.part");
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        var t = await _files.TrashAsync(_me, "keep.txt", "u", Ct);

        await _system.SetConfigAsync(FileLimits.TrashRetentionDaysKey, "0", Ct);   // 0 = never
        await _files.RunMaintenanceAsync(_me, Ct);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.Single(await _files.ListTrashAsync(_me, Ct));

        await _system.SetConfigAsync(FileLimits.TrashRetentionDaysKey, "30", Ct);
        using (var db = _db.CreateContextConnection(_me))
            db.Execute("UPDATE file_trash SET deleted_at = '2000-01-01T00:00:00.0000000Z' WHERE id = @id", new { id = t.Id });
        await _files.RunMaintenanceAsync(_me, Ct);
        Assert.Empty(await _files.ListTrashAsync(_me, Ct));
    }

    // ───── transfer ─────

    [Fact]
    public async Task Transfer_PersonalToSpace_CopyThenMove_WithConflicts()
    {
        var space = ContextRef.Space(Ulid.NewUlid().ToString());
        await _files.CreateFolderAsync(_me, "out", "u", Ct);
        await Put("out/a.txt", "a");
        await Put("out/b.txt", "b");

        var copied = await _files.TransferAsync(_me, new[] { "out/a.txt" }, space, "", false, FileConflictMode.Fail, "u", Ct);
        Assert.Null(Assert.Single(copied).Error);
        Assert.Contains("a.txt", await Names("", space));
        Assert.Contains("a.txt", await Names("out"));

        var clash = await _files.TransferAsync(_me, new[] { "out/a.txt" }, space, "", false, FileConflictMode.Fail, "u", Ct);
        Assert.Equal("name_exists", Assert.Single(clash).Error);
        var renamed = await _files.TransferAsync(_me, new[] { "out/a.txt" }, space, "", false, FileConflictMode.Rename, "u", Ct);
        Assert.Equal("a (2).txt", Assert.Single(renamed).To);

        var moved = await _files.TransferAsync(_me, new[] { "out" }, space, "", true, FileConflictMode.Fail, "u", Ct);
        Assert.Null(Assert.Single(moved).Error);
        Assert.DoesNotContain("out", await Names());
        Assert.Equal(new[] { "a.txt", "b.txt" }, (await Names("out", space)).Order(StringComparer.Ordinal));

        Assert.Contains(await AllChanges(), c => c is { Op: "delete", Path: "out", Trashed: false });
        Assert.Contains(await AllChanges(space), c => c is { Op: "create", Path: "out/b.txt" });
    }
}
