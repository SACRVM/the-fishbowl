namespace Fishbowl.Core.Files;

// An open file for download: the stream (the caller disposes it), what the
// file is, and its sniffed effective type.
public sealed record FileReadHandle(Stream Stream, FileEntry Entry, string Mime);

// The Files app's single entry point (FilesApi, the maintenance tick, later
// MCP). Paths are relative wire paths; every method throws
// FileStoreException for a refusal the caller maps 1:1 to HTTP.
public interface IFileService
{
    Task<FileCapabilities> GetCapabilitiesAsync(ContextRef ctx, CancellationToken ct = default);
    Task<FileListPage> ListAsync(ContextRef ctx, string? path, string? cursor, int? limit, CancellationToken ct = default);
    Task<FileEntry> StatAsync(ContextRef ctx, string path, CancellationToken ct = default);
    Task<FileReadHandle> OpenReadAsync(ContextRef ctx, string path, CancellationToken ct = default);

    // Conditional by design: ifNoneMatchStar = create only, ifMatch = replace
    // exactly that version; neither → 428.
    Task<FileUploadResult> UploadAsync(ContextRef ctx, string path, Stream body, long? contentLength,
        string? ifMatch, bool ifNoneMatchStar, bool parents, string actor, CancellationToken ct = default);

    Task<FileEntry> CreateFolderAsync(ContextRef ctx, string path, string actor, CancellationToken ct = default);
    Task<FileEntry> MoveAsync(ContextRef ctx, string from, string to, string actor, CancellationToken ct = default);
    Task<FileEntry> CopyAsync(ContextRef ctx, string from, string to, string actor, CancellationToken ct = default);

    Task<FileTrashEntry> TrashAsync(ContextRef ctx, string path, string actor, CancellationToken ct = default);
    Task DeletePermanentlyAsync(ContextRef ctx, string path, string actor, CancellationToken ct = default);
    Task<IReadOnlyList<FileTrashEntry>> ListTrashAsync(ContextRef ctx, CancellationToken ct = default);
    Task<FileEntry> RestoreAsync(ContextRef ctx, string trashId, FileConflictMode mode, string actor, CancellationToken ct = default);
    Task PurgeTrashAsync(ContextRef ctx, string trashId, CancellationToken ct = default);
    Task<int> EmptyTrashAsync(ContextRef ctx, CancellationToken ct = default);

    // keepAppCode: leave what came from a space's .apps (a member below
    // Designer empties everything else).
    Task<int> EmptyTrashAsync(ContextRef ctx, bool keepAppCode, CancellationToken ct = default);

    Task<FileUsage> GetUsageAsync(ContextRef ctx, CancellationToken ct = default);

    // Would `bytes` more fit — the workspace's quota, its owner's quota, the
    // server's cap and free-space floor? Throws the 413/507 a write would;
    // books nothing (a check before a batch of writes, each checked again).
    Task CheckRoomAsync(ContextRef ctx, long bytes, CancellationToken ct = default);

    // A space that doesn't exist yet and `ownerId` will own (an archive
    // restore): `totalBytes` on disk, `filesBytes` of them under files/.
    // Checked like a write and booked against the owner's quota and the
    // server's cap, so back-to-back restores can't each pass on one total.
    Task ReserveNewSpaceAsync(string ownerId, long filesBytes, long totalBytes, CancellationToken ct = default);
    Task<FileChangesPage> GetChangesAsync(ContextRef ctx, string? cursor, int? limit, CancellationToken ct = default);
    Task<(string Cursor, IReadOnlyList<FileSnapshotEntry> Entries)> SnapshotAsync(ContextRef ctx, CancellationToken ct = default);

    Task<IReadOnlyList<FileTransferItemResult>> TransferAsync(ContextRef from, IReadOnlyList<string> paths,
        ContextRef to, string folder, bool move, FileConflictMode mode, string actor, CancellationToken ct = default);

    // Daily: sweep stale uploads, purge old trash, compact the journal,
    // reconcile the whole tree. Does nothing for a context without files/.
    Task RunMaintenanceAsync(ContextRef ctx, CancellationToken ct = default);
}
