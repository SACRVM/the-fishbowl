using Fishbowl.Core.Models;

namespace Fishbowl.Core.Files;

// Files phase 3 — data lifecycle (spec § Operational touch points, § Space
// delete and archived spaces): export ZIPs and archived spaces.
public static class DataLifecycleLimits
{
    // Archived spaces older than this many days are purged by the daily tick;
    // 0 keeps them forever.
    public const string ArchiveRetentionDaysKey = "Archive:RetentionDays";
    public const int DefaultArchiveRetentionDays = 30;

    // "Database + files in one ZIP" is offered only while the files are below
    // this size; above it the two separate downloads remain.
    public const string ExportCombinedMaxBytesKey = "Export:CombinedMaxBytes";
    public const long DefaultExportCombinedMaxBytes = 4L * 1024 * 1024 * 1024;   // 4 GiB

    // The share of a quota at which the user gets a quota.warning message.
    public const double QuotaWarningRatio = 0.9;
}

// What the manifest inside an archive ZIP records. Members are kept for the
// record only — a restore brings none of them back.
public sealed record ArchiveManifest(
    int Version,
    string SpaceId,
    string Slug,
    string Name,
    string? Color,
    string OwnerId,
    DateTime CreatedAt,
    DateTime ArchivedAt,
    string ArchivedBy,
    IReadOnlyList<ArchiveMember> Members,
    string? FishbowlVersion,
    long? SchemaVersion);

public sealed record ArchiveMember(string UserId, string Role);

// One archived space as the owner sees it. `Id` is the ZIP's file name
// without ".zip" (`<spaceId>-<yyyyMMddHHmmss>`); `ExpiresAt` is null when
// archives are kept forever.
public sealed record ArchivedSpace(
    string Id,
    string SpaceId,
    string Slug,
    string Name,
    string? Color,
    DateTime ArchivedAt,
    long SizeBytes,
    DateTime? ExpiresAt);

// Sizes the export dialog shows before anything is downloaded.
public sealed record ExportInfo(long DbBytes, long FilesBytes, long CombinedMaxBytes, bool CombinedAllowed);

public interface ISpaceArchiveService
{
    // ZIPs the whole space folder (DBs via the online backup API, files/
    // incl. trash) plus a manifest into archive/spaces/. Verifies the ZIP
    // before returning; the caller deletes the space afterwards.
    Task<ArchivedSpace> ArchiveAsync(Space space, string actorId, CancellationToken ct = default);

    // Removes a deleted space's folder. A folder that can't go right now
    // (a file locked on Windows) is renamed to spaces/.deleted-<id> and
    // retried by PurgeAsync.
    Task DeleteSpaceFolderAsync(string spaceId, CancellationToken ct = default);

    // Archives the user archived or owned, newest first.
    Task<IReadOnlyList<ArchivedSpace>> ListAsync(string userId, CancellationToken ct = default);

    // Full path of an archive the user may reach, or null.
    Task<string?> GetPathAsync(string archiveId, string userId, CancellationToken ct = default);

    // A new space (new id, original slug if free else -2…) with the user as
    // its only member; no API keys; the files journal epoch rotates.
    Task<Space?> RestoreAsync(string archiveId, string userId, CancellationToken ct = default);

    Task<bool> DeleteAsync(string archiveId, string userId, CancellationToken ct = default);

    // The daily tick: archives past Archive:RetentionDays and leftover
    // .deleted-/.restoring- folders. Returns how many archives went.
    Task<int> PurgeAsync(CancellationToken ct = default);
}
