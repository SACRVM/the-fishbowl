namespace Fishbowl.Core.Files;

// Admin A3 — deleting an account with "archive first" (admin spec § Delete):
// the whole users/<id>/ folder goes into archive/users/<id>-<stamp>.zip with
// a manifest, the same machinery and retention as archived spaces. The
// archive folder is the truth (no table). There's no restore in the UI: an
// operator unzips the archive into users/<folder>/ and registers it with the
// admin cold import (POST /api/v1/admin/users/import).
public sealed record UserArchiveManifest(
    int Version,
    string UserId,
    string? Name,
    DateTime CreatedAt,
    DateTime ArchivedAt,
    string ArchivedBy,
    string? FishbowlVersion,
    long? SchemaVersion);

// One archive as the admin sees it: ids, dates and sizes only, never content.
public sealed record ArchivedUser(
    string Id,
    string UserId,
    DateTime ArchivedAt,
    long SizeBytes,
    DateTime? ExpiresAt);

public interface IUserArchiveService
{
    // ZIPs users/<id>/ (personal.db and apps/*/app.db via the online backup
    // API, files/ incl. trash) plus a manifest, and verifies the ZIP before
    // returning. Null when the account never stored anything (no folder).
    Task<ArchivedUser?> ArchiveAsync(string userId, string? name, DateTime createdAt, string actorId,
        CancellationToken ct = default);

    // Removes a deleted account's folder; a folder that can't go right now
    // is parked as users/.deleted-<id> and swept by PurgeAsync.
    Task DeleteUserFolderAsync(string userId, CancellationToken ct = default);

    // Every user archive, newest first — for the admin's System page.
    Task<IReadOnlyList<ArchivedUser>> ListAsync(CancellationToken ct = default);

    // The daily tick: archives past Archive:RetentionDays (0 keeps them) and
    // leftover .deleted- folders / half-written archives. Returns how many
    // archives went.
    Task<int> PurgeAsync(CancellationToken ct = default);
}
