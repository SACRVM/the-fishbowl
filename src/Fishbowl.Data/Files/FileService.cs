using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Files;

// The Files app's single entry point (docs/superpowers/specs/2026-09-26-files-app-design.md).
//
// The folder files/ inside the context folder is the truth; the v9 tables
// only index it, journal its changes and keep trash bookkeeping. Every
// mutation runs under a per-context lock: the disk operation first, then one
// transaction updating file_index and appending to file_changes. A crash in
// between leaves the disk ahead of the DB, which the next reconcile records
// as source = 'disk' — reconcile is the repair path, no two-phase commit.
//
// Never logs names or paths (PII): a short hash of the path stands in.
public sealed class FileService : IFileService
{
    private const string FilesFolder = "files";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> UsageCache = new(StringComparer.Ordinal);
    private static (DateTime At, long Bytes) _instanceUsage = (DateTime.MinValue, 0);
    private static readonly TimeSpan PartMaxAge = TimeSpan.FromHours(1);

    private readonly DatabaseFactory _dbFactory;
    private readonly ISystemRepository _system;
    private readonly ILogger<FileService> _logger;
    private readonly IMessageRepository? _messages;

    public FileService(DatabaseFactory dbFactory, ISystemRepository system, ILogger<FileService>? logger = null,
        IMessageRepository? messages = null)
    {
        _dbFactory = dbFactory;
        _system = system;
        _logger = logger ?? NullLogger<FileService>.Instance;
        _messages = messages;
    }

    // ───────────────────────────── plumbing ─────────────────────────────

    private FilePathResolver Open(ContextRef ctx)
    {
        if (ctx.Type is not (ContextType.User or ContextType.Space))
            throw new FileStoreException(403, "forbidden", "Files belong to a personal or a space workspace.");
        // Opening the DB creates the context folder and runs the v9 migration.
        using (_dbFactory.CreateContextConnection(ctx)) { }
        var folder = _dbFactory.ResolveContextFolder(ctx);
        return new FilePathResolver(Path.Combine(folder, FilesFolder), FileNameRules.ForHost(),
            FileVolume.IsCaseSensitive(folder), FileVolume.LongPathsEnabled);
    }

    private static string LockKey(FilePathResolver r) => r.CaseSensitive ? r.Root : r.Root.ToUpperInvariant();

