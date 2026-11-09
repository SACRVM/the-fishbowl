using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

// Operator utility: hot-snapshot of `fishbowl-data/` to a timestamped folder.
// Uses SQLite's online backup API for every .db file so the snapshot is safe
// to run against a live host (no locking, no half-written rows).
//
// Differential: every snapshot writes a manifest (snapshot.files.jsonl — each
// copied file's path, size and modification time). A file whose size and
// time are still what the newest earlier snapshot in --out recorded is not
// copied again: the new snapshot gets a hardlink to that snapshot's copy —
// no space, and every snapshot is still a complete folder on its own (delete
// any of them and the others keep their files). Where the target can't link
// (a filesystem without hardlinks, too many links to one file) the file is
// copied. The databases are always backed up whole.
//
// What's included:
//   system.db                              → SQLite backup
//   users/{userId}/personal.db             → SQLite backup
//   spaces/{spaceId}/space.db              → SQLite backup
//   acme/**                                → file copy (cert state; small, portable)
//
// What's deliberately excluded:
//   models/**                              → the ONNX embedding model (re-downloads on next boot)
//                                            Add `--include-models` if you need a
//                                            self-contained restore on an air-gapped
//                                            box.
//
// Restore: stop the host, replace the contents of `fishbowl-data/` with the
// snapshot folder, start the host again. The folder-per-context layout is
// preserved, so individual users/spaces can also be restored by copying just
// that subfolder.
//
// Retention (only when asked for — deleting backups is never a default):
// --keep-daily N keeps the newest N snapshots, --keep-monthly M also the
// newest snapshot of each of the last M months that have one. Everything
// else named snapshot-<yyyyMMdd>-<HHmmss> in --out goes, after a snapshot
// that succeeded; the one just made always stays.
//
// Usage:
//   dotnet run --project tools/snapshot-data -- \
//       [--data <path>] [--out <path>] [--include-models] [--full]
//       [--keep-daily <n>] [--keep-monthly <n>] [--quiet]
//
// Defaults:
//   --data            fishbowl-data
//   --out             fishbowl-backups
//   --include-models  off (models re-download)
//   --full            off (copy every file, link none)
//   --keep-*          off (nothing is deleted)
//   --quiet           off
//
// Exit codes:
//   0  success
//   1  bad arguments
//   2  source data dir missing
//   3  system.db not readable
//   4  destination dir already exists (same timestamp run twice in the same second)
//   5  one or more file operations failed (partial snapshot left on disk for inspection)
//   6  the snapshot succeeded, but an old one couldn't be deleted

var args_ = args;
var dataPath = GetArg("--data") ?? "fishbowl-data";
var outRoot = GetArg("--out") ?? "fishbowl-backups";
var includeModels = HasFlag("--include-models");
var full = HasFlag("--full");
var quiet = HasFlag("--quiet");
if (!TryCount("--keep-daily", 1, out var keepDaily) || !TryCount("--keep-monthly", 0, out var keepMonthly))
    return 1;

if (!Directory.Exists(dataPath))
{
    Console.Error.WriteLine($"error: data dir not found: {Path.GetFullPath(dataPath)}");
    return 2;
}
var systemDbPath = Path.Combine(dataPath, "system.db");
if (!File.Exists(systemDbPath))
{
    Console.Error.WriteLine($"error: system.db not found at {Path.GetFullPath(systemDbPath)}");
    return 3;
}

var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
var snapshotDir = Path.Combine(outRoot, $"snapshot-{timestamp}");
if (Directory.Exists(snapshotDir))
{
    Console.Error.WriteLine($"error: destination already exists: {snapshotDir}");
    return 4;
}

// The base: the newest earlier snapshot with a manifest (older ones, from
// before snapshots were differential, have none — the first run copies all).
var (baseDir, baseFiles) = full ? (null, new Dictionary<string, ManifestEntry>(StringComparer.Ordinal)) : FindBase(outRoot);
Directory.CreateDirectory(snapshotDir);

var failures = new List<string>();
var stats = new Stats();
var manifest = new List<ManifestEntry>();
string? linkProblem = null;     // why linking stopped being tried, once

