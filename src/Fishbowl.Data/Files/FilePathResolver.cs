using System.Collections.Concurrent;
using System.Text;
using Fishbowl.Core.Files;

namespace Fishbowl.Data.Files;

// A wire path resolved against one context's files/ root.
public sealed record ResolvedPath(string Rel, string Full, string Name)
{
    public bool IsRoot => Rel.Length == 0;
    public string ParentRel => Rel.LastIndexOf('/') is var i and >= 0 ? Rel[..i] : "";
}

// Turns a relative, '/'-separated wire path into a disk path under the
// context's files/ root — or a 400. Nothing else in Fishbowl combines paths
// for user files. Rules (spec § Path safety):
//   1. no leading '/' or '\', nothing rooted (C:, UNC, \\?\), no empty / '.'
//      / '..' segments, no '\' anywhere, reserved names refused;
//   2. every segment checked with the host's name rules (FileNameRules);
//   3. the full path must stay under the root;
//   4. links are never followed: any existing component that is a reparse
//      point (symlink, junction, mount point) is refused.
public sealed class FilePathResolver
{
    public string Root { get; }
    public NameRuleSet Rules { get; }
    public bool CaseSensitive { get; }
    public bool LongPaths { get; }
    public StringComparison Comparison => CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public FilePathResolver(string root, NameRuleSet rules, bool caseSensitive, bool longPaths)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Rules = rules;
        CaseSensitive = caseSensitive;
        LongPaths = longPaths;
    }

    // The index key of a relative path: the path itself, case-folded on an
    // insensitive volume so "A.txt" and "a.txt" are one entry.
    public string Key(string rel) => CaseSensitive ? rel : rel.ToUpperInvariant();

    public static string Join(string parentRel, string name) => parentRel.Length == 0 ? name : parentRel + "/" + name;

    public string FullOf(string rel)
        => rel.Length == 0 ? Root : Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    // `create` = the last segment names something Fishbowl is about to
    // create (stricter rules + NFC); `createAll` = every segment (mkdir -p).
    public ResolvedPath Resolve(string? path, bool create = false, bool createAll = false, bool allowRoot = true)
    {
        if (string.IsNullOrEmpty(path))
        {
            if (!allowRoot) throw FileStoreException.InvalidPath("That needs a file or folder, not the root.");
            return new ResolvedPath("", Root, "");
        }
        if (path[0] is '/' or '\\')
            throw FileStoreException.InvalidPath("Paths are relative to your files — without a leading slash.");
        if (path.EndsWith('/')) path = path[..^1];
        if (path.Length == 0)
        {
            if (!allowRoot) throw FileStoreException.InvalidPath("That needs a file or folder, not the root.");
            return new ResolvedPath("", Root, "");
        }
        if (path.Contains('\\'))
            throw FileStoreException.InvalidName("separator", path, "Paths use / — names can't contain \\.");
        if (Path.IsPathRooted(path))
            throw FileStoreException.InvalidPath("Absolute paths aren't allowed.");

        var segments = path.Split('/');
        if (segments.Length > FileLimits.MaxDepth)
            throw FileStoreException.InvalidPath($"Folders can be at most {FileLimits.MaxDepth} levels deep.");

        for (var i = 0; i < segments.Length; i++)
        {
            var creating = createAll || (create && i == segments.Length - 1);
            var violation = creating
                ? FileNameRules.Check(segments[i], Rules, i == 0, CaseSensitive)
                : FileNameRules.CheckAddress(segments[i], Rules, i == 0, CaseSensitive);
            if (violation is { } v) throw FileStoreException.InvalidName(v.Rule, segments[i], v.Message);
            // New names are stored NFC; existing names are addressed as listed.
            if (creating) segments[i] = segments[i].Normalize(NormalizationForm.FormC);
        }

        var rel = string.Join('/', segments);
        var full = Path.GetFullPath(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, Comparison))
            throw FileStoreException.InvalidPath("That path leaves your files.");

        var length = Rules == NameRuleSet.Windows ? full.Length : Encoding.UTF8.GetByteCount(full);
        if (length > FileNameRules.MaxPath(Rules, LongPaths))
            throw FileStoreException.InvalidName("path_too_long", segments[^1],
                "That path is too long for this server.");

        // Walk the existing part of the path; never pass through a link.
        var cur = Root;
        foreach (var seg in segments)
        {
            cur = Path.Combine(cur, seg);
            FileAttributes attrs;
            try { attrs = File.GetAttributes(cur); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            if ((attrs & FileAttributes.ReparsePoint) != 0) throw FileStoreException.LinkNotFollowed();
        }

        return new ResolvedPath(rel, full, segments[^1]);
    }
}

// Facts about the volume a context's files live on, probed once per folder
// and cached for the process: case sensitivity (created a probe file, looked
// for it under another case — right for case-sensitive NTFS folders,
// casefolded ext4 and default-insensitive APFS) and Windows long-path support.
public static class FileVolume
{
    private static readonly ConcurrentDictionary<string, bool> CaseCache = new(StringComparer.Ordinal);
    private static readonly Lazy<bool> LongPathsLazy = new(ReadLongPaths);

    public static bool LongPathsEnabled => LongPathsLazy.Value;

    public static bool IsCaseSensitive(string folder)
        => CaseCache.GetOrAdd(Path.GetFullPath(folder), static f =>
        {
            Directory.CreateDirectory(f);
            var probe = Path.Combine(f, $"{FileNameRules.TempPrefix}probe-{Ulid.NewUlid()}-a");
            try
            {
                File.WriteAllBytes(probe, Array.Empty<byte>());
                var other = Path.Combine(f, Path.GetFileName(probe).ToUpperInvariant());
                return !File.Exists(other);
            }
            catch (IOException) { return !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS(); }
            catch (UnauthorizedAccessException) { return !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS(); }
            finally
            {
                try { File.Delete(probe); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        });

    private static bool ReadLongPaths()
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            return key?.GetValue("LongPathsEnabled") is int v && v == 1;
        }
        catch (Exception) { return false; }
    }
}
