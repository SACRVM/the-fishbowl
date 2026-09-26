namespace Fishbowl.Core.Files;

// The Files app (docs/superpowers/specs/2026-09-26-files-app-design.md).
// Every path on the wire is relative to the context's files/ root,
// '/'-separated, without a leading '/'; "" is the root folder.

public static class FileKinds
{
    public const string File = "file";
    public const string Folder = "folder";
    // A symlink/junction/reparse point found on disk: listed so the user sees
    // why it can't be opened, never followed.
    public const string Link = "link";
}

public sealed record FileEntry(
    string Name,
    string Path,
    string Kind,
    long? Size,
    DateTime? Mtime,
    string? Etag,
    string? Sha256,
    string? Mime,
    string? CreatedBy);

public sealed record FileChange(
    long Seq,
    string Op,          // create | modify | move | delete
    string Kind,        // file | folder
    string Path,
    string? OldPath,
    long? Size,
    DateTime? Mtime,
    string? Etag,
    string? Sha256,
    bool Trashed,
    string Source,      // api | disk
    string? Actor,
    DateTime At);

public sealed record FileTrashEntry(
    string Id,
    string OriginalPath,
    string Kind,
    long Size,
    DateTime DeletedAt,
    string? DeletedBy);

public sealed record FileCapabilities(
    bool CaseSensitive,
    string NameRules,   // windows | posix | macos
    int MaxSegment,
    int MaxPath,
    long MaxFileBytes,
    long QuotaBytes);

// OwnerBytes / OwnerQuotaBytes: the owner's whole storage (personal + owned spaces)
// against their quota (0 = unlimited) — admin spec § Quotas.
public sealed record FileUsage(long Bytes, long QuotaBytes, long Files, long Folders, long TrashBytes,
    long OwnerBytes = 0, long OwnerQuotaBytes = 0);

public sealed record FileListPage(string Path, IReadOnlyList<FileEntry> Entries, string? Next);

public sealed record FileChangesPage(IReadOnlyList<FileChange> Changes, string Cursor, bool More);

// One line of the NDJSON snapshot.
public sealed record FileSnapshotEntry(string Path, string Kind, long? Size, DateTime? Mtime, string? Etag, string? Sha256);

public sealed record FileUploadResult(FileEntry Entry, bool Created);

public enum FileConflictMode { Fail, Rename, Replace }

public sealed record FileTransferItemResult(string From, string? To, string? Error, string? Message);