Log($"# snapshot start — source={Path.GetFullPath(dataPath)} dest={Path.GetFullPath(snapshotDir)}");
Log(baseDir is null ? "  (no earlier snapshot with a manifest: every file is copied)" : $"  (unchanged files link to {Path.GetFileName(baseDir)})");

// 1. system.db
TryBackup(systemDbPath, Path.Combine(snapshotDir, "system.db"), "system.db");

// 2. users/{id}/personal.db
var usersRoot = Path.Combine(dataPath, "users");
if (Directory.Exists(usersRoot))
{
    foreach (var dir in Directory.EnumerateDirectories(usersRoot))
    {
        var name = Path.GetFileName(dir);
        if (string.IsNullOrEmpty(name)) continue;
        var src = Path.Combine(dir, "personal.db");
        if (!File.Exists(src))
        {
            Log($"  - users/{name}/ has no personal.db, skipping");
            continue;
        }
        var destDir = Path.Combine(snapshotDir, "users", name);
        Directory.CreateDirectory(destDir);
        TryBackup(src, Path.Combine(destDir, "personal.db"), $"users/{name}/personal.db");
        CopyContextExtras(dir, destDir, $"users/{name}");
    }
}

// 3. spaces/{id}/space.db
var spacesRoot = Path.Combine(dataPath, "spaces");
if (Directory.Exists(spacesRoot))
{
    foreach (var dir in Directory.EnumerateDirectories(spacesRoot))
    {
        var name = Path.GetFileName(dir);
        if (string.IsNullOrEmpty(name)) continue;
        var src = Path.Combine(dir, "space.db");
        if (!File.Exists(src))
        {
            Log($"  - spaces/{name}/ has no space.db, skipping");
            continue;
        }
        var destDir = Path.Combine(snapshotDir, "spaces", name);
        Directory.CreateDirectory(destDir);
        TryBackup(src, Path.Combine(destDir, "space.db"), $"spaces/{name}/space.db");
        CopyContextExtras(dir, destDir, $"spaces/{name}");
    }
}

// 3b. archive/ — archived spaces (self-contained ZIPs), file copy
var archiveRoot = Path.Combine(dataPath, "archive");
if (Directory.Exists(archiveRoot))
{
    TryCopyTree(archiveRoot, Path.Combine(snapshotDir, "archive"), "archive/",
        rel => !Path.GetFileName(rel).StartsWith('.'));   // half-written .zip.part files
}

// 4. acme/ — file copy (cert state, not a SQLite DB)
var acmeRoot = Path.Combine(dataPath, "acme");
if (Directory.Exists(acmeRoot))
{
    TryCopyTree(acmeRoot, Path.Combine(snapshotDir, "acme"), "acme/");
}

// 5. models/ — opt-in, file copy
var modelsRoot = Path.Combine(dataPath, "models");
if (Directory.Exists(modelsRoot))
{
    if (includeModels)
    {
        TryCopyTree(modelsRoot, Path.Combine(snapshotDir, "models"), "models/");
    }
    else
    {
        Log("  - models/ skipped (use --include-models to bundle ~470MB of ONNX weights)");
    }
}

// 6. the manifest — what the next snapshot links against — and a metadata
//    sidecar, so the operator (or a restore script) can sanity-check what's
//    inside without scanning files. Written last: a snapshot without them
//    never serves as a base.
using (var w = new StreamWriter(Path.Combine(snapshotDir, ManifestEntry.FileName)))
    foreach (var e in manifest) w.WriteLine(JsonSerializer.Serialize(e));
