using Microsoft.Data.Sqlite;

// Operator utility: hot-snapshot of `fishbowl-data/` to a timestamped folder.
// Uses SQLite's online backup API for every .db file so the snapshot is safe
// to run against a live host (no locking, no half-written rows).
//
// What's included:
//   system.db                              → SQLite backup
//   users/{userId}/personal.db             → SQLite backup
//   teams/{teamId}/team.db                 → SQLite backup
//   acme/**                                → file copy (cert state; small, portable)
//
// What's deliberately excluded:
//   models/**                              → ONNX MiniLM (re-downloads on next boot)
//                                            Add `--include-models` if you need a
//                                            self-contained restore on an air-gapped
//                                            box.
//
// Restore: stop the host, replace the contents of `fishbowl-data/` with the
// snapshot folder, start the host again. The folder-per-context layout is
// preserved, so individual users/teams can also be restored by copying just
// that subfolder.
//
// Usage:
//   dotnet run --project tools/snapshot-data -- \
//       [--data <path>] [--out <path>] [--include-models] [--quiet]
//
// Defaults:
//   --data            fishbowl-data
//   --out             fishbowl-backups
//   --include-models  off (models re-download)
//   --quiet           off
//
// Exit codes:
//   0  success
//   2  source data dir missing
//   3  system.db not readable
//   4  destination dir already exists (same timestamp run twice in the same second)
//   5  one or more file operations failed (partial snapshot left on disk for inspection)

var args_ = args;
var dataPath = GetArg("--data") ?? "fishbowl-data";
var outRoot = GetArg("--out") ?? "fishbowl-backups";
var includeModels = HasFlag("--include-models");
var quiet = HasFlag("--quiet");

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
Directory.CreateDirectory(snapshotDir);

var failures = new List<string>();
var stats = new Stats();

Log($"# snapshot start — source={Path.GetFullPath(dataPath)} dest={Path.GetFullPath(snapshotDir)}");

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
    }
}

// 3. teams/{id}/team.db
var teamsRoot = Path.Combine(dataPath, "teams");
if (Directory.Exists(teamsRoot))
{
    foreach (var dir in Directory.EnumerateDirectories(teamsRoot))
    {
        var name = Path.GetFileName(dir);
        if (string.IsNullOrEmpty(name)) continue;
        var src = Path.Combine(dir, "team.db");
        if (!File.Exists(src))
        {
            Log($"  - teams/{name}/ has no team.db, skipping");
            continue;
        }
        var destDir = Path.Combine(snapshotDir, "teams", name);
        Directory.CreateDirectory(destDir);
        TryBackup(src, Path.Combine(destDir, "team.db"), $"teams/{name}/team.db");
    }
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
        Log("  - models/ skipped (use --include-models to bundle ~90MB of ONNX weights)");
    }
}

// 6. metadata sidecar — small JSON next to the snapshot so the operator (or
//    a restore script) can sanity-check what's inside without scanning files.
var meta = new
{
    snapshotAt = DateTime.UtcNow.ToString("o"),
    source = Path.GetFullPath(dataPath),
    fishbowlVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev",
    databases = stats.DbCount,
    bytes = stats.BytesWritten,
    includedModels = includeModels,
};
File.WriteAllText(
    Path.Combine(snapshotDir, "snapshot.json"),
    System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

Console.Error.WriteLine();
Console.Error.WriteLine($"# done — {stats.DbCount} DB(s) backed up, {FormatBytes(stats.BytesWritten)} written");
Console.Error.WriteLine($"# snapshot at: {Path.GetFullPath(snapshotDir)}");

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"# WARNING — {failures.Count} item(s) failed:");
    foreach (var f in failures) Console.Error.WriteLine($"  - {f}");
    return 5;
}

Console.WriteLine(snapshotDir);
return 0;

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

void TryCopyTree(string srcRoot, string destRoot, string label)
{
    try
    {
        Directory.CreateDirectory(destRoot);
        long copied = 0;
        foreach (var src in Directory.EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcRoot, src);
            var dest = Path.Combine(destRoot, rel);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            File.Copy(src, dest, overwrite: false);
            copied += new FileInfo(dest).Length;
        }
        stats.BytesWritten += copied;
        Log($"  + {label} ({FormatBytes(copied)})");
    }
    catch (Exception ex)
    {
        var msg = $"{label}: {ex.GetType().Name} — {ex.Message}";
        failures.Add(msg);
        Console.Error.WriteLine($"  ! {msg}");
    }
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
        DataSource = destPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString());
    dest.Open();

    source.BackupDatabase(dest);
    dest.Close();
    source.Close();
}

sealed class Stats
{
    public int DbCount;
    public long BytesWritten;
}
