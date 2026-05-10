using System.Security.Claims;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;

namespace Fishbowl.Api.Endpoints;

// Host-level administration. Cookie + IsAdmin gated. Bearer always 403:
// every operation here is wholesale and Bearer tokens are data-scope only.
//
// Today: cold DB import. Operator drops a `users/<incoming-id>/` folder onto
// the data dir (e.g. rsync'd from another machine) and POSTs here to wire
// it into system.db. The DB itself is already the user's data — we just
// register a `users` row and a local-auth mapping so they can sign in.
//
// The folder layout (DatabaseFactory.PersonalDbFileName) is the contract
// here: an importable folder is one named after the would-be userId with a
// personal.db inside.
public static class AdminApi
{
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/admin");

        group.MapGet("/users/importable", async (
            ClaimsPrincipal user,
            ISystemRepository system,
            DatabaseFactory dbFactory,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(user, system, ct))
                return Results.Forbid();

            var usersRoot = dbFactory.UsersRoot;
            if (!Directory.Exists(usersRoot))
                return Results.Ok(Array.Empty<object>());

            var existing = await system.ListUserIdsAsync(ct);
            var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);

            var candidates = new List<object>();
            foreach (var dir in Directory.EnumerateDirectories(usersRoot))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name)) continue;
                if (existingSet.Contains(name)) continue;

                var dbPath = Path.Combine(dir, DatabaseFactory.PersonalDbFileName);
                if (!File.Exists(dbPath)) continue;

                var size = new FileInfo(dbPath).Length;
                candidates.Add(new { folderName = name, dbBytes = size });
            }

            return Results.Ok(candidates);
        })
        .WithName("ListImportableUsers")
        .WithSummary("Lists user folders on disk that aren't yet registered in system.db.")
        .RequireAuthorization();

        group.MapPost("/users/import", async (
            ImportUserRequest request,
            ClaimsPrincipal user,
            ISystemRepository system,
            DatabaseFactory dbFactory,
            IPasswordHasher hasher,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(user, system, ct))
                return Results.Forbid();

            // Strict path-component validation. Reject anything that could
            // walk outside the users root — no separators, no traversal,
            // no nulls. Folder names are typically GUIDs or local usernames.
            var folder = request?.FolderName?.Trim() ?? string.Empty;
            if (!IsSafePathComponent(folder))
                return Results.BadRequest(new { error = "folderName must be a single path component (no '/', '\\', '..')." });

            var existing = await system.GetUserAsync(folder, ct);
            if (existing is not null)
                return Results.Conflict(new { error = "A user with this id is already registered." });

            var userFolder = Path.Combine(dbFactory.UsersRoot, folder);
            var dbPath = Path.Combine(userFolder, DatabaseFactory.PersonalDbFileName);
            if (!Directory.Exists(userFolder) || !File.Exists(dbPath))
                return Results.NotFound(new { error = "No personal.db found at users/" + folder + "/." });

            // Sanity-open the SQLite to make sure it's not corrupt before we
            // commit a system.db row pointing at it.
            try
            {
                using var probe = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
                probe.Open();
                var tableCount = probe.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'notes'");
                if (tableCount == 0)
                {
                    return Results.BadRequest(new
                    {
                        error = "Folder doesn't look like a Fishbowl personal DB — no `notes` table."
                    });
                }
            }
            catch (SqliteException)
            {
                return Results.BadRequest(new { error = "personal.db is unreadable or corrupt." });
            }

            // Validate the local credentials before we write anything to
            // system.db — same rules as setup.
            var username = request!.Username?.Trim().ToLowerInvariant() ?? string.Empty;
            if (username.Length < 3 || username.Length > 64)
                return Results.BadRequest(new { error = "Username must be 3–64 characters." });
            foreach (var ch in username)
            {
                if (!(char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-'))
                    return Results.BadRequest(new { error = "Username may only contain letters, digits, _, . or -." });
            }
            if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 12)
                return Results.BadRequest(new { error = "Password must be at least 12 characters." });

            var existingByUsername = await system.GetUserByLocalUsernameAsync(username, ct);
            if (existingByUsername is not null)
                return Results.Conflict(new { error = "Username is already taken." });

            // Belt + braces: opening the context connection through the
            // factory triggers any pending schema migration on the imported
            // DB. The operator may have moved a file from an older Fishbowl
            // version; better to roll it forward now than hit a half-migrated
            // state on first user login.
            using (var _ = dbFactory.CreateContextConnection(ContextRef.User(folder)))
            { /* opened-and-migrated, drop the connection */ }

            await system.CreateUserAsync(folder, name: request.DisplayName ?? username, email: null, avatarUrl: null, ct);
            await system.CreateUserMappingAsync(folder, "local", username, ct);
            var hash = hasher.Hash(request.Password);
            await system.SetPasswordAsync(folder, hash.Hash, hash.Salt, ct: ct);

            return Results.Ok(new
            {
                userId = folder,
                username,
                message = "User imported. They can sign in with /api/auth/login."
            });
        })
        .WithName("ImportUser")
        .WithSummary("Registers a pre-existing users/{folderName}/ data folder as a new user with a local password.")
        .RequireAuthorization();

        // Admin password reset → temp password the operator passes out-of-band
        // (chat / paper). User must rotate on next login. Returns the new
        // password as plaintext exactly once; we never email it (CONCEPT.md
        // self-host-without-internet means we can't assume an SMTP server).
        group.MapPost("/users/{userId}/reset-password", async (
            string userId,
            ClaimsPrincipal caller,
            ISystemRepository system,
            IPasswordHasher hasher,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(caller, system, ct))
                return Results.Forbid();

            var target = await system.GetUserAsync(userId, ct);
            if (target is null)
                return Results.NotFound(new { error = "No such user." });

            // Refuse on OAuth-only users — there's nowhere to put a local
            // password, and we shouldn't quietly bolt one on (the human
            // expects "sign in with Google" and a password field appearing
            // on /login would be confusing). Operator should run /import or
            // a future "add local password" flow if they really want both.
            if (string.IsNullOrEmpty(target.PasswordHash))
                return Results.BadRequest(new
                {
                    error = "User has no local-auth mapping. Reset only applies to local-password users."
                });

            var tempPassword = GenerateTempPassword();
            var hash = hasher.Hash(tempPassword);
            await system.SetPasswordAsync(userId, hash.Hash, hash.Salt, mustChange: true, ct);

            return Results.Ok(new
            {
                userId,
                tempPassword,
                mustChangeOnNextLogin = true,
                message = "Share this password with the user out-of-band. They must change it on next sign-in.",
            });
        })
        .WithName("AdminResetPassword")
        .WithSummary("Generates a temp password for a local user and forces a change on next login.")
        .RequireAuthorization();

        return routes;
    }

    // 12 chars from a 60-symbol alphabet (lowercase + digits + hand-picked
    // upper). Excludes look-alikes (I/l/0/O/1) so the operator can read it
    // off a screen without confusion. ~70 bits of entropy — fine for a
    // 10-minute, single-use credential the user immediately rotates.
    private static string GenerateTempPassword()
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[12];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }

    private static async Task<bool> IsCookieAdminAsync(
        ClaimsPrincipal user, ISystemRepository system, CancellationToken ct)
    {
        if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return false;
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (string.IsNullOrEmpty(userId)) return false;
        var profile = await system.GetUserAsync(userId, ct);
        return profile?.IsAdmin == true;
    }

    private static bool IsSafePathComponent(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value is "." or "..") return false;
        foreach (var ch in value)
        {
            if (ch is '/' or '\\' or '\0') return false;
            if (Path.GetInvalidFileNameChars().Contains(ch)) return false;
        }
        return true;
    }
}

public sealed record ImportUserRequest(
    string FolderName,
    string Username,
    string Password,
    string? DisplayName = null);
