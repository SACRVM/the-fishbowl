using System.Data;
using Dapper;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class SystemRepository : ISystemRepository
{
    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<SystemRepository> _logger;

    public SystemRepository(DatabaseFactory dbFactory, ILogger<SystemRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<SystemRepository>.Instance;
    }

    public async Task<string?> GetUserIdByMappingAsync(string provider, string providerId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition("SELECT user_id FROM user_mappings WHERE provider = @provider AND provider_id = @providerId",
            new { provider, providerId }, cancellationToken: ct));
    }

    public async Task<bool> CreateUserMappingAsync(string userId, string provider, string providerId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var affected = await db.ExecuteAsync(
            new CommandDefinition("INSERT INTO user_mappings (provider, provider_id, user_id) VALUES (@provider, @providerId, @userId)",
            new { provider, providerId, userId }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> CreateUserAsync(string userId, string? name, string? email, string? avatarUrl, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var affected = await db.ExecuteAsync(
            new CommandDefinition("INSERT INTO users (id, name, email, avatar_url, created_at) VALUES (@userId, @name, @email, @avatarUrl, @createdAt)",
            new { userId, name, email, avatarUrl, createdAt = DateTime.UtcNow.ToString("o") }, cancellationToken: ct));

        if (affected > 0)
        {
            _logger.LogInformation("Provisioned user {UserId}", userId);
        }

        return affected > 0;
    }

    public async Task<User?> GetUserAsync(string userId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.QuerySingleOrDefaultAsync<User>(
            new CommandDefinition(
                @"SELECT id AS Id, name AS Name, email AS Email, avatar_url AS AvatarUrl,
                         created_at AS CreatedAt, password_hash AS PasswordHash,
                         password_salt AS PasswordSalt, is_admin AS IsAdmin,
                         must_change_password AS MustChangePassword
                  FROM users WHERE id = @userId",
                new { userId }, cancellationToken: ct));
    }

    // Local username lookup. Username is stored lowercased in user_mappings
    // (provider="local") so case doesn't fork "Alice" vs "alice".
    public async Task<User?> GetUserByLocalUsernameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        using var db = _dbFactory.CreateSystemConnection();
        var normalised = username.Trim().ToLowerInvariant();
        return await db.QuerySingleOrDefaultAsync<User>(
            new CommandDefinition(
                @"SELECT u.id AS Id, u.name AS Name, u.email AS Email, u.avatar_url AS AvatarUrl,
                         u.created_at AS CreatedAt, u.password_hash AS PasswordHash,
                         u.password_salt AS PasswordSalt, u.is_admin AS IsAdmin,
                         u.must_change_password AS MustChangePassword
                  FROM users u
                  INNER JOIN user_mappings m
                      ON m.user_id = u.id AND m.provider = 'local' AND m.provider_id = @username",
                new { username = normalised }, cancellationToken: ct));
    }

    // Stores the new hash/salt and (by default) clears the must-change flag —
    // a fresh password is implicitly the user's own choice. Admin-reset
    // explicitly sets `mustChange = true` to flip the flag back on.
    public async Task<bool> SetPasswordAsync(
        string userId, string passwordHash, string passwordSalt,
        bool mustChange = false, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var affected = await db.ExecuteAsync(
            new CommandDefinition(
                @"UPDATE users
                  SET password_hash = @hash, password_salt = @salt, must_change_password = @flag
                  WHERE id = @userId",
                new { userId, hash = passwordHash, salt = passwordSalt, flag = mustChange ? 1 : 0 },
                cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> SetAdminAsync(string userId, bool isAdmin, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var affected = await db.ExecuteAsync(
            new CommandDefinition(
                "UPDATE users SET is_admin = @flag WHERE id = @userId",
                new { userId, flag = isAdmin ? 1 : 0 },
                cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> HasLocalUserAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var count = await db.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM user_mappings WHERE provider = 'local'",
                cancellationToken: ct));
        return count > 0;
    }

    public async Task<IReadOnlyList<string>> ListUserIdsAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var ids = await db.QueryAsync<string>(
            new CommandDefinition("SELECT id FROM users", cancellationToken: ct));
        return ids.ToList();
    }

    public async Task<bool> UpsertUserAsync(string userId, string? name, string? email, string? avatarUrl, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO users (id, name, email, avatar_url, created_at)
            VALUES (@userId, @name, @email, @avatarUrl, @createdAt)
            ON CONFLICT(id) DO UPDATE SET
                name       = excluded.name,
                email      = excluded.email,
                avatar_url = excluded.avatar_url
            WHERE
                IFNULL(users.name, '')       != IFNULL(excluded.name, '') OR
                IFNULL(users.email, '')      != IFNULL(excluded.email, '') OR
                IFNULL(users.avatar_url, '') != IFNULL(excluded.avatar_url, '')",
            new { userId, name, email, avatarUrl, createdAt = DateTime.UtcNow.ToString("o") },
            cancellationToken: ct));

        if (affected > 0)
        {
            _logger.LogInformation("User {UserId} profile upserted", userId);
        }

        return affected > 0;
    }

    public async Task<string?> GetConfigAsync(string key, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.ExecuteScalarAsync<string>(
            new CommandDefinition("SELECT value FROM system_config WHERE key = @key", new { key }, cancellationToken: ct));
    }

    public async Task<bool> SetConfigAsync(string key, string value, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var affected = await db.ExecuteAsync(
            new CommandDefinition(@"
                INSERT INTO system_config (key, value, updated_at)
                VALUES (@key, @value, @updatedAt)
                ON CONFLICT(key) DO UPDATE SET value = @value, updated_at = @updatedAt",
            new { key, value, updatedAt = DateTime.UtcNow.ToString("o") }, cancellationToken: ct));

        if (affected > 0)
        {
            _logger.LogDebug("Config key {Key} updated", key);
        }

        return affected > 0;
    }
}