var meta = new
{
    snapshotAt = DateTime.UtcNow.ToString("o"),
    source = Path.GetFullPath(dataPath),
    fishbowlVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev",
    databases = stats.DbCount,
    bytes = stats.BytesWritten,
    files = manifest.Count,
    linkedFiles = stats.LinkedFiles,
    linkedBytes = stats.LinkedBytes,
    linkedTo = baseDir is null ? null : Path.GetFileName(baseDir),
    includedModels = includeModels,
};
File.WriteAllText(
    Path.Combine(snapshotDir, "snapshot.json"),
    JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

Console.Error.WriteLine();
Console.Error.WriteLine($"# done — {stats.DbCount} DB(s) backed up, {FormatBytes(stats.BytesWritten)} written, "
    + $"{stats.LinkedFiles} unchanged file(s) linked ({FormatBytes(stats.LinkedBytes)} not copied again)");
if (linkProblem is not null) Console.Error.WriteLine($"# note — files were copied instead of linked: {linkProblem}");
Console.Error.WriteLine($"# snapshot at: {Path.GetFullPath(snapshotDir)}");

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"# WARNING — {failures.Count} item(s) failed:");
    foreach (var f in failures) Console.Error.WriteLine($"  - {f}");
    return 5;
}

var pruned = keepDaily > 0 || keepMonthly > 0 ? Prune(outRoot, snapshotDir, keepDaily, keepMonthly) : true;

Console.WriteLine(snapshotDir);
return pruned ? 0 : 6;

void TryBackup(string src, string dest, string label)
{
    try
    {
        BackupSqliteFile(src, dest);
        var bytes = new FileInfo(dest).Length;
        stats.DbCount++;
        stats.BytesWritten += bytes;
        Log($"  + {label} ({FormatBytes(bytes)})");
    }
    catch (Exception ex)
    {
        var msg = $"{label}: {ex.GetType().Name} — {ex.Message}";
        failures.Add(msg);
        Console.Error.WriteLine($"  ! {msg}");
    }
}

// Beside the context DB: every apps/*/app.db (online backup) and the files/
// tree (plain copy, trash included, upload temps not). The DB goes first,
// then the files — a best-effort copy of a mutable tree; whatever drifted is
// what the reconcile after a restore journals.
void CopyContextExtras(string contextDir, string destDir, string label)
{
    var apps = Path.Combine(contextDir, "apps");
    if (Directory.Exists(apps))
        foreach (var appDir in Directory.EnumerateDirectories(apps))
        {
            var appDb = Path.Combine(appDir, "app.db");
            if (!File.Exists(appDb)) continue;
            var appDest = Path.Combine(destDir, "apps", Path.GetFileName(appDir));
            Directory.CreateDirectory(appDest);
            TryBackup(appDb, Path.Combine(appDest, "app.db"), $"{label}/apps/{Path.GetFileName(appDir)}/app.db");
        }
    var files = Path.Combine(contextDir, "files");
    if (Directory.Exists(files))
        TryCopyTree(files, Path.Combine(destDir, "files"), $"{label}/files/",
            rel => !Path.GetFileName(rel).StartsWith(".~fb-", StringComparison.OrdinalIgnoreCase));
}

// A tree, file by file: linked to the base snapshot's copy when the file is
// what the base recorded, else copied. One file that fails is reported and
// the rest go on.
void TryCopyTree(string srcRoot, string destRoot, string label, Func<string, bool>? include = null)
{
    long copied = 0, linked = 0;
    try
    {
        Directory.CreateDirectory(destRoot);
        foreach (var src in Directory.EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcRoot, src);
            if (include is not null && !include(rel)) continue;
            var dest = Path.Combine(destRoot, rel);
            var key = Path.GetRelativePath(snapshotDir, dest).Replace('\\', '/');
            try
            {
                // Read before the copy: a file changed while it is copied gets
                // a newer time than the one recorded, so the next run copies it again.
                var info = new FileInfo(src);
                var entry = new ManifestEntry(key, info.Length, info.LastWriteTimeUtc.Ticks);
                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                if (TryLink(entry, dest)) linked += entry.Size;
                else
                {
                    File.Copy(src, dest, overwrite: false);
                    copied += new FileInfo(dest).Length;
                }
                manifest.Add(entry);
            }
            catch (Exception ex)
            {
                var msg = $"{label}{rel}: {ex.GetType().Name} — {ex.Message}";
                failures.Add(msg);
                Console.Error.WriteLine($"  ! {msg}");
            }
        }
    }
    catch (Exception ex)
    {
        var msg = $"{label}: {ex.GetType().Name} — {ex.Message}";
        failures.Add(msg);
        Console.Error.WriteLine($"  ! {msg}");
    }
    stats.BytesWritten += copied;
    Log($"  + {label} ({FormatBytes(copied)} copied{(linked > 0 ? $", {FormatBytes(linked)} linked" : "")})");
}

