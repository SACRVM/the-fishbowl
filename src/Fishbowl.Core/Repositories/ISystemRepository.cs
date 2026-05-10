using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

public interface ISystemRepository
{
    // User Mapping
    Task<string?> GetUserIdByMappingAsync(string provider, string providerId, CancellationToken ct = default);
    Task<bool> CreateUserMappingAsync(string userId, string provider, string providerId, CancellationToken ct = default);

    // User Profile
    Task<bool> CreateUserAsync(string userId, string? name, string? email, string? avatarUrl, CancellationToken ct = default);

    /// <summary>
    /// Insert-or-update the user's profile snapshot. Called on every successful
    /// login so name/email/avatar stay in sync with what the provider sends.
    /// Returns true on insert or when at least one field changed.
    /// </summary>
    Task<bool> UpsertUserAsync(string userId, string? name, string? email, string? avatarUrl, CancellationToken ct = default);

    Task<User?> GetUserAsync(string userId, CancellationToken ct = default);

    // Local-auth helpers. Only used by /api/auth/login + setup. OAuth users
    // never see these — their password fields stay null.
    Task<User?> GetUserByLocalUsernameAsync(string username, CancellationToken ct = default);
    // `mustChange = true` flips the next-login force-rotate flag (admin
    // reset path). Default false — a user changing their own password
    // doesn't need to change it again.
    Task<bool> SetPasswordAsync(
        string userId, string passwordHash, string passwordSalt,
        bool mustChange = false, CancellationToken ct = default);
    Task<bool> SetAdminAsync(string userId, bool isAdmin, CancellationToken ct = default);

    // True if at least one local-auth user exists. The setup wizard uses
    // this (combined with Google:ClientId) to decide whether the wizard
    // is locked out — operators must finish setup once *any* provider is
    // wired up, not only Google. Cheap: it's a COUNT on user_mappings.
    Task<bool> HasLocalUserAsync(CancellationToken ct = default);

    // Every registered user id. Cheap (system.db's `users` table is small)
    // and used by admin endpoints that need to diff disk folders against
    // the registry to find unimported data.
    Task<IReadOnlyList<string>> ListUserIdsAsync(CancellationToken ct = default);

    // Configuration
    Task<string?> GetConfigAsync(string key, CancellationToken ct = default);
    Task<bool> SetConfigAsync(string key, string value, CancellationToken ct = default);
}
