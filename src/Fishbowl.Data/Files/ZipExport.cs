using System.IO.Compression;
using Fishbowl.Core.Files;
using Microsoft.Data.Sqlite;

namespace Fishbowl.Data.Files;

// Streams workspace data into a ZIP (spec § Operational touch points): an
// export ZIP to the response, an archive ZIP to disk. Entries are written one
// by one straight to the target stream — nothing is built in memory, and
// ZipArchive switches to ZIP64 on its own. Already-compressed types are
// stored, everything else deflated.
public static class ZipExport
{
    private static readonly HashSet<string> StoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".gz", ".tgz", ".7z", ".rar", ".xz", ".bz2", ".zst", ".br",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif",
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac",
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi",
        ".pdf", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub",
        ".woff", ".woff2",
    };

    private static readonly DateTime ZipMin = new(1980, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ZipMax = new(2107, 12, 30, 0, 0, 0, DateTimeKind.Utc);

    public static CompressionLevel LevelFor(string name)
        => StoredExtensions.Contains(Path.GetExtension(name)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;

    private static DateTimeOffset ZipTime(DateTime utc)
        => utc < ZipMin ? ZipMin : utc > ZipMax ? ZipMax : utc;

    // A consistent copy of a live SQLite DB (online backup API) into one
    // entry. The backup goes through a temp file — the backup API needs a
    // database to write into — which is streamed into the entry and deleted.
    public static void AddDatabase(ZipArchive zip, string entryName, SqliteConnection source, CancellationToken ct = default)
    {
        var temp = Path.GetTempFileName();
        try
        {
            using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temp,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,   // the handle must close so the temp file can be read and deleted
            }.ToString()))
            {
                dest.Open();
                source.BackupDatabase(dest);
            }
            ct.ThrowIfCancellationRequested();
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = ZipTime(DateTime.UtcNow);
            using var input = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            using var output = entry.Open();
            input.CopyTo(output, 81920);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { /* best effort */ }
        }
    }

    // Same, for a DB file no DatabaseFactory context owns (an app's app.db).
    public static void AddDatabaseFile(ZipArchive zip, string entryName, string dbPath, CancellationToken ct = default)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        source.Open();
        AddDatabase(zip, entryName, source, ct);
    }

    // Every file and folder under `folderFull` as `prefix/<relative path>`
    // (forward slashes), empty folders included. Links are never followed.
    // `skip(rel, isFolder)` leaves an item (and a skipped folder's subtree)
    // out. Returns the number of entries written.
    public static int AddFolder(ZipArchive zip, string folderFull, string prefix,
        Func<string, bool, bool>? skip = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(folderFull)) return 0;
        var count = 0;
        var stack = new Stack<string>();
        stack.Push(folderFull);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var any = false;
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", DiskFileStore.Shallow))
            {
                ct.ThrowIfCancellationRequested();
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var rel = Path.GetRelativePath(folderFull, info.FullName).Replace('\\', '/');
                var isFolder = info is DirectoryInfo;
                if (skip?.Invoke(rel, isFolder) == true) continue;
                any = true;
                if (isFolder) { stack.Push(info.FullName); continue; }

                var name = prefix.Length == 0 ? rel : prefix + "/" + rel;
                var entry = zip.CreateEntry(name, LevelFor(info.Name));
                entry.LastWriteTime = ZipTime(info.LastWriteTimeUtc);
                using (var input = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920))
                using (var output = entry.Open())
                    input.CopyTo(output, 81920);
                count++;
            }
            // An empty folder still belongs to the tree: a directory entry.
            if (!any && dir != folderFull)
            {
                var rel = Path.GetRelativePath(folderFull, dir).Replace('\\', '/');
                var entry = zip.CreateEntry((prefix.Length == 0 ? rel : prefix + "/" + rel) + "/");
                entry.LastWriteTime = ZipTime(Directory.GetLastWriteTimeUtc(dir));
                count++;
            }
        }
        return count;
    }

    // What "all files" means for an export: the tree minus trash and the
    // hidden temp files of uploads in flight.
    public static bool SkipForExport(string rel, bool isFolder)
    {
        if (rel.Equals(FileNameRules.TrashFolder, StringComparison.OrdinalIgnoreCase)) return true;
        var name = rel[(rel.LastIndexOf('/') + 1)..];
        return !isFolder && name.StartsWith(FileNameRules.TempPrefix, StringComparison.OrdinalIgnoreCase);
    }

    // An archive keeps the trash (a restore brings the space back as it was)
    // but never an upload that was still being written.
    public static bool SkipForArchive(string rel, bool isFolder)
    {
        var name = rel[(rel.LastIndexOf('/') + 1)..];
        return !isFolder && name.StartsWith(FileNameRules.TempPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
