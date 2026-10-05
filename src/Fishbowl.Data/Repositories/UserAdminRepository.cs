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

    // The instance never loses its last active admin through a state change:
    // the count sits in the UPDATE's own WHERE, so two admins taking each
    // other out at once can't both pass (one statement = one write lock).
    // False when the account is missing or is that last admin. Leaving
    // `active` also drops the account's open OAuth codes — a code issued
    // before a disable/block must not become a key afterwards.
    public async Task<bool> SetStateAsync(string userId, string state, CancellationToken ct = default)
    {
        if (!UserStates.IsKnown(state)) throw new ArgumentException("Unknown account state.", nameof(state));
        using var db = _dbFactory.CreateSystemConnection();
        if (db.State != System.Data.ConnectionState.Open) db.Open();
        using var tx = db.BeginTransaction();
        var affected = await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE users SET state = @state
            WHERE id = @userId
              AND (@state = 'active' OR is_admin = 0 OR state <> 'active'
                   OR (SELECT COUNT(*) FROM users WHERE is_admin = 1 AND state = 'active') > 1)",
            new { userId, state }, tx, cancellationToken: ct));
        if (affected > 0 && state != UserStates.Active)
            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM oauth_codes WHERE user_id = @userId AND used = 0", new { userId }, tx, cancellationToken: ct));
        tx.Commit();
        return affected > 0;
    }

    // An approved, active local-password account in one transaction: the
    // users row (approved by `approverId`, the password to be changed at the
    // first sign-in) and its local mapping. False — and nothing written —
    // when the id or the username is taken. Used by the admin's "Add local
    // user" and by the cold import.
    public async Task<bool> CreateLocalAccountAsync(
        string userId, string? name, string username, string passwordHash, string passwordSalt,
        string approverId, long? quotaBytes, CancellationToken ct = default)
    {
        if (quotaBytes is < 0) throw new ArgumentOutOfRangeException(nameof(quotaBytes));
        using var db = _dbFactory.CreateSystemConnection();
        if (db.State != System.Data.ConnectionState.Open) db.Open();
        using var tx = db.BeginTransaction();
        var now = DateTime.UtcNow.ToString("o");
        var taken = await db.ExecuteScalarAsync<long>(new CommandDefinition(@"
            SELECT (SELECT COUNT(*) FROM users WHERE id = @userId)
                 + (SELECT COUNT(*) FROM user_mappings WHERE provider = 'local' AND provider_id = @username)",
            new { userId, username }, tx, cancellationToken: ct));
        if (taken > 0)
        {
            tx.Rollback();
            return false;
        }
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO users (id, name, email, avatar_url, created_at, state, approved_by, approved_at, quota_bytes,
                               password_hash, password_salt, must_change_password, session_stamp)
            VALUES (@userId, @name, NULL, NULL, @now, 'active', @approverId, @now, @quotaBytes,
                    @passwordHash, @passwordSalt, 1, @stamp)",
            new { userId, name, now, approverId, quotaBytes, passwordHash, passwordSalt, stamp = Ulid.NewUlid().ToString() },
            tx, cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition(
            "INSERT INTO user_mappings (provider, provider_id, user_id) VALUES ('local', @username, @userId)",
            new { username, userId }, tx, cancellationToken: ct));
        tx.Commit();
        return true;
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

    public async Task<bool> SetQuotaAsync(string userId, long? quotaBytes, CancellationToken ct = default)
    {
        if (quotaBytes is < 0) throw new ArgumentOutOfRangeException(nameof(quotaBytes));
        using var db = _dbFactory.CreateSystemConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET quota_bytes = @quotaBytes WHERE id = @userId",
            new { userId, quotaBytes }, cancellationToken: ct));
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

    public async Task<IReadOnlyList<OwnedSpaceRow>> ListSolelyOwnedSpacesAsync(string userId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var rows = await db.QueryAsync<OwnedSpaceRow>(new CommandDefinition(@"
            SELECT s.id AS Id, s.slug AS Slug, s.name AS Name
            FROM spaces s
            JOIN space_members m ON m.space_id = s.id AND m.user_id = @userId AND m.role = 'owner'
            WHERE NOT EXISTS (
                SELECT 1 FROM space_members o
                WHERE o.space_id = s.id AND o.role = 'owner' AND o.user_id <> @userId)
            ORDER BY s.name COLLATE NOCASE", new { userId }, cancellationToken: ct));
        return rows.ToList();
    }

    // Refuses (false) inside the same write transaction when the account is
    // the last active admin or the only owner of a space — the caller's
    // checks ran before a possibly long archive, and a space created or
    // restored meanwhile must not end up without members.
    public async Task<bool> DeleteUserAsync(string userId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        if (db.State != System.Data.ConnectionState.Open) db.Open();
        using var tx = db.BeginTransaction();
        var blocked = await db.ExecuteScalarAsync<long?>(new CommandDefinition(@"
            SELECT CASE
                WHEN u.is_admin = 1 AND u.state = 'active'
                     AND (SELECT COUNT(*) FROM users WHERE is_admin = 1 AND state = 'active') <= 1 THEN 1
                WHEN EXISTS (
                    SELECT 1 FROM space_members m
                    WHERE m.user_id = u.id AND m.role = 'owner'
                      AND NOT EXISTS (SELECT 1 FROM space_members o
                                      WHERE o.space_id = m.space_id AND o.role = 'owner' AND o.user_id <> u.id)) THEN 1
                ELSE 0 END
            FROM users u WHERE u.id = @userId", new { userId }, tx, cancellationToken: ct));
        if (blocked is null or 1)
        {
            tx.Rollback();
            return false;
        }
        foreach (var sql in new[]
        {
            "DELETE FROM api_keys WHERE user_id = @userId OR (owner_type = 'user' AND owner_id = @userId) OR (context_type = 'user' AND context_id = @userId)",
            "DELETE FROM oauth_codes WHERE user_id = @userId",
            "DELETE FROM notification_channels WHERE user_id = @userId",
            "DELETE FROM discord_link_codes WHERE user_id = @userId",
            "DELETE FROM space_members WHERE user_id = @userId",
            "DELETE FROM messages WHERE recipient_id = @userId",
            "DELETE FROM system_config WHERE key IN ('Digest:LastSent:' || @userId, 'Files:QuotaWarned:' || @userId, 'Messages:Muted:' || @userId)",
            "DELETE FROM user_mappings WHERE user_id = @userId",
            "DELETE FROM users WHERE id = @userId",
            "INSERT OR REPLACE INTO deleted_users (id, deleted_at) VALUES (@userId, @now)",
        })
            await db.ExecuteAsync(new CommandDefinition(sql, new { userId, now = DateTime.UtcNow.ToString("o") }, tx, cancellationToken: ct));
        tx.Commit();
        return true;
    }

    // Ignoring case: on Windows (and macOS) users/<ID>/ and users/<id>/ are
    // one folder, so a tombstone covers every spelling of its id.
    public async Task<bool> IsDeletedAsync(string userId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM deleted_users WHERE id = @userId COLLATE NOCASE", new { userId }, cancellationToken: ct)) > 0;
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
