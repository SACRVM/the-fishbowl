using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Core.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Files;

// Archived accounts (admin spec § Delete, phase A3) — the SpaceArchiver's
// twin for users/<id>/: fishbowl-data/archive/users/<userId>-<stamp>.zip with
// a manifest.json, same retention (Archive:RetentionDays). The ZIP holds
// personal.db and every apps/*/app.db (online backup API) and files/ incl.
// the trash: unzipped into another instance's users/<id>/ it is a cold import.
public sealed class UserArchiver : IUserArchiveService
{
    private const string ManifestEntry = "manifest.json";
    private const string DeletedPrefix = ".deleted-";
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly DatabaseFactory _db;
    private readonly ISystemRepository _system;
    private readonly ILogger<UserArchiver> _logger;

    public UserArchiver(DatabaseFactory db, ISystemRepository system, ILogger<UserArchiver>? logger = null)
    {
        _db = db;
        _system = system;
        _logger = logger ?? NullLogger<UserArchiver>.Instance;
    }

    private string ArchiveRoot => Path.Combine(Path.GetDirectoryName(_db.UsersRoot)!, "archive", "users");

    private async Task<int> RetentionDaysAsync(CancellationToken ct)
    {
        var v = await _system.GetConfigAsync(DataLifecycleLimits.ArchiveRetentionDaysKey, ct);
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d >= 0
            ? d : DataLifecycleLimits.DefaultArchiveRetentionDays;
    }

    public async Task<ArchivedUser?> ArchiveAsync(string userId, string? name, DateTime createdAt, string actorId,
        CancellationToken ct = default)
    {
        var folder = _db.ResolveContextFolder(ContextRef.User(userId));
        if (!Directory.Exists(folder)) return null;
        Directory.CreateDirectory(ArchiveRoot);
        CheckFreeSpace(DiskFileStore.Measure(folder).Bytes);

        var dbPath = Path.Combine(folder, DatabaseFactory.PersonalDbFileName);
        var now = DateTime.UtcNow;
        long? schema = null;
        if (File.Exists(dbPath))
            schema = ReadSchemaVersion(dbPath);
        var manifest = new UserArchiveManifest(1, userId, name, createdAt, now, actorId,
            typeof(UserArchiver).Assembly.GetName().Version?.ToString(), schema);

        var id = $"{userId}-{now:yyyyMMddHHmmss}";
        for (var n = 2; File.Exists(Path.Combine(ArchiveRoot, id + ".zip")); n++)
            id = $"{userId}-{now:yyyyMMddHHmmss}-{n}";
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

                // Straight from the file, not through the factory: an archive
                // of a disabled or blocked account must not run migrations or
                // the "may this account store" guard.
                if (File.Exists(dbPath))
                {
                    ZipExport.AddDatabaseFile(zip, DatabaseFactory.PersonalDbFileName, dbPath, ct);
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

            // Only a ZIP that reads back complete counts: the account's folder
            // is deleted after this returns.
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

        var size = new FileInfo(final).Length;
        _logger.LogInformation("Archived user {UserId} as {ArchiveId} ({Bytes} bytes)", userId, id, size);
        var days = await RetentionDaysAsync(ct);
        return ToArchived(id, manifest, size, days);
    }

    private static long? ReadSchemaVersion(string dbPath)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            conn.Open();
            return conn.ExecuteScalar<long>("PRAGMA user_version");
        }
        catch (SqliteException) { return null; }
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

    public Task DeleteUserFolderAsync(string userId, CancellationToken ct = default)
    {
        var folder = _db.ResolveContextFolder(ContextRef.User(userId));
        if (!Directory.Exists(folder)) return Task.CompletedTask;
        // Pooled handles would keep personal.db open (Windows refuses the delete).
        SqliteConnection.ClearAllPools();
        if (TryDelete(folder)) return Task.CompletedTask;
        var parked = Path.Combine(_db.UsersRoot, DeletedPrefix + userId);
        try
        {
            Directory.Move(folder, parked);
            _logger.LogWarning("User folder {UserId} busy — parked for the sweeper", userId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "User folder {UserId} could not be removed or parked", userId);
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

    private sealed record Found(string Id, string Path, UserArchiveManifest Manifest, long Size);

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

    private UserArchiveManifest? ReadManifest(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(ManifestEntry);
            if (entry is null) return null;
            using var s = entry.Open();
            return JsonSerializer.Deserialize<UserArchiveManifest>(s, Json);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Unreadable user archive {ArchiveFile} skipped", System.IO.Path.GetFileName(path));
            return null;
        }
    }

    private static ArchivedUser ToArchived(string id, UserArchiveManifest m, long size, int days)
        => new(id, m.UserId, m.ArchivedAt, size, days > 0 ? m.ArchivedAt.AddDays(days) : null);

    public async Task<IReadOnlyList<ArchivedUser>> ListAsync(CancellationToken ct = default)
    {
        var days = await RetentionDaysAsync(ct);
        return All().Select(f => ToArchived(f.Id, f.Manifest, f.Size, days))
            .OrderByDescending(a => a.ArchivedAt)
            .ToList();
    }

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
                    _logger.LogWarning("User archive {ArchiveId} past retention could not be deleted yet", f.Id);
                }
            }
        }

        if (Directory.Exists(ArchiveRoot))
            foreach (var part in Directory.EnumerateFiles(ArchiveRoot, ".*.zip.part"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(part) > LeftoverAge)
                    try { File.Delete(part); } catch (IOException) { /* next tick */ }
        if (Directory.Exists(_db.UsersRoot))
            foreach (var dir in Directory.EnumerateDirectories(_db.UsersRoot))
                if (System.IO.Path.GetFileName(dir).StartsWith(DeletedPrefix, StringComparison.Ordinal))
                    TryDelete(dir);

        if (purged > 0) _logger.LogInformation("Purged {Count} archived accounts past {Days} days", purged, days);
        return purged;
    }
}