    private static async Task<IDisposable> LockAsync(FilePathResolver r, CancellationToken ct)
    {
        var sem = Locks.GetOrAdd(LockKey(r), static _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }

    // Two contexts at once (transfer): always in the same order, so two
    // opposite transfers can't deadlock.
    private static async Task<IDisposable> LockBothAsync(FilePathResolver a, FilePathResolver b, CancellationToken ct)
    {
        if (LockKey(a) == LockKey(b)) return await LockAsync(a, ct);
        var (first, second) = string.CompareOrdinal(LockKey(a), LockKey(b)) < 0 ? (a, b) : (b, a);
        var l1 = await LockAsync(first, ct);
        try { return new Both(l1, await LockAsync(second, ct)); }
        catch { l1.Dispose(); throw; }
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) sem.Release(); }
    }

    private sealed class Both(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose() { b.Dispose(); a.Dispose(); }
    }

    private static string Hash(string rel)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rel)))[..8];

    private static string Iso(DateTime? d) => d is { } v ? v.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : "";

    private static DateTime? ParseIso(string? s)
        => string.IsNullOrEmpty(s) ? null : DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private async Task<FileSettings> SettingsAsync(CancellationToken ct)
    {
        async Task<long> Long(string key, long fallback)
        {
            var v = await _system.GetConfigAsync(key, ct);
            return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && x >= 0 ? x : fallback;
        }
        var d = FileSettings.Defaults;
        return new FileSettings(
            await Long(FileLimits.MaxFileBytesKey, d.MaxFileBytes),
            await Long(FileLimits.QuotaBytesKey, d.QuotaBytes),
            await Long(FileLimits.InstanceCapBytesKey, d.InstanceCapBytes),
            await Long(FileLimits.MinFreeBytesKey, d.MinFreeBytes),
            (int)Math.Min(int.MaxValue, await Long(FileLimits.TrashRetentionDaysKey, d.TrashRetentionDays)),
            (int)Math.Min(int.MaxValue, await Long(FileLimits.JournalRetentionDaysKey, d.JournalRetentionDays)),
            (int)Math.Min(int.MaxValue, await Long(FileLimits.ReconcileIntervalSecondsKey, d.ReconcileIntervalSeconds)));
    }

    // ───────────────────────────── DB rows ─────────────────────────────

    private sealed class IndexRow
    {
        public string PathKey { get; set; } = "";
        public string Path { get; set; } = "";
        public string ParentKey { get; set; } = "";
        public string Kind { get; set; } = "";
        public long? Size { get; set; }
        public string? Mtime { get; set; }
        public string? Sha256 { get; set; }
        public string? Mime { get; set; }
        public string? CreatedBy { get; set; }
    }

    private sealed class ChangeRow
    {
        public long Seq { get; set; }
        public string Op { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Path { get; set; } = "";
        public string? OldPath { get; set; }
        public long? Size { get; set; }
        public string? Mtime { get; set; }
        public string? Etag { get; set; }
        public string? Sha256 { get; set; }
        public long Trashed { get; set; }
        public string Source { get; set; } = "";
        public string? Actor { get; set; }
        public string At { get; set; } = "";
    }

    private sealed class TrashRow
    {
        public string Id { get; set; } = "";
        public string OriginalPath { get; set; } = "";
        public string Kind { get; set; } = "";
        public long Size { get; set; }
        public string DeletedAt { get; set; } = "";
        public string? DeletedBy { get; set; }
    }

    private sealed class StateRow
    {
        public string Epoch { get; set; } = "";
        public long FloorSeq { get; set; }
        public long CaseSensitive { get; set; }
        public string? LastScanAt { get; set; }
    }

    // The one state row; created on first use. A cold import onto a host with
    // another case rule rebuilds the index (new path keys) and rotates the
    // epoch, so sync clients re-snapshot.
    private static StateRow EnsureState(IDbConnection db, IDbTransaction? tx, FilePathResolver r)
    {
        var cs = r.CaseSensitive ? 1 : 0;
        var row = db.QuerySingleOrDefault<StateRow>(
            "SELECT epoch, floor_seq, case_sensitive, last_scan_at FROM file_state WHERE id = 1", transaction: tx);
        if (row is null)
        {
            row = new StateRow { Epoch = Ulid.NewUlid().ToString(), CaseSensitive = cs };
            db.Execute("INSERT INTO file_state (id, epoch, floor_seq, case_sensitive) VALUES (1, @Epoch, 0, @CaseSensitive)", row, tx);
            return row;
        }
        if (row.CaseSensitive != cs)
        {
            db.Execute("DELETE FROM file_index", transaction: tx);
            row.Epoch = Ulid.NewUlid().ToString();
            row.CaseSensitive = cs;
            row.LastScanAt = null;
            db.Execute("UPDATE file_state SET epoch = @Epoch, case_sensitive = @CaseSensitive, last_scan_at = NULL WHERE id = 1", row, tx);
        }
        return row;
    }

    private static void Upsert(IDbConnection db, IDbTransaction tx, FilePathResolver r, DiskItem item, string? sha, string? mime, string? createdBy)
    {
        var parentRel = item.Rel.LastIndexOf('/') is var i and >= 0 ? item.Rel[..i] : "";
        db.Execute(@"
            INSERT OR REPLACE INTO file_index (path_key, path, parent_key, kind, size, mtime, sha256, mime, created_by)
            VALUES (@PathKey, @Path, @ParentKey, @Kind, @Size, @Mtime, @Sha256, @Mime, @CreatedBy)",
            new IndexRow
            {
                PathKey = r.Key(item.Rel),
                Path = item.Rel,
                ParentKey = r.Key(parentRel),
                Kind = item.Kind,
                Size = item.Size,
                Mtime = Iso(item.Mtime),
                Sha256 = sha,
                Mime = mime,
                CreatedBy = createdBy,
            }, tx);
    }

    // The entry and everything below it. Range on the key: under SQLite's
    // byte-wise comparison every "k/…" sorts in [ "k/", "k0" ) ('0' follows '/').
    private static void DeleteSubtreeRows(IDbConnection db, IDbTransaction tx, string key)
        => db.Execute("DELETE FROM file_index WHERE path_key = @key OR (path_key >= @lo AND path_key < @hi)",
            new { key, lo = key + "/", hi = key + "0" }, tx);

    private static List<IndexRow> SubtreeRows(IDbConnection db, IDbTransaction tx, string key)
        => db.Query<IndexRow>("SELECT * FROM file_index WHERE path_key = @key OR (path_key >= @lo AND path_key < @hi) ORDER BY path",
            new { key, lo = key + "/", hi = key + "0" }, tx).ToList();

    private static void Journal(IDbConnection db, IDbTransaction tx, string op, string kind, string path, string? oldPath,
        long? size, DateTime? mtime, string? sha, bool trashed, string source, string? actor)
    {
        db.Execute(@"
            INSERT INTO file_changes (op, kind, path, old_path, size, mtime, etag, sha256, trashed, source, actor, at)
            VALUES (@op, @kind, @path, @oldPath, @size, @mtime, @etag, @sha, @trashed, @source, @actor, @at)",
            new
            {
                op,
                kind,
                path,
                oldPath,
                size = kind == FileKinds.File ? size : null,
                mtime = mtime is null ? null : Iso(mtime),
                etag = kind == FileKinds.File ? DiskFileStore.Etag(size, mtime) : null,
                sha,
                trashed = trashed ? 1 : 0,
                source,
                actor,
                at = Iso(DateTime.UtcNow),
            }, tx);
    }

    private static FileEntry ToEntry(DiskItem d, IndexRow? row)
    {
        // Cached hash / type are valid only while size + mtime match.
        var fresh = row is not null && row.Kind == d.Kind && row.Size == d.Size && row.Mtime == Iso(d.Mtime);
        return new FileEntry(
            d.Name, d.Rel, d.Kind, d.Kind == FileKinds.File ? d.Size : null, d.Mtime,
            d.Kind == FileKinds.File ? DiskFileStore.Etag(d.Size, d.Mtime) : null,
            fresh ? row!.Sha256 : null,
            fresh ? row!.Mime : null,
            row?.CreatedBy);
    }

    // ───────────────────────────── reconcile ─────────────────────────────

    // Disk vs index for a set of disk items and the index rows covering the
    // same scope. Disk wins; every difference is journalled as source = disk.
    private static void ApplyDiff(IDbConnection db, IDbTransaction tx, FilePathResolver r, List<DiskItem> disk, List<IndexRow> rows)
    {
        var diskByKey = new Dictionary<string, DiskItem>(StringComparer.Ordinal);
        foreach (var d in disk) diskByKey.TryAdd(r.Key(d.Rel), d);
        var rowByKey = rows.ToDictionary(x => x.PathKey, StringComparer.Ordinal);

        var gone = new List<string>();
        foreach (var row in rows.OrderBy(x => x.PathKey, StringComparer.Ordinal))
        {
            if (diskByKey.TryGetValue(row.PathKey, out var d) && d.Kind == row.Kind) continue;
            if (gone.Any(g => row.PathKey.StartsWith(g + "/", StringComparison.Ordinal))) continue;   // under a vanished folder
            DeleteSubtreeRows(db, tx, row.PathKey);
            if (row.Kind is FileKinds.File or FileKinds.Folder)
                Journal(db, tx, "delete", row.Kind, row.Path, null, row.Size, ParseIso(row.Mtime), null, false, "disk", null);
            gone.Add(row.PathKey);
        }

        foreach (var d in disk.OrderBy(x => x.Rel, StringComparer.Ordinal))
        {
            var key = r.Key(d.Rel);
            if (rowByKey.TryGetValue(key, out var row) && row.Kind == d.Kind && !gone.Contains(key))
            {
                if (d.Kind == FileKinds.File && (row.Size != d.Size || row.Mtime != Iso(d.Mtime)))
                {
                    var mime = FileSniffer.Sniff(DiskFileStore.Head(r.FullOf(d.Rel)), d.Name);
                    Upsert(db, tx, r, d, null, mime, row.CreatedBy);
                    Journal(db, tx, "modify", d.Kind, d.Rel, null, d.Size, d.Mtime, null, false, "disk", null);
                }
                continue;
            }
            var sniffed = d.Kind == FileKinds.File ? FileSniffer.Sniff(DiskFileStore.Head(r.FullOf(d.Rel)), d.Name) : null;
            Upsert(db, tx, r, d, null, sniffed, null);
            Journal(db, tx, "create", d.Kind, d.Rel, null, d.Size, d.Mtime, null, false, "disk", null);
        }
    }

    // One folder: what a listing shows is always current.
    private async Task ReconcileFolderAsync(ContextRef ctx, FilePathResolver r, string folderRel, CancellationToken ct)
    {
        var disk = DiskFileStore.Children(r, folderRel).Where(d => d.Kind != FileKinds.Link).ToList();
        await _dbFactory.WithContextTransactionAsync(ctx, (db, tx, _) =>
        {
            EnsureState(db, tx, r);
            var rows = db.Query<IndexRow>("SELECT * FROM file_index WHERE parent_key = @p", new { p = r.Key(folderRel) }, tx).ToList();
            ApplyDiff(db, tx, r, disk, rows);
            return Task.CompletedTask;
        }, ct);
    }

    // The whole tree, throttled by Files:ReconcileIntervalSeconds unless forced.
    private async Task FullReconcileAsync(ContextRef ctx, FilePathResolver r, bool force, FileSettings s, CancellationToken ct)
    {
        if (!force)
        {
            using var db = _dbFactory.CreateContextConnection(ctx);
            var state = EnsureState(db, null, r);
            if (ParseIso(state.LastScanAt) is { } last && DateTime.UtcNow - last < TimeSpan.FromSeconds(s.ReconcileIntervalSeconds))
                return;
        }
        var disk = Directory.Exists(r.Root)
            ? DiskFileStore.Walk(r, "").Where(d => d.Kind != FileKinds.Link).ToList()
            : new List<DiskItem>();
        await _dbFactory.WithContextTransactionAsync(ctx, (db, tx, _) =>
        {
            EnsureState(db, tx, r);
            var rows = db.Query<IndexRow>("SELECT * FROM file_index", transaction: tx).ToList();
            ApplyDiff(db, tx, r, disk, rows);
            db.Execute("UPDATE file_state SET last_scan_at = @at WHERE id = 1", new { at = Iso(DateTime.UtcNow) }, tx);
            return Task.CompletedTask;
        }, ct);
        UsageCache.TryRemove(LockKey(r), out _);
    }

    // ───────────────────────────── limits ─────────────────────────────

    private static long Usage(FilePathResolver r)
        => UsageCache.GetOrAdd(LockKey(r), _ => DiskFileStore.Measure(r.Root).Bytes);

    private static void AdjustUsage(FilePathResolver r, long delta)
    {
        if (UsageCache.TryGetValue(LockKey(r), out var v)) UsageCache[LockKey(r)] = Math.Max(0, v + delta);
        // Shrinking: re-measure the owner next time (cheaper than knowing whose it was).
        if (delta < 0) OwnerUsageCache.Clear();
    }

    // ───────────────────────────── the owner's quota ─────────────────────────────
    //
    // Admin spec § Quotas: one quota per user (users.quota_bytes, null = the
    // Files:DefaultUserQuotaBytes default, 0 = unlimited) covering their
    // personal workspace (DB + files, the whole folder) and every space they
    // own — a space bills its owner, so sharing doesn't multiply anyone's
    // allowance. Checked next to the per-workspace Files:QuotaBytes.

    private static readonly ConcurrentDictionary<string, (DateTime At, long Bytes)> OwnerUsageCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan OwnerUsageTtl = TimeSpan.FromSeconds(30);
    private const string QuotaWarnedKeyPrefix = "Files:QuotaWarned:";

    private sealed record OwnerQuota(string OwnerId, long QuotaBytes, bool ViaSpace);

    private async Task<OwnerQuota?> OwnerQuotaAsync(ContextRef ctx, CancellationToken ct)
    {
        string? ownerId = ctx.Id;
        if (ctx.Type == ContextType.Space)
        {
            using var sys = _dbFactory.CreateSystemConnection();
            ownerId = await sys.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(@"
                SELECT m.user_id FROM space_members m JOIN spaces s ON s.id = m.space_id
                WHERE (s.id = @x OR s.slug = @x) AND m.role = @owner LIMIT 1",
                new { x = ctx.Id, owner = SpaceRole.Owner.ToDbValue() }, cancellationToken: ct));
        }
        if (string.IsNullOrEmpty(ownerId)) return null;
        var user = await _system.GetUserAsync(ownerId, ct);
        if (user is null) return null;
        var quota = user.QuotaBytes;
        if (quota is null)
        {
            var v = await _system.GetConfigAsync(FileLimits.DefaultUserQuotaBytesKey, ct);
            quota = long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && x >= 0
                ? x : FileLimits.DefaultUserQuotaBytes;
        }
        return new OwnerQuota(ownerId, quota.Value, ctx.Type == ContextType.Space);
    }

    // Everything the owner stores: users/<id>/ plus spaces/<id>/ of each owned space.
    private long OwnerUsage(string ownerId)
    {
        if (OwnerUsageCache.TryGetValue(ownerId, out var hit) && DateTime.UtcNow - hit.At < OwnerUsageTtl) return hit.Bytes;
        long total = DiskFileStore.Measure(_dbFactory.ResolveContextFolder(ContextRef.User(ownerId))).Bytes;
        using (var sys = _dbFactory.CreateSystemConnection())
            foreach (var spaceId in sys.Query<string>(
                         "SELECT space_id FROM space_members WHERE user_id = @ownerId AND role = @owner",
                         new { ownerId, owner = SpaceRole.Owner.ToDbValue() }))
                total += DiskFileStore.Measure(_dbFactory.ResolveContextFolder(ContextRef.Space(spaceId))).Bytes;
        OwnerUsageCache[ownerId] = (DateTime.UtcNow, total);
        return total;
    }

    private long OwnerRoom(OwnerQuota? q)
        => q is null || q.QuotaBytes <= 0 ? long.MaxValue : q.QuotaBytes - OwnerUsage(q.OwnerId);

    private static FileStoreException OwnerQuotaExceeded(OwnerQuota q)
        => FileStoreException.TooLarge("user_quota", q.ViaSpace
            ? "That would go over the space owner's storage quota."
            : "That would go over your storage quota.");

    private void CheckOwnerRoom(OwnerQuota? q, long delta)
    {
        if (q is null || q.QuotaBytes <= 0 || delta <= 0) return;
        if (OwnerUsage(q.OwnerId) + delta > q.QuotaBytes) throw OwnerQuotaExceeded(q);
    }

    // After a write that grew the owner's data: book it, and tell the owner
    // once when they cross 90% (latched in system_config until they drop
    // back below, like the digest's per-user latch).
    private async Task OwnerGrewAsync(OwnerQuota? q, long delta, CancellationToken ct)
    {
        if (q is null || delta <= 0) return;
        if (OwnerUsageCache.TryGetValue(q.OwnerId, out var v)) OwnerUsageCache[q.OwnerId] = (v.At, v.Bytes + delta);
        await CheckQuotaWarningAsync(q, ct);
    }

    private async Task CheckQuotaWarningAsync(OwnerQuota q, CancellationToken ct)
    {
        if (q.QuotaBytes <= 0) return;
        var used = OwnerUsage(q.OwnerId);
        var key = QuotaWarnedKeyPrefix + q.OwnerId;
        var latched = !string.IsNullOrEmpty(await _system.GetConfigAsync(key, ct));
        var over = used >= q.QuotaBytes * DataLifecycleLimits.QuotaWarningRatio;
        if (over && !latched)
        {
            await _system.SetConfigAsync(key, "1", ct);
            if (_messages is not null)
                await _messages.CreateAsync(new[] { q.OwnerId }, MessageKinds.QuotaWarning, "user", q.OwnerId,
                    System.Text.Json.JsonSerializer.Serialize(new { usedBytes = used, quotaBytes = q.QuotaBytes }), ct);
            _logger.LogInformation("Quota warning for user {UserId}", q.OwnerId);
        }
        else if (!over && latched)
        {
            await _system.SetConfigAsync(key, "", ct);
        }
    }

    private long InstanceUsage()
    {
        if (DateTime.UtcNow - _instanceUsage.At < TimeSpan.FromSeconds(60)) return _instanceUsage.Bytes;
        long total = 0;
        foreach (var root in new[] { _dbFactory.UsersRoot, _dbFactory.SpacesRoot })
            if (Directory.Exists(root))
                foreach (var dir in Directory.EnumerateDirectories(root))
                    total += DiskFileStore.Measure(Path.Combine(dir, FilesFolder)).Bytes;
        var archive = Path.Combine(Path.GetDirectoryName(_dbFactory.UsersRoot)!, "archive");
        total += DiskFileStore.Measure(archive).Bytes;
        _instanceUsage = (DateTime.UtcNow, total);
        return total;
    }

    // `delta` = how much the context grows; `writeBytes` = what hits the disk.
    private void CheckRoom(FilePathResolver r, FileSettings s, long delta, long writeBytes)
    {
        if (s.QuotaBytes > 0 && delta > 0 && Usage(r) + delta > s.QuotaBytes)
            throw FileStoreException.TooLarge("quota", "That would go over this workspace's storage quota.");
        if (s.InstanceCapBytes > 0 && delta > 0 && InstanceUsage() + delta > s.InstanceCapBytes)
            throw FileStoreException.TooLarge("instance", "That would go over this server's storage limit.");
        if (s.MinFreeBytes > 0 || writeBytes > 0)
        {
            var root = Path.GetPathRoot(r.Root);
            if (!string.IsNullOrEmpty(root))
            {
                long free;
                try { free = new DriveInfo(root).AvailableFreeSpace; }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return; }
                // (Subtracting, not adding: a huge configured floor must not overflow.)
                if (free < s.MinFreeBytes || free - s.MinFreeBytes < writeBytes) throw FileStoreException.InsufficientStorage();
            }
        }
    }

    // ───────────────────────────── reads ─────────────────────────────

    public Task<FileCapabilities> GetCapabilitiesAsync(ContextRef ctx, CancellationToken ct = default)
        => CapabilitiesCoreAsync(ctx, ct);

    private async Task<FileCapabilities> CapabilitiesCoreAsync(ContextRef ctx, CancellationToken ct)
    {
        var r = Open(ctx);
        var s = await SettingsAsync(ct);
        return new FileCapabilities(r.CaseSensitive, FileNameRules.Describe(r.Rules), FileNameRules.MaxSegment(r.Rules),
            FileNameRules.MaxPath(r.Rules, r.LongPaths), s.MaxFileBytes, s.QuotaBytes);
    }

    public async Task<FileListPage> ListAsync(ContextRef ctx, string? path, string? cursor, int? limit, CancellationToken ct = default)
    {
        var r = Open(ctx);
        var folder = r.Resolve(path);
        if (!folder.IsRoot && !Directory.Exists(folder.Full))
        {
            if (File.Exists(folder.Full)) throw new FileStoreException(400, "not_a_folder", "That's a file, not a folder.");
            throw FileStoreException.NotFound();
        }
        if (!Directory.Exists(r.Root)) return new FileListPage(folder.Rel, Array.Empty<FileEntry>(), null);

        List<IndexRow> rows;
        using (await LockAsync(r, ct))
        {
            await ReconcileFolderAsync(ctx, r, folder.Rel, ct);
            using var db = _dbFactory.CreateContextConnection(ctx);
            rows = db.Query<IndexRow>("SELECT * FROM file_index WHERE parent_key = @p", new { p = r.Key(folder.Rel) }).ToList();
        }
        var byKey = rows.ToDictionary(x => x.PathKey, StringComparer.Ordinal);
        var entries = DiskFileStore.Children(r, folder.Rel)
            .Select(d => ToEntry(d, byKey.GetValueOrDefault(r.Key(d.Rel))))
            .OrderBy(e => e.Kind == FileKinds.Folder ? 0 : e.Kind == FileKinds.Link ? 1 : 2)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        var size = Math.Clamp(limit ?? FileLimits.ListPageSize, 1, FileLimits.ListPageSize);
        var offset = int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        var page = entries.Skip(offset).Take(size).ToList();
        var next = offset + page.Count < entries.Count ? (offset + page.Count).ToString(CultureInfo.InvariantCulture) : null;
        return new FileListPage(folder.Rel, page, next);
    }

    public async Task<FileEntry> StatAsync(ContextRef ctx, string path, CancellationToken ct = default)
    {
        var r = Open(ctx);
        var p = r.Resolve(path);
        if (p.IsRoot) return new FileEntry("", "", FileKinds.Folder, null, Directory.Exists(r.Root) ? Directory.GetLastWriteTimeUtc(r.Root) : null, null, null, null, null);
        var d = DiskFileStore.Stat(r, p.Rel) ?? throw FileStoreException.NotFound();
        using var db = _dbFactory.CreateContextConnection(ctx);
        var row = await db.QuerySingleOrDefaultAsync<IndexRow>(new CommandDefinition(
            "SELECT * FROM file_index WHERE path_key = @k", new { k = r.Key(p.Rel) }, cancellationToken: ct));
        var entry = ToEntry(d, row);
        if (entry.Kind == FileKinds.File && entry.Mime is null)
            entry = entry with { Mime = FileSniffer.Sniff(DiskFileStore.Head(p.Full), d.Name) };
        return entry;
    }

    public async Task<FileReadHandle> OpenReadAsync(ContextRef ctx, string path, CancellationToken ct = default)
    {
        var r = Open(ctx);
        var p = r.Resolve(path, allowRoot: false);
        var d = DiskFileStore.Stat(r, p.Rel) ?? throw FileStoreException.NotFound();
        if (d.Kind == FileKinds.Folder) throw new FileStoreException(400, "is_folder", "That's a folder — download its files one by one.");
        var mime = FileSniffer.Sniff(DiskFileStore.Head(p.Full), d.Name);
        FileStream stream;
        try
        {
            // Delete sharing: a replace or trash can rename over a file that
            // is being streamed (Windows).
            stream = new FileStream(p.Full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        }
        catch (FileNotFoundException) { throw FileStoreException.NotFound(); }
        catch (IOException) { throw FileStoreException.Locked(); }
        var entry = await StatAsync(ctx, p.Rel, ct);
        return new FileReadHandle(stream, entry with { Mime = mime }, mime);
    }

    public async Task<FileUsage> GetUsageAsync(ContextRef ctx, CancellationToken ct = default)
    {
        var r = Open(ctx);
        var s = await SettingsAsync(ct);
        var all = DiskFileStore.Measure(r.Root);
        var trashFull = Path.Combine(r.Root, FileNameRules.TrashFolder);
        var trash = DiskFileStore.Measure(trashFull);
        UsageCache[LockKey(r)] = all.Bytes;
        var trashFolders = Directory.Exists(trashFull) ? trash.Folders + 1 : 0;
        var q = await OwnerQuotaAsync(ctx, ct);
        long ownerBytes = 0;
        if (q is not null)
        {
            OwnerUsageCache.TryRemove(q.OwnerId, out _);
            ownerBytes = OwnerUsage(q.OwnerId);
            await CheckQuotaWarningAsync(q, ct);   // also clears the latch once back below
        }
        return new FileUsage(all.Bytes, s.QuotaBytes, all.Files - trash.Files, all.Folders - trashFolders, trash.Bytes,
            ownerBytes, q?.QuotaBytes ?? 0);
    }

    // ───────────────────────────── writes ─────────────────────────────

    private static string NormalizeEtag(string etag)
    {
        var e = etag.Trim();
        if (e.StartsWith("W/", StringComparison.Ordinal)) e = e[2..];
        return e.Trim('"');
    }

    private static void CheckPrecondition(DiskItem? existing, string? ifMatch, bool ifNoneMatchStar)
    {
        if (ifNoneMatchStar && existing is not null) throw FileStoreException.PreconditionFailed();
        if (ifMatch is null) return;
        if (existing is null) throw FileStoreException.PreconditionFailed();
        if (NormalizeEtag(ifMatch) == "*") return;
        if (NormalizeEtag(ifMatch) != DiskFileStore.Etag(existing.Size, existing.Mtime)) throw FileStoreException.PreconditionFailed();
    }

    // Creates the missing folders of `folderRel` (mkdir -p), journalled.
    private static void EnsureFolders(IDbConnection db, IDbTransaction tx, FilePathResolver r, string folderRel, string actor)
    {
        Directory.CreateDirectory(r.Root);
        if (folderRel.Length == 0) return;
        var cur = "";
        foreach (var seg in folderRel.Split('/'))
        {
            cur = FilePathResolver.Join(cur, seg);
            var full = r.FullOf(cur);
            if (Directory.Exists(full)) continue;
            Directory.CreateDirectory(full);
            var item = DiskFileStore.Stat(r, cur)!;
            Upsert(db, tx, r, item, null, null, actor);
            Journal(db, tx, "create", FileKinds.Folder, cur, null, null, item.Mtime, null, false, "api", actor);
        }
    }

    public async Task<FileUploadResult> UploadAsync(ContextRef ctx, string path, Stream body, long? contentLength,
        string? ifMatch, bool ifNoneMatchStar, bool parents, string actor, CancellationToken ct = default)
    {
        if (!ifNoneMatchStar && ifMatch is null) throw FileStoreException.PreconditionRequired();
        var r = Open(ctx);
        var addr = r.Resolve(path, allowRoot: false);
        var existing = DiskFileStore.Stat(r, addr.Rel);
        if (existing?.Kind == FileKinds.Folder) throw new FileStoreException(409, "is_folder", "A folder with that name already exists here.", "name_exists");
        var target = existing is null ? r.Resolve(path, create: true, createAll: parents, allowRoot: false) : addr;
        CheckPrecondition(existing, ifMatch, ifNoneMatchStar);

        var s = await SettingsAsync(ct);
        if (contentLength > s.MaxFileBytes)
            throw FileStoreException.TooLarge("size", $"Files can be at most {s.MaxFileBytes:N0} bytes on this server.");
        var parentFull = Path.GetDirectoryName(target.Full)!;
        if (target.ParentRel.Length == 0) Directory.CreateDirectory(r.Root);   // files/ appears on first write
        if (!Directory.Exists(parentFull) && !parents)
            throw new FileStoreException(404, "not_found", "The folder doesn't exist.");
        CheckRoom(r, s, (contentLength ?? 0) - (existing?.Size ?? 0), contentLength ?? 0);
        var owner = await OwnerQuotaAsync(ctx, ct);
        CheckOwnerRoom(owner, (contentLength ?? 0) - (existing?.Size ?? 0));

        if (!Directory.Exists(parentFull))
        {
            using (await LockAsync(r, ct))
                await _dbFactory.WithContextTransactionAsync(ctx, (db, tx, _) =>
                {
                    EnsureState(db, tx, r);
                    EnsureFolders(db, tx, r, target.ParentRel, actor);
                    return Task.CompletedTask;
                }, ct);
        }

        var temp = Path.Combine(parentFull, DiskFileStore.TempName());
        var head = new byte[FileLimits.SniffBytes];
        var headLen = 0;
        long total = 0;
        var roomLeft = s.QuotaBytes > 0 ? s.QuotaBytes - Usage(r) + (existing?.Size ?? 0) : long.MaxValue;
        var ownerRoom = OwnerRoom(owner);
        var ownerRoomLeft = ownerRoom == long.MaxValue ? long.MaxValue : ownerRoom + (existing?.Size ?? 0);
        try
        {
            string sha;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    DiskFileStore.Hide(temp);
                    var buf = new byte[81920];
                    int n;
                    while ((n = await body.ReadAsync(buf, ct)) > 0)
                    {
                        total += n;
                        if (total > s.MaxFileBytes)
                            throw FileStoreException.TooLarge("size", $"Files can be at most {s.MaxFileBytes:N0} bytes on this server.");
                        if (total > roomLeft)
                            throw FileStoreException.TooLarge("quota", "That would go over this workspace's storage quota.");
                        if (total > ownerRoomLeft) throw OwnerQuotaExceeded(owner!);
                        if (headLen < head.Length)
                        {
                            var take = Math.Min(n, head.Length - headLen);
                            Array.Copy(buf, 0, head, headLen, take);
                            headLen += take;
                        }
                        hash.AppendData(buf, 0, n);
                        await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    }
                }
                sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            using (await LockAsync(r, ct))
            {
                // Re-check under the lock: someone may have written meanwhile.
                var now = DiskFileStore.Stat(r, target.Rel);
                if (now?.Kind == FileKinds.Folder) throw new FileStoreException(409, "is_folder", "A folder with that name already exists here.", "name_exists");
                CheckPrecondition(now, ifMatch, ifNoneMatchStar);
                DiskFileStore.Unhide(temp);
                DiskFileStore.Move(temp, target.Full, overwriteFile: true);
                var item = DiskFileStore.Stat(r, target.Rel)!;
                var mime = FileSniffer.Sniff(head.AsSpan(0, headLen), item.Name);
                await _dbFactory.WithContextTransactionAsync(ctx, (db, tx, _) =>
                {
                    EnsureState(db, tx, r);
                    Upsert(db, tx, r, item, sha, mime, actor);
                    Journal(db, tx, now is null ? "create" : "modify", FileKinds.File, item.Rel, null, item.Size, item.Mtime, sha, false, "api", actor);
                    return Task.CompletedTask;
                }, ct);
                AdjustUsage(r, total - (now?.Size ?? 0));
                await OwnerGrewAsync(owner, total - (now?.Size ?? 0), ct);
                _logger.LogInformation("Uploaded file {PathHash} ({Bytes} bytes, {Mime}) in {CtxType}:{CtxId}",
                    Hash(item.Rel), total, mime, ctx.Type, ctx.Id);
                return new FileUploadResult(ToEntry(item, new IndexRow
                {
                    PathKey = r.Key(item.Rel),
                    Kind = item.Kind,
                    Size = item.Size,
                    Mtime = Iso(item.Mtime),
                    Sha256 = sha,
                    Mime = mime,
                    CreatedBy = actor,
                }), now is null);
            }
        }
        finally
        {
            // An aborted upload leaves nothing behind: no temp file, no
            // visible file, no journal entry.
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { /* the sweeper takes it */ }
        }
    }

    public async Task<FileEntry> CreateFolderAsync(ContextRef ctx, string path, string actor, CancellationToken ct = default)
    {
        var r = Open(ctx);
        var t = r.Resolve(path, create: true, allowRoot: false);
        using (await LockAsync(r, ct))
        {
            if (DiskFileStore.Exists(t.Full)) throw FileStoreException.Exists();
            Directory.CreateDirectory(r.Root);
            if (!Directory.Exists(Path.GetDirectoryName(t.Full))) throw new FileStoreException(404, "not_found", "The folder doesn't exist.");
            Directory.CreateDirectory(t.Full);
            var item = DiskFileStore.Stat(r, t.Rel)!;
            await _dbFactory.WithContextTransactionAsync(ctx, (db, tx, _) =>
            {
                EnsureState(db, tx, r);
                Upsert(db, tx, r, item, null, null, actor);
                Journal(db, tx, "create", FileKinds.Folder, item.Rel, null, null, item.Mtime, null, false, "api", actor);
                return Task.CompletedTask;
            }, ct);
            return ToEntry(item, null) with { CreatedBy = actor };
        }
    }

    public async Task<FileEntry> MoveAsync(ContextRef ctx, string from, string to, string actor, CancellationToken ct = default)
    {
        var r = Open(ctx);
        var src = r.Resolve(from, allowRoot: false);
        var dst = r.Resolve(to, create: true, allowRoot: false);
        using (await LockAsync(r, ct))
        {
            var item = DiskFileStore.Stat(r, src.Rel) ?? throw FileStoreException.NotFound();
            // Make sure the index knows the entry, so the journal describes a
            // move of something a sync client has seen.
            await ReconcileFolderAsync(ctx, r, src.ParentRel, ct);

            if (string.Equals(src.Rel, dst.Rel, StringComparison.Ordinal) && r.CaseSensitive) return ToEntry(item, null);
            var caseOnly = !r.CaseSensitive && string.Equals(src.Rel, dst.Rel, StringComparison.OrdinalIgnoreCase);
            if (caseOnly && string.Equals(item.Rel, dst.Rel, StringComparison.Ordinal) && DiskName(r, src) == dst.Name)
                return ToEntry(item, null);
            if (!caseOnly && DiskFileStore.Exists(dst.Full)) throw FileStoreException.Exists();
            if (dst.Rel.StartsWith(src.Rel + "/", r.Comparison))
                throw new FileStoreException(400, "into_own_subtree", "A folder can't move into itself.");
            var dstParent = Path.GetDirectoryName(dst.Full)!;
            if (!Directory.Exists(dstParent)) throw new FileStoreException(404, "not_found", "The target folder doesn't exist.");

            if (caseOnly)
            {
                // Through a temp name, so a case-only rename works whatever
                // File.Move does on this volume.
                var tmp = Path.Combine(dstParent, DiskFileStore.TempName());
                DiskFileStore.Move(src.Full, tmp);
                DiskFileStore.Move(tmp, dst.Full);
            }
            else DiskFileStore.Move(src.Full, dst.Full);

            var moved = DiskFileStore.Stat(r, dst.Rel)!;
            var oldRel = item.Rel;
            await _dbFactory.WithContextTransactionAsync(ctx, (db, tx, _) =>
            {
                EnsureState(db, tx, r);
                var rows = SubtreeRows(db, tx, r.Key(oldRel));
                DeleteSubtreeRows(db, tx, r.Key(oldRel));
                foreach (var row in rows)
                {
                    var newRel = dst.Rel + row.Path[Math.Min(row.Path.Length, oldRel.Length)..];
                    var parentRel = newRel.LastIndexOf('/') is var i and >= 0 ? newRel[..i] : "";
                    row.PathKey = r.Key(newRel);
                    row.Path = newRel;
                    row.ParentKey = r.Key(parentRel);
                    db.Execute(@"INSERT OR REPLACE INTO file_index (path_key, path, parent_key, kind, size, mtime, sha256, mime, created_by)
                                 VALUES (@PathKey, @Path, @ParentKey, @Kind, @Size, @Mtime, @Sha256, @Mime, @CreatedBy)", row, tx);
                }
                if (rows.Count == 0) Upsert(db, tx, r, moved, null, null, null);
                Journal(db, tx, "move", moved.Kind, moved.Rel, oldRel, moved.Size, moved.Mtime, null, false, "api", actor);
                return Task.CompletedTask;
            }, ct);
            return ToEntry(moved, null);
        }
    }

    // The on-disk spelling of an existing entry's last segment.
    private static string DiskName(FilePathResolver r, ResolvedPath p)
    {
        var parent = Path.GetDirectoryName(p.Full)!;
        foreach (var info in new DirectoryInfo(parent).EnumerateFileSystemInfos("*", DiskFileStore.Shallow))
            if (string.Equals(info.Name, p.Name, r.Comparison)) return info.Name;
        return p.Name;
    }

    // Copies `src` (file or folder, same or another context) to `dst`;
    // returns every created item, parents first. Assumes the locks are held
    // and room was checked.
    private static async Task<List<DiskItem>> CopyTreeAsync(FilePathResolver rs, DiskItem src, FilePathResolver rd, ResolvedPath dst, CancellationToken ct)
    {
        var created = new List<DiskItem>();
        if (src.Kind == FileKinds.File)
        {
            await DiskFileStore.CopyFileAsync(rs.FullOf(src.Rel), dst.Full, ct);
            created.Add(DiskFileStore.Stat(rd, dst.Rel)!);
            return created;
        }
        Directory.CreateDirectory(dst.Full);
        created.Add(DiskFileStore.Stat(rd, dst.Rel)!);
        foreach (var d in DiskFileStore.Walk(rs, src.Rel).Where(x => x.Kind != FileKinds.Link))
        {
            var rel = dst.Rel + d.Rel[src.Rel.Length..];
            var full = rd.FullOf(rel);
            if (d.Kind == FileKinds.Folder) Directory.CreateDirectory(full);
            else await DiskFileStore.CopyFileAsync(rs.FullOf(d.Rel), full, ct);
            created.Add(DiskFileStore.Stat(rd, rel)!);
        }
        return created;
    }

    private static (long Bytes, int Nodes) CountTree(FilePathResolver r, DiskItem item)
    {
        if (item.Kind == FileKinds.File) return (item.Size ?? 0, 1);
        var all = DiskFileStore.Walk(r, item.Rel).Where(x => x.Kind != FileKinds.Link).ToList();
        return (all.Sum(x => x.Size ?? 0), all.Count + 1);
    }

    private static void CheckNodes(int nodes)
    {
        if (nodes > FileLimits.MaxCopyNodes)
            throw new FileStoreException(400, "too_many_items", $"That's more than {FileLimits.MaxCopyNodes:N0} items to copy at once.");
    }

    private static void IndexCreated(IDbConnection db, IDbTransaction tx, FilePathResolver r, List<DiskItem> created, string actor)
    {
        foreach (var c in created)
        {
            var mime = c.Kind == FileKinds.File ? FileSniffer.Sniff(DiskFileStore.Head(r.FullOf(c.Rel)), c.Name) : null;
            Upsert(db, tx, r, c, null, mime, actor);
            Journal(db, tx, "create", c.Kind, c.Rel, null, c.Size, c.Mtime, null, false, "api", actor);
        }
    }

    public async Task<FileEntry> CopyAsync(ContextRef ctx, string from, string to, string actor, CancellationToken ct = default)
    {
        var r = Open(ctx);
        var src = r.Resolve(from, allowRoot: false);
        var dst = r.Resolve(to, create: true, allowRoot: false);
        var s = await SettingsAsync(ct);
        using (await LockAsync(r, ct))
        {
            var item = DiskFileStore.Stat(r, src.Rel) ?? throw FileStoreException.NotFound();
            if (item.Kind == FileKinds.Link) throw FileStoreException.LinkNotFollowed();
            if (DiskFileStore.Exists(dst.Full)) throw FileStoreException.Exists();
            if (string.Equals(dst.Rel, src.Rel, r.Comparison) || dst.Rel.StartsWith(src.Rel + "/", r.Comparison))
                throw new FileStoreException(400, "into_own_subtree", "A folder can't be copied into itself.");
            if (!Directory.Exists(Path.GetDirectoryName(dst.Full))) throw new FileStoreException(404, "not_found", "The target folder doesn't exist.");
            var (bytes, nodes) = CountTree(r, item);
            CheckNodes(nodes);
            CheckRoom(r, s, bytes, bytes);
            var owner = await OwnerQuotaAsync(ctx, ct);
            CheckOwnerRoom(owner, bytes);

            var created = await CopyTreeAsync(r, item, r, dst, ct);
            await _dbFactory.WithContextTransactionAsync(ctx, (db, tx, _) =>
            {
                EnsureState(db, tx, r);
                IndexCreated(db, tx, r, created, actor);
                return Task.CompletedTask;
            }, ct);
            AdjustUsage(r, bytes);
            await OwnerGrewAsync(owner, bytes, ct);
            return ToEntry(created[0], null) with { CreatedBy = actor };
        }
    }

    // ───────────────────────────── trash ─────────────────────────────

    private string TrashRoot(FilePathResolver r) => Path.Combine(r.Root, FileNameRules.TrashFolder);

    private async Task<FileTrashEntry> TrashCoreAsync(ContextRef ctx, FilePathResolver r, ResolvedPath src, string actor, CancellationToken ct)
    {
        var item = DiskFileStore.Stat(r, src.Rel) ?? throw FileStoreException.NotFound();
        await ReconcileFolderAsync(ctx, r, src.ParentRel, ct);
        var id = Ulid.NewUlid().ToString();
        var dir = Path.Combine(TrashRoot(r), id);
        Directory.CreateDirectory(dir);
        var size = DiskFileStore.Measure(src.Full).Bytes;
        try { DiskFileStore.Move(src.Full, Path.Combine(dir, item.Name)); }
        catch { try { Directory.Delete(dir); } catch (IOException) { } throw; }

        var entry = new TrashRow
        {
            Id = id,
            OriginalPath = item.Rel,
            Kind = item.Kind,
            Size = size,
            DeletedAt = Iso(DateTime.UtcNow),
            DeletedBy = actor,
        };
        await _dbFactory.WithContextTransactionAsync(ctx, (db, tx, _) =>
        {
            EnsureState(db, tx, r);
            db.Execute(@"INSERT INTO file_trash (id, original_path, kind, size, deleted_at, deleted_by)
                         VALUES (@Id, @OriginalPath, @Kind, @Size, @DeletedAt, @DeletedBy)", entry, tx);
            DeleteSubtreeRows(db, tx, r.Key(item.Rel));
            Journal(db, tx, "delete", item.Kind, item.Rel, null, item.Size, item.Mtime, null, true, "api", actor);
            return Task.CompletedTask;
        }, ct);
        return ToTrash(entry);
    }

    private static FileTrashEntry ToTrash(TrashRow t)
        => new(t.Id, t.OriginalPath, t.Kind, t.Size, ParseIso(t.DeletedAt) ?? DateTime.UtcNow, t.DeletedBy);

    public async Task<FileTrashEntry> TrashAsync(ContextRef ctx, string path, string actor, CancellationToken ct = default)
    {
        var r = Open(ctx);
        var src = r.Resolve(path, allowRoot: false);
        using (await LockAsync(r, ct))
            return await TrashCoreAsync(ctx, r, src, actor, ct);
    }

    public async Task DeletePermanentlyAsync(ContextRef ctx, string path, string actor, CancellationToken ct = default)
    {
        var r = Open(ctx);
        var src = r.Resolve(path, allowRoot: false);
        using (await LockAsync(r, ct))
        {
            var item = DiskFileStore.Stat(r, src.Rel) ?? throw FileStoreException.NotFound();
            await ReconcileFolderAsync(ctx, r, src.ParentRel, ct);
            var size = DiskFileStore.Measure(src.Full).Bytes;
            DiskFileStore.Delete(src.Full);
            await _dbFactory.WithContextTransactionAsync(ctx, (db, tx, _) =>
            {
                EnsureState(db, tx, r);
                DeleteSubtreeRows(db, tx, r.Key(item.Rel));
                Journal(db, tx, "delete", item.Kind == FileKinds.Link ? FileKinds.File : item.Kind, item.Rel, null, item.Size, item.Mtime, null, false, "api", actor);
                return Task.CompletedTask;
            }, ct);
            AdjustUsage(r, -size);
            _logger.LogInformation("Deleted {PathHash} permanently in {CtxType}:{CtxId}", Hash(item.Rel), ctx.Type, ctx.Id);
        }
    }

    public async Task<IReadOnlyList<FileTrashEntry>> ListTrashAsync(ContextRef ctx, CancellationToken ct = default)
    {
        var r = Open(ctx);
        using var db = _dbFactory.CreateContextConnection(ctx);
        var rows = (await db.QueryAsync<TrashRow>(new CommandDefinition(
            "SELECT * FROM file_trash ORDER BY deleted_at DESC", cancellationToken: ct))).ToList();
        var list = new List<FileTrashEntry>();
        var trashRoot = TrashRoot(r);
        foreach (var row in rows)
            if (Directory.Exists(Path.Combine(trashRoot, row.Id))) list.Add(ToTrash(row));
        // A .trash/<id>/ without its row (a partial cold import): listed as
        // "origin unknown", restorable to the root.
        if (Directory.Exists(trashRoot))
        {
            var known = rows.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in new DirectoryInfo(trashRoot).EnumerateDirectories("*", DiskFileStore.Shallow))
            {
                if (known.Contains(dir.Name) || (dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var child = dir.EnumerateFileSystemInfos("*", DiskFileStore.Shallow).FirstOrDefault();
                if (child is null) continue;
                list.Add(new FileTrashEntry(dir.Name, child.Name, child is DirectoryInfo ? FileKinds.Folder : FileKinds.File,
                    DiskFileStore.Measure(child.FullName).Bytes, dir.CreationTimeUtc, null));
            }
        }
        return list;
    }

    private static bool IsTrashId(string id) => Ulid.TryParse(id, out _);

    public async Task<FileEntry> RestoreAsync(ContextRef ctx, string trashId, FileConflictMode mode, string actor, CancellationToken ct = default)
    {
        if (!IsTrashId(trashId)) throw FileStoreException.NotFound();
        var r = Open(ctx);
        using (await LockAsync(r, ct))
        {
            var dir = Path.Combine(TrashRoot(r), trashId);
            TrashRow? row;
            using (var db = _dbFactory.CreateContextConnection(ctx))
                row = await db.QuerySingleOrDefaultAsync<TrashRow>(new CommandDefinition(
                    "SELECT * FROM file_trash WHERE id = @trashId", new { trashId }, cancellationToken: ct));
            var child = Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFileSystemInfos("*", DiskFileStore.Shallow).FirstOrDefault() : null;
            if (child is null)
            {
                if (row is not null)
                    using (var db = _dbFactory.CreateContextConnection(ctx))
                        await db.ExecuteAsync(new CommandDefinition("DELETE FROM file_trash WHERE id = @trashId", new { trashId }, cancellationToken: ct));
                throw FileStoreException.NotFound();
            }

            var target = r.Resolve(row?.OriginalPath ?? child.Name, allowRoot: false);
            if (DiskFileStore.Exists(target.Full))
            {
                if (mode == FileConflictMode.Fail) throw FileStoreException.Exists();
                target = FreeName(r, target, " (restored)", " (restored {0})");
            }

            List<DiskItem> created = new();
            await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
            {
                EnsureState(db, tx, r);
                // Missing parents come back, as a file explorer does it.
                EnsureFolders(db, tx, r, target.ParentRel, actor);
                DiskFileStore.Move(child.FullName, target.Full);
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
                db.Execute("DELETE FROM file_trash WHERE id = @trashId", new { trashId }, tx);
                var item = DiskFileStore.Stat(r, target.Rel)!;
                created.Add(item);
                if (item.Kind == FileKinds.Folder)
                    created.AddRange(DiskFileStore.Walk(r, item.Rel).Where(x => x.Kind != FileKinds.Link));
                IndexCreated(db, tx, r, created, actor);
                await Task.CompletedTask;
            }, ct);
            return ToEntry(created[0], null);
        }
    }

    // The next free name beside `taken`: "report (restored).pdf",
    // "report (restored 2).pdf", …
    private static ResolvedPath FreeName(FilePathResolver r, ResolvedPath taken, string first, string nth)
    {
        var ext = Path.GetExtension(taken.Name);
        var stem = taken.Name[..^ext.Length];
        if (stem.Length == 0) { stem = taken.Name; ext = ""; }
        for (var i = 1; i < 1000; i++)
        {
            var name = stem + (i == 1 ? first : string.Format(CultureInfo.InvariantCulture, nth, i)) + ext;
            var p = r.Resolve(FilePathResolver.Join(taken.ParentRel, name), create: true, allowRoot: false);
            if (!DiskFileStore.Exists(p.Full)) return p;
        }
        throw FileStoreException.Exists();
    }

    public async Task PurgeTrashAsync(ContextRef ctx, string trashId, CancellationToken ct = default)
    {
        if (!IsTrashId(trashId)) throw FileStoreException.NotFound();
        var r = Open(ctx);
        using (await LockAsync(r, ct))
        {
            if (!await PurgeOneAsync(ctx, r, trashId, ct)) throw FileStoreException.NotFound();
        }
    }

    private async Task<bool> PurgeOneAsync(ContextRef ctx, FilePathResolver r, string trashId, CancellationToken ct)
    {
        var dir = Path.Combine(TrashRoot(r), trashId);
        var found = Directory.Exists(dir);
        if (found)
        {
            var size = DiskFileStore.Measure(dir).Bytes;
            DiskFileStore.Delete(dir);
            AdjustUsage(r, -size);
        }
        using var db = _dbFactory.CreateContextConnection(ctx);
        var n = await db.ExecuteAsync(new CommandDefinition("DELETE FROM file_trash WHERE id = @trashId", new { trashId }, cancellationToken: ct));
        return found || n > 0;
    }

    public async Task<int> EmptyTrashAsync(ContextRef ctx, CancellationToken ct = default)
    {
        var r = Open(ctx);
        using (await LockAsync(r, ct))
        {
            var count = 0;
            var trashRoot = TrashRoot(r);
            if (Directory.Exists(trashRoot))
                foreach (var dir in new DirectoryInfo(trashRoot).EnumerateDirectories("*", DiskFileStore.Shallow).ToList())
                    if (await PurgeOneAsync(ctx, r, dir.Name, ct)) count++;
            using var db = _dbFactory.CreateContextConnection(ctx);
            await db.ExecuteAsync(new CommandDefinition("DELETE FROM file_trash", cancellationToken: ct));
            return count;
        }
    }

    // ───────────────────────────── change feed ─────────────────────────────

    private static string Cursor(string epoch, long seq) => $"{epoch}:{seq.ToString(CultureInfo.InvariantCulture)}";

    private static long Head(IDbConnection db, StateRow state)
        => Math.Max(db.ExecuteScalar<long?>("SELECT MAX(seq) FROM file_changes") ?? 0, state.FloorSeq);

    public async Task<FileChangesPage> GetChangesAsync(ContextRef ctx, string? cursor, int? limit, CancellationToken ct = default)
    {
        var r = Open(ctx);
        var s = await SettingsAsync(ct);
        using (await LockAsync(r, ct))
            await FullReconcileAsync(ctx, r, force: false, s, ct);

        using var db = _dbFactory.CreateContextConnection(ctx);
        var state = EnsureState(db, null, r);
        var head = Head(db, state);
        if (string.IsNullOrEmpty(cursor))
            return new FileChangesPage(Array.Empty<FileChange>(), Cursor(state.Epoch, head), false);

        var sep = cursor.LastIndexOf(':');
        if (sep <= 0 || !long.TryParse(cursor[(sep + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
            throw FileStoreException.CursorExpired();
        // Another epoch, compacted away, or ahead of the head (a DB restored
        // from an older backup): the client must take a snapshot.
        if (cursor[..sep] != state.Epoch || seq < state.FloorSeq || seq > head)
            throw FileStoreException.CursorExpired();

        var take = Math.Clamp(limit ?? 500, 1, 1000);
        var rows = (await db.QueryAsync<ChangeRow>(new CommandDefinition(
            "SELECT * FROM file_changes WHERE seq > @seq ORDER BY seq LIMIT @n", new { seq, n = take + 1 }, cancellationToken: ct))).ToList();
        var more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        var changes = rows.Select(x => new FileChange(x.Seq, x.Op, x.Kind, x.Path, x.OldPath, x.Size, ParseIso(x.Mtime),
            x.Etag, x.Sha256, x.Trashed != 0, x.Source, x.Actor, ParseIso(x.At) ?? DateTime.UtcNow)).ToList();
        var next = changes.Count > 0 ? changes[^1].Seq : seq;
        return new FileChangesPage(changes, Cursor(state.Epoch, next), more);
    }

    public async Task<(string Cursor, IReadOnlyList<FileSnapshotEntry> Entries)> SnapshotAsync(ContextRef ctx, CancellationToken ct = default)
    {
        var r = Open(ctx);
        var s = await SettingsAsync(ct);
        using (await LockAsync(r, ct))
        {
            await FullReconcileAsync(ctx, r, force: true, s, ct);
            using var db = _dbFactory.CreateContextConnection(ctx);
            var state = EnsureState(db, null, r);
            var cursor = Cursor(state.Epoch, Head(db, state));
            var rows = (await db.QueryAsync<IndexRow>(new CommandDefinition(
                "SELECT * FROM file_index WHERE kind IN ('file','folder') ORDER BY path", cancellationToken: ct))).ToList();
            var entries = rows.Select(x => new FileSnapshotEntry(x.Path, x.Kind, x.Kind == FileKinds.File ? x.Size : null,
                ParseIso(x.Mtime), x.Kind == FileKinds.File ? DiskFileStore.Etag(x.Size, ParseIso(x.Mtime)) : null, x.Sha256)).ToList();
            return (cursor, entries);
        }
    }

    // ───────────────────────────── transfer ─────────────────────────────

    public async Task<IReadOnlyList<FileTransferItemResult>> TransferAsync(ContextRef from, IReadOnlyList<string> paths,
        ContextRef to, string folder, bool move, FileConflictMode mode, string actor, CancellationToken ct = default)
    {
        var rs = Open(from);
        var rt = Open(to);
        var s = await SettingsAsync(ct);
        var targetFolder = rt.Resolve(folder);
        if (!targetFolder.IsRoot && !Directory.Exists(targetFolder.Full)) throw new FileStoreException(404, "not_found", "The target folder doesn't exist.");
        var sameContext = LockKey(rs) == LockKey(rt);
        var results = new List<FileTransferItemResult>();
        var targetOwner = await OwnerQuotaAsync(to, ct);
        var sourceOwner = move && !sameContext ? await OwnerQuotaAsync(from, ct) : null;
        // Moving between two workspaces of the same owner doesn't grow them.
        var ownerGrows = !(move && (sameContext || sourceOwner?.OwnerId == targetOwner?.OwnerId));

        using (await LockBothAsync(rs, rt, ct))
        {
            Directory.CreateDirectory(rt.Root);
            foreach (var p in paths)
            {
                try
                {
                    var src = rs.Resolve(p, allowRoot: false);
                    var item = DiskFileStore.Stat(rs, src.Rel) ?? throw FileStoreException.NotFound();
                    if (item.Kind == FileKinds.Link) throw FileStoreException.LinkNotFollowed();
                    await ReconcileFolderAsync(from, rs, src.ParentRel, ct);

                    var dst = rt.Resolve(FilePathResolver.Join(targetFolder.Rel, item.Name), create: true, allowRoot: false);
                    if (sameContext && (string.Equals(dst.Rel, src.Rel, rs.Comparison) || dst.Rel.StartsWith(src.Rel + "/", rs.Comparison)))
                        throw new FileStoreException(400, "into_own_subtree", "A folder can't go into itself.");
                    if (DiskFileStore.Exists(dst.Full))
                    {
                        if (mode == FileConflictMode.Fail) throw FileStoreException.Exists();
                        if (mode == FileConflictMode.Rename) dst = FreeName(rt, dst, " (2)", " ({0})");
                        else await TrashCoreAsync(to, rt, dst, actor, ct);   // replace: the old one goes to trash
                    }

                    var (bytes, nodes) = CountTree(rs, item);
                    CheckNodes(nodes);
                    CheckRoom(rt, s, sameContext && move ? 0 : bytes, sameContext && move ? 0 : bytes);
                    if (ownerGrows) CheckOwnerRoom(targetOwner, bytes);

                    List<DiskItem> created;
                    var moved = false;
                    if (move)
                    {
                        try
                        {
                            DiskFileStore.Move(src.Full, dst.Full);
                            moved = true;
                        }
                        catch (IOException) { /* another volume: copy, then delete below */ }
                    }
                    if (moved)
                    {
                        created = new List<DiskItem> { DiskFileStore.Stat(rt, dst.Rel)! };
                        if (created[0].Kind == FileKinds.Folder)
                            created.AddRange(DiskFileStore.Walk(rt, dst.Rel).Where(x => x.Kind != FileKinds.Link));
                    }
                    else
                    {
                        created = await CopyTreeAsync(rs, item, rt, dst, ct);
                        if (move) DiskFileStore.Delete(src.Full);
                    }

                    if (move)
                    {
                        await _dbFactory.WithContextTransactionAsync(from, (db, tx, _) =>
                        {
                            EnsureState(db, tx, rs);
                            DeleteSubtreeRows(db, tx, rs.Key(item.Rel));
                            Journal(db, tx, "delete", item.Kind, item.Rel, null, item.Size, item.Mtime, null, false, "api", actor);
                            return Task.CompletedTask;
                        }, ct);
                        if (!sameContext) AdjustUsage(rs, -bytes);
                    }
                    await _dbFactory.WithContextTransactionAsync(to, (db, tx, _) =>
                    {
                        EnsureState(db, tx, rt);
                        IndexCreated(db, tx, rt, created, actor);
                        return Task.CompletedTask;
                    }, ct);
                    if (!(sameContext && move)) AdjustUsage(rt, bytes);
                    if (ownerGrows) await OwnerGrewAsync(targetOwner, bytes, ct);
                    results.Add(new FileTransferItemResult(p, dst.Rel, null, null));
                }
                catch (FileStoreException ex)
                {
                    results.Add(new FileTransferItemResult(p, null, ex.Code, ex.Message));
                }
            }
        }
        _logger.LogInformation("Transfer ({Op}) of {Count} items {From}:{FromId} → {To}:{ToId}",
            move ? "move" : "copy", paths.Count, from.Type, from.Id, to.Type, to.Id);
        return results;
    }

    // ───────────────────────────── maintenance ─────────────────────────────

    public async Task RunMaintenanceAsync(ContextRef ctx, CancellationToken ct = default)
    {
        var folder = _dbFactory.ResolveContextFolder(ctx);
        if (!Directory.Exists(Path.Combine(folder, FilesFolder))) return;
        var r = Open(ctx);
        var s = await SettingsAsync(ct);
        using (await LockAsync(r, ct))
        {
            // Uploads killed mid-flight (a crashed process) leave .part files.
            var stack = new Stack<string>();
            stack.Push(r.Root);
            while (stack.Count > 0)
            {
                foreach (var info in new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos("*", DiskFileStore.Shallow))
                {
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (info is DirectoryInfo) { stack.Push(info.FullName); continue; }
                    if (info.Name.StartsWith(FileNameRules.TempPrefix, StringComparison.OrdinalIgnoreCase)
                        && info.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                        && DateTime.UtcNow - info.LastWriteTimeUtc > PartMaxAge)
                    {
                        try { info.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
            }

            if (s.TrashRetentionDays > 0)
            {
                var cutoff = Iso(DateTime.UtcNow.AddDays(-s.TrashRetentionDays));
                List<string> old;
                using (var db = _dbFactory.CreateContextConnection(ctx))
                    old = (await db.QueryAsync<string>(new CommandDefinition(
                        "SELECT id FROM file_trash WHERE deleted_at < @cutoff", new { cutoff }, cancellationToken: ct))).ToList();
                foreach (var id in old) await PurgeOneAsync(ctx, r, id, ct);
            }

            if (s.JournalRetentionDays > 0)
            {
                var cutoff = Iso(DateTime.UtcNow.AddDays(-s.JournalRetentionDays));
                await _dbFactory.WithContextTransactionAsync(ctx, (db, tx, _) =>
                {
                    var state = EnsureState(db, tx, r);
                    var through = db.ExecuteScalar<long?>("SELECT MAX(seq) FROM file_changes WHERE at < @cutoff", new { cutoff }, tx);
                    if (through is { } t && t > 0)
                    {
                        db.Execute("DELETE FROM file_changes WHERE seq <= @t", new { t }, tx);
                        db.Execute("UPDATE file_state SET floor_seq = @f WHERE id = 1", new { f = Math.Max(state.FloorSeq, t) }, tx);
                    }
                    return Task.CompletedTask;
                }, ct);
            }

            await FullReconcileAsync(ctx, r, force: true, s, ct);
        }
    }
}