// A hardlink to the base snapshot's copy, when the file is still what the
// base recorded and that copy is still there with its size.
bool TryLink(ManifestEntry entry, string dest)
{
    if (baseDir is null || linkProblem is not null) return false;
    if (!baseFiles.TryGetValue(entry.Path, out var was) || was.Size != entry.Size || was.Mtime != entry.Mtime) return false;
    var existing = Path.Combine(baseDir, entry.Path);
    var old = new FileInfo(existing);
    if (!old.Exists || old.Length != entry.Size) return false;
    if (HardLink.TryCreate(existing, dest, out var why))
    {
        stats.LinkedFiles++;
        stats.LinkedBytes += entry.Size;
        return true;
    }
    // Too many links to this one file: copy it — it is the next base. Any
    // other refusal means the target doesn't link at all; stop trying.
    if (!why.TooManyLinks) linkProblem = why.Message;
    return false;
}

// The newest snapshot-* folder in `root` with a manifest, and that manifest.
static (string? Dir, Dictionary<string, ManifestEntry> Files) FindBase(string root)
{
    var files = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
    if (!Directory.Exists(root)) return (null, files);
    foreach (var dir in SnapshotDirs(root).Select(d => d.Dir))
    {
        var path = Path.Combine(dir, ManifestEntry.FileName);
        if (!File.Exists(path) || !File.Exists(Path.Combine(dir, "snapshot.json"))) continue;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var e = JsonSerializer.Deserialize<ManifestEntry>(line);
            if (e is not null) files[e.Path] = e;
        }
        return (dir, files);
    }
    return (null, files);
}

// snapshot-<yyyyMMdd>-<HHmmss> folders, newest first (the name is the time).
static IEnumerable<(string Dir, string Stamp)> SnapshotDirs(string root) =>
    Directory.EnumerateDirectories(root)
        .Select(d => (Dir: d, Match: SnapshotName().Match(Path.GetFileName(d))))
        .Where(x => x.Match.Success)
        .Select(x => (x.Dir, Stamp: x.Match.Groups[1].Value))
        .OrderByDescending(x => x.Stamp, StringComparer.Ordinal);

// Keeps the newest `daily`, plus the newest of each of the last `monthly`
// months; deletes the other snapshot folders. Never the one just made.
bool Prune(string root, string current, int daily, int monthly)
{
    var all = SnapshotDirs(root).ToList();
    var keep = new HashSet<string>(StringComparer.Ordinal) { Path.GetFullPath(current) };
    foreach (var (dir, _) in all.Take(daily)) keep.Add(Path.GetFullPath(dir));
    foreach (var month in all.GroupBy(x => x.Stamp[..6]).Take(monthly))
        keep.Add(Path.GetFullPath(month.First().Dir));
    var ok = true;
    foreach (var (dir, _) in all)
    {
        if (keep.Contains(Path.GetFullPath(dir))) continue;
        try
        {
            Directory.Delete(dir, recursive: true);
            Log($"  - deleted {Path.GetFileName(dir)} (retention)");
        }
        catch (Exception ex)
        {
            ok = false;
            Console.Error.WriteLine($"  ! couldn't delete {Path.GetFileName(dir)}: {ex.GetType().Name} — {ex.Message}");
        }
    }
    Console.Error.WriteLine($"# retention — kept {keep.Count} snapshot(s) (newest {daily}, monthly {monthly})");
    return ok;
}

void Log(string line)
{
    if (!quiet) Console.Error.WriteLine(line);
}

string? GetArg(string flag)
{
    var i = Array.IndexOf(args_, flag);
    if (i < 0 || i + 1 >= args_.Length) return null;
    return args_[i + 1];
}

