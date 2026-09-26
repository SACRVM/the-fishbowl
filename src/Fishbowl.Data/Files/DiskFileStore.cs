using System.Security.Cryptography;
using Fishbowl.Core.Files;

namespace Fishbowl.Data.Files;

// What the disk says about one entry.
public sealed record DiskItem(string Rel, string Name, string Kind, long? Size, DateTime? Mtime);

// The only code that touches a context's files/ tree. Paths come in already
// resolved (FilePathResolver); nothing here follows a reparse point or
// shows Fishbowl's own reserved names.
public static class DiskFileStore
{
    public static readonly EnumerationOptions Shallow = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
    };

    public static string Etag(long? size, DateTime? mtime)
        => mtime is { } m ? $"{size ?? 0}-{m.ToUniversalTime().Ticks}" : $"{size ?? 0}-0";

    public static bool Exists(string full) => File.Exists(full) || Directory.Exists(full);

    public static DiskItem? Stat(FilePathResolver r, string rel)
    {
        var full = r.FullOf(rel);
        FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        if (!info.Exists) return null;
        return ToItem(rel, info);
    }

    private static DiskItem ToItem(string rel, FileSystemInfo info)
    {
        var name = info.Name;
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            return new DiskItem(rel, name, FileKinds.Link, null, null);
        if (info is DirectoryInfo d)
            return new DiskItem(rel, name, FileKinds.Folder, null, d.LastWriteTimeUtc);
        var f = (FileInfo)info;
        return new DiskItem(rel, name, FileKinds.File, f.Length, f.LastWriteTimeUtc);
    }

    // The visible children of a folder (reserved names skipped).
    public static List<DiskItem> Children(FilePathResolver r, string folderRel)
    {
        var full = r.FullOf(folderRel);
        var list = new List<DiskItem>();
        if (!Directory.Exists(full)) return list;
        foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos("*", Shallow))
        {
            if (FileNameRules.IsReserved(info.Name, folderRel.Length == 0, r.CaseSensitive)) continue;
            list.Add(ToItem(FilePathResolver.Join(folderRel, info.Name), info));
        }
        return list;
    }

    // Every visible entry below a folder, depth-first, never through a link.
    public static List<DiskItem> Walk(FilePathResolver r, string folderRel)
    {
        var all = new List<DiskItem>();
        void Recurse(string rel, int depth)
        {
            foreach (var item in Children(r, rel))
            {
                all.Add(item);
                if (item.Kind == FileKinds.Folder && depth < FileLimits.MaxDepth) Recurse(item.Rel, depth + 1);
            }
        }
        Recurse(folderRel, 1);
        return all;
    }

    // Total bytes of every file below a folder, including hidden/reserved
    // ones (trash, in-flight parts) — what the folder costs on disk.
    public static (long Bytes, long Files, long Folders) Measure(string full)
    {
        if (File.Exists(full)) return (new FileInfo(full).Length, 1, 0);
        if (!Directory.Exists(full)) return (0, 0, 0);
        long bytes = 0, files = 0, folders = 0;
        var stack = new Stack<string>();
        stack.Push(full);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Shallow))
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (info is DirectoryInfo) { folders++; stack.Push(info.FullName); }
                else { files++; bytes += ((FileInfo)info).Length; }
            }
        }
        return (bytes, files, folders);
    }

    public static string TempName() => $"{FileNameRules.TempPrefix}{Ulid.NewUlid()}.part";

    public static void Hide(string full)
    {
        if (OperatingSystem.IsWindows())
            File.SetAttributes(full, File.GetAttributes(full) | FileAttributes.Hidden);
    }

    public static void Unhide(string full)
    {
        if (OperatingSystem.IsWindows())
            File.SetAttributes(full, File.GetAttributes(full) & ~FileAttributes.Hidden);
    }

    // Moves a file or folder; IO sharing violations become 423.
    public static void Move(string fromFull, string toFull, bool overwriteFile = false)
    {
        try
        {
            if (Directory.Exists(fromFull)) Directory.Move(fromFull, toFull);
            else File.Move(fromFull, toFull, overwriteFile);
        }
        catch (IOException ex) when (IsSharingViolation(ex)) { throw FileStoreException.Locked(); }
        catch (UnauthorizedAccessException) { throw FileStoreException.Locked(); }
    }

    public static void Delete(string full)
    {
        try
        {
            if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(full, recursive: true);
            else if (Directory.Exists(full)) Directory.Delete(full);   // a link: remove the link, not its target
            else File.Delete(full);
        }
        catch (IOException ex) when (IsSharingViolation(ex)) { throw FileStoreException.Locked(); }
        catch (UnauthorizedAccessException) { throw FileStoreException.Locked(); }
    }

    // Copies one file into a hidden temp file beside the target, then renames
    // it into place, so a half-copied file is never visible.
    public static async Task<string> CopyFileAsync(string fromFull, string toFull, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(toFull)!;
        var temp = Path.Combine(dir, TempName());
        try
        {
            await using (var src = new FileStream(fromFull, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true))
            await using (var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await src.CopyToAsync(dst, ct);
            }
            Move(temp, toFull);
            return toFull;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static async Task<string> Sha256Async(string full, CancellationToken ct)
    {
        await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
    }

    public static byte[] Head(string full)
    {
        try
        {
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[FileLimits.SniffBytes];
            var n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            return buf[..n];
        }
        catch (IOException) { return Array.Empty<byte>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<byte>(); }
    }

    private static bool IsSharingViolation(IOException ex)
        => (ex.HResult & 0xFFFF) is 32 or 33;   // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
}
