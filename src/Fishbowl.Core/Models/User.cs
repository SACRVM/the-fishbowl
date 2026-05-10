using System;

namespace Fishbowl.Core.Models;

/// <summary>
/// A Fishbowl user — internal identity (GUID) plus the most recent profile
/// snapshot from the auth provider. Refreshed on every successful login via
/// <c>ISystemRepository.UpsertUserAsync</c>.
///
/// Password fields are populated only for local auth users; OAuth-only users
/// (e.g. Google) have null hash + salt. <see cref="IsAdmin"/> gates access to
/// host-level admin endpoints (cold DB import, future user management).
/// </summary>
public class User
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
    public bool IsAdmin { get; set; }

    // True after admin-reset; cleared by a successful change-password.
    // Login still verifies the temp hash, but the auth API refuses to
    // issue a cookie until the user has rotated to one of their own
    // choosing.
    public bool MustChangePassword { get; set; }
}
