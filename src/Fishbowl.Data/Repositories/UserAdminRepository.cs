using Dapper;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Data.Repositories;

// Account administration over system.db. Reads profile metadata and sign-in
// methods only; it never opens a context DB.
public class UserAdminRepository : IUserAdminRepository
{
    private readonly DatabaseFactory _dbFactory;

    public UserAdminRepository(DatabaseFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    private sealed class UserRow
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public string? Email { get; set; }
        public bool IsAdmin { get; set; }
        public string State { get; set; } = UserStates.Active;
        public long? QuotaBytes { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastSignInAt { get; set; }
        public DateTime? ApprovedAt { get; set; }
    }

    private sealed class MappingRow
    {
        public string UserId { get; set; } = "";
        public string Provider { get; set; } = "";
    }

    public async Task<IReadOnlyList<AdminUserRow>> ListUsersAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var users = (await db.QueryAsync<UserRow>(new CommandDefinition(@"
            SELECT id, name, email, is_admin, state, quota_bytes, created_at, last_sign_in_at, approved_at
            FROM users ORDER BY created_at", cancellationToken: ct))).ToList();
        var mappings = await db.QueryAsync<MappingRow>(new CommandDefinition(
            "SELECT user_id, provider FROM user_mappings", cancellationToken: ct));
        var byUser = mappings.GroupBy(m => m.UserId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<string>)g.Select(m => m.Provider).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList());
        return users.Select(u => new AdminUserRow(
            u.Id, u.Name, u.Email,
            byUser.TryGetValue(u.Id, out var p) ? p : Array.Empty<string>(),
            u.IsAdmin, u.State, u.QuotaBytes, u.CreatedAt, u.LastSignInAt, u.ApprovedAt)).ToList();
    }

    public async Task<IReadOnlyList<string>> ListAdminIdsAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var ids = await db.QueryAsync<string>(new CommandDefinition(
            "SELECT id FROM users WHERE is_admin = 1 AND state = 'active'", cancellationToken: ct));
        return ids.ToList();
    }

    public async Task<int> CountActiveAdminsAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM users WHERE is_admin = 1 AND state = 'active'", cancellationToken: ct));
    }

    public async Task<bool> SetStateAsync(string userId, string state, CancellationToken ct = default)
    {
        if (!UserStates.IsKnown(state)) throw new ArgumentException("Unknown account state.", nameof(state));
        using var db = _dbFactory.CreateSystemConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET state = @state WHERE id = @userId", new { userId, state }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> ApproveAsync(string userId, string approverId, long? quotaBytes, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE users
            SET state = 'active', approved_by = @approverId, approved_at = @now, quota_bytes = @quotaBytes
            WHERE id = @userId AND state = 'pending'",
            new { userId, approverId, quotaBytes, now = DateTime.UtcNow.ToString("o") }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> TouchSignInAsync(string userId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET last_sign_in_at = @now WHERE id = @userId",
            new { userId, now = DateTime.UtcNow.ToString("o") }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> DeletePendingAsync(string userId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        if (db.State != System.Data.ConnectionState.Open) db.Open();
        using var tx = db.BeginTransaction();
        var state = await db.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state FROM users WHERE id = @userId", new { userId }, tx, cancellationToken: ct));
        if (state != UserStates.Pending)
        {
            tx.Rollback();
            return false;
        }
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_mappings WHERE user_id = @userId", new { userId }, tx, cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM users WHERE id = @userId", new { userId }, tx, cancellationToken: ct));
        tx.Commit();
        return true;
    }

    public async Task RecordAdminActionAsync(string actorId, string action, string? targetType, string? targetId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO admin_audit (id, actor_id, action, target_type, target_id, at)
            VALUES (@id, @actorId, @action, @targetType, @targetId, @at)",
            new { id = Ulid.NewUlid().ToString(), actorId, action, targetType, targetId, at = DateTime.UtcNow.ToString("o") },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AdminAuditEntry>> ListAuditAsync(int limit = 200, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var rows = await db.QueryAsync<AuditRow>(new CommandDefinition(@"
            SELECT id, actor_id, action, target_type, target_id, at
            FROM admin_audit ORDER BY at DESC, id DESC LIMIT @limit",
            new { limit = Math.Clamp(limit, 1, 1000) }, cancellationToken: ct));
        return rows.Select(r => new AdminAuditEntry(r.Id, r.ActorId, r.Action, r.TargetType, r.TargetId, r.At)).ToList();
    }

    private sealed class AuditRow
    {
        public string Id { get; set; } = "";
        public string ActorId { get; set; } = "";
        public string Action { get; set; } = "";
        public string? TargetType { get; set; }
        public string? TargetId { get; set; }
        public DateTime At { get; set; }
    }
}
