using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

// Instance administration over system.db: account states, approval, the
// admin count, and the admin log. Metadata only — nothing here reads a
// user's content.
public interface IUserAdminRepository
{
    Task<IReadOnlyList<AdminUserRow>> ListUsersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListAdminIdsAsync(CancellationToken ct = default);

    // Active admins only — the ones who can act.
    Task<int> CountActiveAdminsAsync(CancellationToken ct = default);

    // False when nothing changed — also when it would leave no active admin
    // (the count is part of the write). Leaving active deletes the account's
    // open OAuth codes.
    Task<bool> SetStateAsync(string userId, string state, CancellationToken ct = default);

    // An approved, active local-password account in one transaction (users
    // row + local mapping, password to be changed at the first sign-in).
    // False — and nothing written — when the id or the username is taken.
    Task<bool> CreateLocalAccountAsync(
        string userId, string? name, string username, string passwordHash, string passwordSalt,
        string approverId, long? quotaBytes, CancellationToken ct = default);

    // pending → active, recording who approved and the quota (null = default).
    Task<bool> ApproveAsync(string userId, string approverId, long? quotaBytes, CancellationToken ct = default);

    Task<bool> TouchSignInAsync(string userId, CancellationToken ct = default);

    // The account's storage quota in bytes: null = the instance default,
    // 0 = unlimited.
    Task<bool> SetQuotaAsync(string userId, long? quotaBytes, CancellationToken ct = default);

    // Removes a never-approved shell: its mappings and its users row. Refuses
    // (false) for anything but a pending account.
    Task<bool> DeletePendingAsync(string userId, CancellationToken ct = default);

    // Spaces where this account is the only owner — they must be handed over
    // or deleted before the account can go (admin spec § Delete).
    Task<IReadOnlyList<OwnedSpaceRow>> ListSolelyOwnedSpacesAsync(string userId, CancellationToken ct = default);

    // Removes an account from system.db in one transaction: its sign-in
    // mappings, API keys (its own and its apps'), notification channels,
    // Discord link codes, space memberships, messages addressed to it, its
    // per-user config latches, and the users row — and leaves a tombstone in
    // deleted_users. The folder is the caller's (archive, then delete).
    // Refuses (false) the last active admin or a sole owner of a space —
    // checked inside the transaction.
    Task<bool> DeleteUserAsync(string userId, CancellationToken ct = default);

    // True for an id whose account was deleted, in any spelling.
    Task<bool> IsDeletedAsync(string userId, CancellationToken ct = default);

    Task RecordAdminActionAsync(string actorId, string action, string? targetType, string? targetId, CancellationToken ct = default);

    Task<IReadOnlyList<AdminAuditEntry>> ListAuditAsync(int limit = 200, CancellationToken ct = default);
}

// What the Users list shows about a person: who they are and how they sign
// in, never what they store.
public sealed record AdminUserRow(
    string Id,
    string? Name,
    string? Email,
    IReadOnlyList<string> Providers,
    bool IsAdmin,
    string State,
    long? QuotaBytes,
    DateTime CreatedAt,
    DateTime? LastSignInAt,
    DateTime? ApprovedAt);

public sealed record OwnedSpaceRow(string Id, string Slug, string Name);

public sealed record AdminAuditEntry(
    string Id,
    string ActorId,
    string Action,
    string? TargetType,
    string? TargetId,
    DateTime At);
