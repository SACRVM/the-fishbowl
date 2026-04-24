using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

// tools/update-vendor — fetch vendored third-party assets, verify SHA-256
// against vendor.lock.json, write to disk. Fails loud on mismatch so a
// silently-replaced CDN blob can't sneak into the repo.
//
// Usage:
//   dotnet run --project tools/update-vendor                  verify + refresh
//   dotnet run --project tools/update-vendor -- --pin         recompute & rewrite SHAs
//   dotnet run --project tools/update-vendor -- --dry-run     fetch + verify, no write
//
// The manifest (vendor.lock.json) lives at the repo root; the tool walks up
// from cwd until it finds one. outputPath entries resolve relative to the
// manifest's directory.

var modePin = args.Contains("--pin");
var modeDry = args.Contains("--dry-run");

var manifestPath = FindManifest();
if (manifestPath is null)
{
    Console.Error.WriteLine("error: no vendor.lock.json found in cwd or any ancestor directory.");
    return 2;
}
var root = Path.GetDirectoryName(manifestPath)!;

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

var manifest = JsonSerializer.Deserialize<Manifest>(
    await File.ReadAllTextAsync(manifestPath), jsonOpts)
    ?? throw new InvalidOperationException("empty manifest");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("fishbowl-update-vendor/1.0");

var mismatch = false;
foreach (var e in manifest.Entries)
{
    Console.WriteLine($"→ {e.Name}@{e.Version}");
    Console.WriteLine($"  url:    {e.Url}");

    byte[] bytes;
    try { bytes = await http.GetByteArrayAsync(e.Url); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  !! fetch failed: {ex.Message}");
        mismatch = true;
        continue;
    }
    var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    Console.WriteLine($"  bytes:  {bytes.Length:N0}");
    Console.WriteLine($"  sha256: {sha}");

    if (!modePin && !string.IsNullOrEmpty(e.Sha256) &&
        !sha.Equals(e.Sha256, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"  !! sha256 mismatch — expected {e.Sha256}");
        mismatch = true;
        continue;
    }

    var outPath = Path.Combine(root, e.OutputPath.Replace('/', Path.DirectorySeparatorChar));
    var rel = Path.GetRelativePath(root, outPath).Replace('\\', '/');

    if (modeDry)
    {
        Console.WriteLine($"  dry:    would write {rel}");
    }
    else
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllBytesAsync(outPath, bytes);
        Console.WriteLine($"  wrote:  {rel}");
    }

    if (modePin) e.Sha256 = sha;
}

if (modePin)
{
    var updated = JsonSerializer.Serialize(manifest, jsonOpts);
    await File.WriteAllTextAsync(manifestPath, updated + Environment.NewLine);
    Console.WriteLine($"\npinned SHAs → {Path.GetRelativePath(root, manifestPath).Replace('\\', '/')}");
}

if (mismatch)
{
    Console.Error.WriteLine("\nFAIL: one or more entries mismatched or failed to fetch.");
    return 1;
}

Console.WriteLine($"\nOK: {manifest.Entries.Count} entries {(modeDry ? "verified" : "up to date")}.");
return 0;

static string? FindManifest()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        var p = Path.Combine(dir.FullName, "vendor.lock.json");
        if (File.Exists(p)) return p;
        dir = dir.Parent;
    }
    return null;
}

sealed class Manifest
{
    public List<Entry> Entries { get; set; } = [];
}

sealed class Entry
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string? License { get; set; }
    public string Sha256 { get; set; } = "";
}
