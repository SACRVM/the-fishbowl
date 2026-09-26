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

    Task<bool> SetStateAsync(string userId, string state, CancellationToken ct = default);

    // pending → active, recording who approved and the quota (null = default).
    Task<bool> ApproveAsync(string userId, string approverId, long? quotaBytes, CancellationToken ct = default);

    Task<bool> TouchSignInAsync(string userId, CancellationToken ct = default);

    // The account's storage quota in bytes: null = the instance default,
    // 0 = unlimited.
    Task<bool> SetQuotaAsync(string userId, long? quotaBytes, CancellationToken ct = default);

    // Removes a never-approved shell: its mappings and its users row. Refuses
    // (false) for anything but a pending account.
    Task<bool> DeletePendingAsync(string userId, CancellationToken ct = default);

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

public sealed record AdminAuditEntry(
    string Id,
    string ActorId,
    string Action,
    string? TargetType,
    string? TargetId,
    DateTime At);
