using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Files;

// Archived spaces (spec § Space delete and archived spaces). The archive
// folder is the truth: fishbowl-data/archive/spaces/<spaceId>-<stamp>.zip,
// each carrying a manifest.json — no table, so copying the folder moves the
// archives, like files/. A ZIP holds space.db and every apps/*/app.db (online
// backup API) and files/ including the trash: unzipped into another
// instance's spaces/ it is a cold import.
public sealed class SpaceArchiver : ISpaceArchiveService
{
    private const string ManifestEntry = "manifest.json";
    private const string DeletedPrefix = ".deleted-";
    private const string RestoringPrefix = ".restoring-";
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly DatabaseFactory _db;
    private readonly ISystemRepository _system;
    private readonly ILogger<SpaceArchiver> _logger;

    public SpaceArchiver(DatabaseFactory db, ISystemRepository system, ILogger<SpaceArchiver>? logger = null)
    {
        _db = db;
        _system = system;
        _logger = logger ?? NullLogger<SpaceArchiver>.Instance;
    }

    private string ArchiveRoot => Path.Combine(Path.GetDirectoryName(_db.UsersRoot)!, "archive", "spaces");

    private async Task<int> RetentionDaysAsync(CancellationToken ct)
    {
        var v = await _system.GetConfigAsync(DataLifecycleLimits.ArchiveRetentionDaysKey, ct);
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d >= 0
            ? d : DataLifecycleLimits.DefaultArchiveRetentionDays;
    }

    // ───────────────────────────── archive ─────────────────────────────

    public async Task<ArchivedSpace> ArchiveAsync(Space space, string actorId, CancellationToken ct = default)
    {
        var folder = _db.ResolveContextFolder(ContextRef.Space(space.Id));
        Directory.CreateDirectory(ArchiveRoot);
        CheckFreeSpace(DiskFileStore.Measure(folder).Bytes);

        List<ArchiveMember> members;
        using (var sys = _db.CreateSystemConnection())
            members = (await sys.QueryAsync<ArchiveMember>(new CommandDefinition(
                "SELECT user_id AS UserId, role AS Role FROM space_members WHERE space_id = @id ORDER BY joined_at",
                new { id = space.Id }, cancellationToken: ct))).ToList();

        var dbPath = Path.Combine(folder, DatabaseFactory.SpaceDbFileName);
        long? schema = null;
        if (File.Exists(dbPath))
            using (var conn = _db.CreateContextConnection(ContextRef.Space(space.Id)))
                schema = conn.ExecuteScalar<long>("PRAGMA user_version");

        var now = DateTime.UtcNow;
        var manifest = new ArchiveManifest(1, space.Id, space.Slug, space.Name, space.Color,
            members.FirstOrDefault(m => m.Role == SpaceRole.Owner.ToDbValue())?.UserId ?? space.CreatedBy,
            space.CreatedAt, now, actorId, members,
            typeof(SpaceArchiver).Assembly.GetName().Version?.ToString(), schema);

        var id = $"{space.Id}-{now:yyyyMMddHHmmss}";
        for (var n = 2; File.Exists(Path.Combine(ArchiveRoot, id + ".zip")); n++)
            id = $"{space.Id}-{now:yyyyMMddHHmmss}-{n}";
        var final = Path.Combine(ArchiveRoot, id + ".zip");
        var temp = Path.Combine(ArchiveRoot, $".{id}.zip.part");

        try
        {
            var written = 0;
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var m = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                using (var s = m.Open()) JsonSerializer.Serialize(s, manifest, Json);
                written++;

                if (File.Exists(dbPath))
                {
                    using var conn = (SqliteConnection)_db.CreateContextConnection(ContextRef.Space(space.Id));
                    ZipExport.AddDatabase(zip, DatabaseFactory.SpaceDbFileName, conn, ct);
                    written++;
                }

                var apps = Path.Combine(folder, "apps");
                if (Directory.Exists(apps))
                    foreach (var appDir in Directory.EnumerateDirectories(apps))
                    {
                        var appDb = Path.Combine(appDir, DatabaseFactory.AppDbFileName);
                        if (!File.Exists(appDb)) continue;
                        ZipExport.AddDatabaseFile(zip, $"apps/{Path.GetFileName(appDir)}/{DatabaseFactory.AppDbFileName}", appDb, ct);
                        written++;
                    }

                written += ZipExport.AddFolder(zip, Path.Combine(folder, "files"), "files", ZipExport.SkipForArchive, ct);
            }

            // Only a ZIP that reads back complete counts: the space is
            // deleted after this returns.
            using (var check = ZipFile.OpenRead(temp))
                if (check.Entries.Count != written)
                    throw new IOException($"Archive check failed: {check.Entries.Count} of {written} entries.");
            File.Move(temp, final);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { /* the sweeper retries */ }
            throw;
        }