bool HasFlag(string flag) => Array.IndexOf(args_, flag) >= 0;

// A count flag: absent = 0 (off); present = a whole number ≥ min.
bool TryCount(string flag, int min, out int value)
{
    value = 0;
    if (!HasFlag(flag)) return true;
    if (int.TryParse(GetArg(flag), out value) && value >= min) return true;
    Console.Error.WriteLine($"error: {flag} needs a whole number ≥ {min}");
    return false;
}

static string FormatBytes(long bytes)
{
    if (bytes < 1024) return $"{bytes} B";
    if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
    if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
    return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
}

// SQLite online backup. The destination connection sets Pooling=False so that
// when we dispose it the OS handle closes immediately — otherwise the pool
// holds a lock on the snapshot file and a subsequent integrity-check would
// fail on Windows. Mirrors the pattern in ExportApi.BackupContextAsync.
//
// Off Windows the destination is opened through SQLite's `unix-none` VFS (no
// file locks): snapshots usually go to a network share, and a Linux CIFS
// mount refuses SQLite's byte-range locks — every write there failed with
// "database is locked". The copy is a new file in a new folder that only this
// process writes, so the locks guard nothing. Not the `nolock=1` parameter:
// it also turns WAL off, and the copy carries its source's WAL flag, so the
// switch to DELETE below could no longer open it. The source keeps its normal
// locks: it may be a live host's database. Windows stays on the plain path,
// which works against its shares.
static void Exec(SqliteConnection c, string sql)
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

static void BackupSqliteFile(string sourcePath, string destPath)
{
    using var source = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = sourcePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString());
    source.Open();

    using var dest = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = OperatingSystem.IsWindows()
            ? destPath
            : new Uri(Path.GetFullPath(destPath)).AbsoluteUri + "?vfs=unix-none",
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString());
    dest.Open();

    // A backup carries its source's WAL flag into the copy; the snapshot is
    // made a plain rollback-journal file again (it lives on a share, where
    // WAL's shared memory doesn't work). Exclusive locking first keeps SQLite
    // from making a -shm beside it on the way.
    Exec(dest, "PRAGMA locking_mode = EXCLUSIVE");
    source.BackupDatabase(dest);
    Exec(dest, "PRAGMA journal_mode = DELETE");
    dest.Close();
    source.Close();
}

sealed class Stats
{
    public int DbCount;
    public long BytesWritten;
    public int LinkedFiles;
    public long LinkedBytes;
}

// One file of a snapshot: its path in the snapshot ('/'-separated), and the
// size and modification time (UTC ticks) its source had when it was copied.
sealed record ManifestEntry(
    [property: System.Text.Json.Serialization.JsonPropertyName("p")] string Path,
    [property: System.Text.Json.Serialization.JsonPropertyName("s")] long Size,
    [property: System.Text.Json.Serialization.JsonPropertyName("m")] long Mtime)
{
    public const string FileName = "snapshot.files.jsonl";
}

static partial class Program
{
    [GeneratedRegex(@"^snapshot-(\d{8}-\d{6})$")]
    private static partial Regex SnapshotName();
}

// A hardlink, by the operating system's own call (.NET has none).
static class HardLink
{
    public readonly record struct Refusal(string Message, bool TooManyLinks);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int UnixLink(string existing, string link);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsLink(string link, string existing, IntPtr security);

    private const int EMLINK = 31;                       // Linux, macOS
    private const int ERROR_TOO_MANY_LINKS = 1142;

    public static bool TryCreate(string existing, string link, out Refusal why)
    {
        why = default;
        try
        {
            if (OperatingSystem.IsWindows() ? WindowsLink(link, existing, IntPtr.Zero) : UnixLink(existing, link) == 0)
                return true;
            var code = Marshal.GetLastPInvokeError();
            why = new Refusal($"the target refused a hardlink (error {code})",
                code == (OperatingSystem.IsWindows() ? ERROR_TOO_MANY_LINKS : EMLINK));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            why = new Refusal($"no hardlinks on this system ({ex.GetType().Name})", false);
        }
        return false;
    }
}