        _logger.LogInformation("Archived space {SpaceId} as {ArchiveId} ({Bytes} bytes)", space.Id, id, new FileInfo(final).Length);
        var days = await RetentionDaysAsync(ct);
        return ToArchived(id, manifest, new FileInfo(final).Length, days);
    }

    private void CheckFreeSpace(long needed)
    {
        var root = Path.GetPathRoot(ArchiveRoot);
        if (string.IsNullOrEmpty(root)) return;
        long free;
        try { free = new DriveInfo(root).AvailableFreeSpace; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return; }
        if (free < needed + FileLimits.DefaultMinFreeBytes / 4) throw FileStoreException.InsufficientStorage();
    }

    public Task DeleteSpaceFolderAsync(string spaceId, CancellationToken ct = default)
    {
        var folder = _db.ResolveContextFolder(ContextRef.Space(spaceId));
        if (!Directory.Exists(folder)) return Task.CompletedTask;
        // Pooled handles would keep space.db open (Windows refuses the delete).
        SqliteConnection.ClearAllPools();
        if (TryDelete(folder)) return Task.CompletedTask;
        var parked = Path.Combine(_db.SpacesRoot, DeletedPrefix + spaceId);
        try
        {
            Directory.Move(folder, parked);
            _logger.LogWarning("Space folder {SpaceId} busy — parked for the sweeper", spaceId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Space folder {SpaceId} could not be removed or parked", spaceId);
        }
        return Task.CompletedTask;
    }

    private static bool TryDelete(string folder)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);   // hidden/read-only temp files
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ───────────────────────────── list / read ─────────────────────────────

    private sealed record Found(string Id, string Path, ArchiveManifest Manifest, long Size);

    private IEnumerable<Found> All()
    {
        if (!Directory.Exists(ArchiveRoot)) yield break;
        foreach (var path in Directory.EnumerateFiles(ArchiveRoot, "*.zip"))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith('.')) continue;
            var manifest = ReadManifest(path);
            if (manifest is null) continue;
            yield return new Found(name, path, manifest, new FileInfo(path).Length);
        }
    }

    private ArchiveManifest? ReadManifest(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(ManifestEntry);
            if (entry is null) return null;
            using var s = entry.Open();
            return JsonSerializer.Deserialize<ArchiveManifest>(s, Json);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Unreadable archive {ArchiveFile} skipped", System.IO.Path.GetFileName(path));
            return null;
        }
    }

    private static bool Reaches(ArchiveManifest m, string userId)
        => string.Equals(m.ArchivedBy, userId, StringComparison.Ordinal)
           || string.Equals(m.OwnerId, userId, StringComparison.Ordinal);

    private static ArchivedSpace ToArchived(string id, ArchiveManifest m, long size, int days)
        => new(id, m.SpaceId, m.Slug, m.Name, m.Color, m.ArchivedAt, size,
            days > 0 ? m.ArchivedAt.AddDays(days) : null);

    public async Task<IReadOnlyList<ArchivedSpace>> ListAsync(string userId, CancellationToken ct = default)
    {
        var days = await RetentionDaysAsync(ct);
        return All().Where(f => Reaches(f.Manifest, userId))
            .Select(f => ToArchived(f.Id, f.Manifest, f.Size, days))
            .OrderByDescending(a => a.ArchivedAt)
            .ToList();
    }

    private Found? Find(string archiveId, string userId)
    {
        // An id is a file name we made — never a path.
        if (string.IsNullOrEmpty(archiveId) || archiveId.StartsWith('.')
            || archiveId.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || archiveId.Contains(".."))
            return null;
        var path = System.IO.Path.Combine(ArchiveRoot, archiveId + ".zip");
        if (!File.Exists(path)) return null;
        var manifest = ReadManifest(path);
        return manifest is not null && Reaches(manifest, userId)
            ? new Found(archiveId, path, manifest, new FileInfo(path).Length)
            : null;
    }

    public Task<string?> GetPathAsync(string archiveId, string userId, CancellationToken ct = default)
        => Task.FromResult(Find(archiveId, userId)?.Path);

    // ───────────────────────────── restore ─────────────────────────────

    public async Task<Space?> RestoreAsync(string archiveId, string userId, CancellationToken ct = default)
    {
        var found = Find(archiveId, userId);
        if (found is null) return null;
        var m = found.Manifest;
        CheckFreeSpace(found.Size * 2);

        var newId = Ulid.NewUlid().ToString();
        var staging = System.IO.Path.Combine(_db.SpacesRoot, RestoringPrefix + newId);
        var target = _db.ResolveContextFolder(ContextRef.Space(newId));
        try
        {
            // ExtractToDirectory refuses entries that would land outside
            // the target (zip-slip), so a crafted archive can't escape.
            ZipFile.ExtractToDirectory(found.Path, staging);
            File.Delete(System.IO.Path.Combine(staging, ManifestEntry));
            Directory.Move(staging, target);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }

        var space = new Space
        {
            Id = newId,
            Name = m.Name,
            Color = m.Color,
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow,
        };
        using (var sys = _db.CreateSystemConnection())
        using (var tx = sys.BeginTransaction())
        {
            space.Slug = SlugGenerator.DedupeAgainst(m.Slug, candidate =>
                sys.ExecuteScalar<long>("SELECT COUNT(*) FROM spaces WHERE slug = @candidate", new { candidate }, tx) > 0);
            await sys.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO spaces(id, slug, name, created_by, created_at, color)
                VALUES (@Id, @Slug, @Name, @CreatedBy, @CreatedAt, @Color)",
                new { space.Id, space.Slug, space.Name, space.CreatedBy, CreatedAt = space.CreatedAt.ToString("o"), space.Color },
                transaction: tx, cancellationToken: ct));
            // The restoring user is the only member — former members stay in
            // the manifest, for the record. No API key comes back either.
            await sys.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO space_members(space_id, user_id, role, joined_at)
                VALUES (@SpaceId, @UserId, @Role, @JoinedAt)",
                new { SpaceId = newId, UserId = userId, Role = SpaceRole.Owner.ToDbValue(), JoinedAt = space.CreatedAt.ToString("o") },
                transaction: tx, cancellationToken: ct));
            tx.Commit();
        }

        // A restored tree is another history: the files journal starts a new
        // epoch, so a sync client that knew the old space re-snapshots.
        using (var conn = _db.CreateContextConnection(ContextRef.Space(newId)))
        {
            var hasState = conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'file_state'") > 0;
            if (hasState)
                conn.Execute("UPDATE file_state SET epoch = @epoch, last_scan_at = NULL WHERE id = 1",
                    new { epoch = Ulid.NewUlid().ToString() });
        }

        _logger.LogInformation("Restored archive {ArchiveId} as space {SpaceId}", archiveId, newId);
        return space;
    }

    public Task<bool> DeleteAsync(string archiveId, string userId, CancellationToken ct = default)
    {
        var found = Find(archiveId, userId);
        if (found is null) return Task.FromResult(false);
        File.Delete(found.Path);
        _logger.LogInformation("Deleted archive {ArchiveId}", archiveId);
        return Task.FromResult(true);
    }

    // ───────────────────────────── purge ─────────────────────────────

    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var purged = 0;
        var days = await RetentionDaysAsync(ct);
        if (days > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-days);
            foreach (var f in All().ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (f.Manifest.ArchivedAt >= cutoff) continue;
                try { File.Delete(f.Path); purged++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Archive {ArchiveId} past retention could not be deleted yet", f.Id);
                }
            }
        }

        // Leftovers: a half-written archive, a parked space folder, a restore
        // that died between extract and rename.
        if (Directory.Exists(ArchiveRoot))
            foreach (var part in Directory.EnumerateFiles(ArchiveRoot, ".*.zip.part"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(part) > LeftoverAge)
                    try { File.Delete(part); } catch (IOException) { /* next tick */ }
        if (Directory.Exists(_db.SpacesRoot))
            foreach (var dir in Directory.EnumerateDirectories(_db.SpacesRoot))
            {
                var name = System.IO.Path.GetFileName(dir);
                var stale = name.StartsWith(DeletedPrefix, StringComparison.Ordinal)
                            || (name.StartsWith(RestoringPrefix, StringComparison.Ordinal)
                                && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > LeftoverAge);
                if (stale) TryDelete(dir);
            }

        if (purged > 0) _logger.LogInformation("Purged {Count} archived spaces past {Days} days", purged, days);
        return purged;
    }
}
