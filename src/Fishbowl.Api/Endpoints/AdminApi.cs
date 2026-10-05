using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Files;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Plugins;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Fishbowl.Data;
using Fishbowl.Data.Files;
using Fishbowl.Data.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

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
            IUserAdminRepository admin,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(user, system, ct))
                return Results.Forbid();

            var usersRoot = dbFactory.UsersRoot;
            if (!Directory.Exists(usersRoot))
                return Results.Ok(Array.Empty<object>());

            // Ignoring case, like the file system on Windows: a folder whose
            // name differs from a registered id only by case is that user's.
            var existing = await system.ListUserIdsAsync(ct);
            var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

            var candidates = new List<object>();
            foreach (var dir in Directory.EnumerateDirectories(usersRoot))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;   // parked .deleted-/.restoring- folders
                if (existingSet.Contains(name)) continue;
                if (await admin.IsDeletedAsync(name, ct)) continue;

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
            IUserAdminRepository admin,
            UserAdminRepository accounts,
            IMessageRepository messages,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(user, system, ct))
                return Results.Forbid();

            // Strict path-component validation. Reject anything that could
            // walk outside the users root — no separators, no traversal,
            // no nulls. Folder names are typically GUIDs or local usernames.
            // A tombstone covers its id in any case (IsDeletedAsync).
            var folder = request?.FolderName?.Trim() ?? string.Empty;
            if (!IsSafePathComponent(folder) || folder.StartsWith('.') || await admin.IsDeletedAsync(folder, ct))
                return ApiErrors.BadRequest("invalid_folder_name", "folderName must be a single path component (no '/', '\\', '..').");

            // Windows (and macOS) match names ignoring case and drop trailing
            // dots: users/ABCD/ or users/abcd./ would open a registered
            // user's folder. A registered id in any case is that user.
            var existing = await system.ListUserIdsAsync(ct);
            if (existing.Any(id => string.Equals(id, folder, StringComparison.OrdinalIgnoreCase)))
                return ApiErrors.Conflict("user_exists", "A user with this id is already registered.");

            // Only a folder that is really there under exactly this name —
            // never one the file system would find under another spelling.
            var entry = Directory.Exists(dbFactory.UsersRoot)
                ? Directory.EnumerateDirectories(dbFactory.UsersRoot).Select(Path.GetFileName)
                    .FirstOrDefault(n => string.Equals(n, folder, StringComparison.Ordinal))
                : null;
            var userFolder = Path.Combine(dbFactory.UsersRoot, folder);
            var dbPath = Path.Combine(userFolder, DatabaseFactory.PersonalDbFileName);
            if (entry is null || !File.Exists(dbPath))
                return ApiErrors.NotFound("no_personal_db", "No personal.db found at users/" + folder + "/.", new { folder });

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
                    return ApiErrors.BadRequest("not_a_personal_db", "Folder doesn't look like a Fishbowl personal DB — no `notes` table.");
                }
            }
            catch (SqliteException)
            {
                return ApiErrors.BadRequest("db_unreadable", "personal.db is unreadable or corrupt.");
            }

            // Validate the local credentials before we write anything to
            // system.db — same rules as setup.
            var username = request!.Username?.Trim().ToLowerInvariant() ?? string.Empty;
            var usernameError = ValidateUsername(username);
            if (usernameError is not null) return ApiErrors.BadRequest("invalid_username", usernameError, new { reason = usernameError });
            if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 12)
                return ApiErrors.BadRequest("min_length", "Password must be at least 12 characters.", new { field = "password", min = 12 });

            var existingByUsername = await system.GetUserByLocalUsernameAsync(username, ct);
            if (existingByUsername is not null)
                return ApiErrors.Conflict("username_taken", "Username is already taken.");

            // Belt + braces: opening the context connection through the
            // factory triggers any pending schema migration on the imported
            // DB. The operator may have moved a file from an older Fishbowl
            // version; better to roll it forward now than hit a half-migrated
            // state on first user login.
            using (var _ = dbFactory.CreateContextConnection(ContextRef.User(folder)))
            { /* opened-and-migrated, drop the connection */ }

            // Imported by an admin = approved by that admin (a later unblock
            // restores it to active), and the password the admin typed is
            // only a way in: it must be changed at the first sign-in.
            var hash = hasher.Hash(request.Password);
            if (!await accounts.CreateLocalAccountAsync(folder, request.DisplayName ?? username, username,
                    hash.Hash, hash.Salt, ActorId(user), quotaBytes: null, ct))
            {
                return await system.GetUserByLocalUsernameAsync(username, ct) is not null
                    ? ApiErrors.Conflict("username_taken", "Username is already taken.")
                    : ApiErrors.Conflict("user_exists", "A user with this id is already registered.");
            }
            // The account's owner learns that an admin gave it a password.
            await messages.CreateAsync(new[] { folder }, MessageKinds.PasswordReset, "user", folder, "{\"via\":\"import\"}", ct);
            await admin.RecordAdminActionAsync(ActorId(user), AdminActions.ImportUser, "user", folder, ct);

            return Results.Ok(new
            {
                userId = folder,
                username,
                mustChangeOnNextLogin = true,
                message = "User imported. They sign in with /api/auth/login and choose their own password."
            });
        })
        .WithName("ImportUser")
        .WithSummary("Registers a pre-existing users/{folderName}/ data folder as a new user with a local password.")
        .RequireAuthorization();

        // Admin password reset → a temporary password the user must replace at
        // the next sign-in. A Global Admin never reads content, so the reset
        // is made visible rather than silent: with a linked chat channel the
        // temporary password goes straight to the user (the admin never sees
        // it); otherwise the admin gets it once to pass on. Either way the
        // user gets a password.reset message, and whoever then signs in with
        // it triggers password.reset-used.
        group.MapPost("/users/{userId}/reset-password", async (
            string userId,
            ClaimsPrincipal caller,
            ISystemRepository system,
            IPasswordHasher hasher,
            IUserAdminRepository admin,
            IMessageRepository messages,
            INotificationChannelRepository channels,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(caller, system, ct))
                return Results.Forbid();

            var target = await system.GetUserAsync(userId, ct);
            if (target is null)
                return ApiErrors.NotFound("no_such_user", "No such user.");

            // Refuse on OAuth-only users — there's nowhere to put a local
            // password, and we shouldn't quietly bolt one on (the human
            // expects "sign in with Google" and a password field appearing
            // on /login would be confusing). Operator should run /import or
            // a future "add local password" flow if they really want both.
            if (string.IsNullOrEmpty(target.PasswordHash))
                return ApiErrors.BadRequest("no_local_login", "User has no local-auth mapping. Reset only applies to local-password users.");

            var tempPassword = GenerateTempPassword();
            var hash = hasher.Hash(tempPassword);
            await system.SetPasswordAsync(userId, hash.Hash, hash.Salt, mustChange: true, ct);
            await admin.RecordAdminActionAsync(ActorId(caller), AdminActions.ResetPassword, "user", userId, ct);
            await messages.CreateAsync(new[] { userId }, MessageKinds.PasswordReset, "user", userId, "{\"via\":\"reset\"}", ct);

            // Straight to the user when they have a chat channel.
            foreach (var bot in http.RequestServices.GetServices<IBotClient>())
            {
                var channel = await channels.GetAsync(userId, bot.Name, ct);
                if (channel is null || !channel.Enabled) continue;
                try
                {
                    await bot.SendAsync(userId, ChatText.Get("password.resetCode", target.Language, tempPassword), ct);
                    return Results.Ok(new
                    {
                        userId,
                        deliveredTo = bot.Name,
                        mustChangeOnNextLogin = true,
                        message = "The temporary password went to the user's " + bot.Name + " DM — you don't see it.",
                    });
                }
                catch (Exception) { /* not delivered: the admin passes it on */ }
            }

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

        MapUserManagement(group);

        return routes;
    }

    // The Users app: every account with its metadata, never its content, plus
    // approve / reject / block / unblock. Each write is one admin_audit row
    // and resolves the user.pending messages about that account for every
    // admin at once.
    private static void MapUserManagement(RouteGroupBuilder group)
    {
        group.MapGet("/users", async (
            ClaimsPrincipal caller,
            ISystemRepository system,
            IUserAdminRepository admin,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(caller, system, ct)) return Results.Forbid();
            var me = ActorId(caller);
            var rows = await admin.ListUsersAsync(ct);
            var users = rows
                .OrderBy(u => u.State == UserStates.Pending ? 0 : 1)
                .ThenBy(u => u.State == UserStates.Pending ? u.CreatedAt : DateTime.MinValue)
                .ThenBy(u => u.Name ?? u.Email ?? u.Id, StringComparer.OrdinalIgnoreCase)
                .Select(u => new
                {
                    id = u.Id,
                    name = u.Name,
                    email = u.Email,
                    providers = u.Providers,
                    isAdmin = u.IsAdmin,
                    state = u.State,
                    quotaBytes = u.QuotaBytes,
                    createdAt = u.CreatedAt,
                    lastSignInAt = u.LastSignInAt,
                    approvedAt = u.ApprovedAt,
                    self = u.Id == me,
                });
            return Results.Ok(new
            {
                signUp = SignUpPolicy.ParseMode(await system.GetConfigAsync(SignUpPolicy.ModeKey, ct)),
                defaultQuotaBytes = await DefaultQuotaAsync(system, ct),
                users,
            });
        })
        .WithName("ListAdminUsers")
        .WithSummary("Every account's metadata: name, e-mail, sign-in methods, state, quota, dates. Never content.")
        .RequireAuthorization();

        group.MapPost("/users/{userId}/approve", async (
            string userId,
            HttpRequest request,
            ClaimsPrincipal caller,
            ISystemRepository system,
            IUserAdminRepository admin,
            IMessageRepository messages,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(caller, system, ct)) return Results.Forbid();

            // Optional body { quotaBytes?: number|null, makeAdmin?: bool },
            // read by hand so a bare POST works too.
            long? quota = null;
            var makeAdmin = false;
            if (request.HasJsonContentType())
            {
                ApproveUserRequest? body;
                try { body = await request.ReadFromJsonAsync<ApproveUserRequest>(ct); }
                catch (System.Text.Json.JsonException) { return ApiErrors.BadRequest("invalid_body", "Body must be JSON: { quotaBytes?, makeAdmin? }."); }
                catch (InvalidOperationException) { return ApiErrors.BadRequest("invalid_body", "Body must be JSON: { quotaBytes?, makeAdmin? }."); }
                if (body?.QuotaBytes is < 0) return ApiErrors.BadRequest("invalid_value", "quotaBytes must be 0 (unlimited) or more.", new { field = "quotaBytes" });
                quota = body?.QuotaBytes;
                makeAdmin = body?.MakeAdmin == true;
            }

            var target = await system.GetUserAsync(userId, ct);
            if (target is null) return ApiErrors.NotFound("no_such_user", "No such user.");
            if (target.State != UserStates.Pending)
                return Results.Conflict(new { error = "not-pending", state = target.State });

            if (!await admin.ApproveAsync(userId, ActorId(caller), quota, ct))
                return Results.Conflict(new { error = "not-pending" });
            if (makeAdmin) await system.SetAdminAsync(userId, true, ct);

            await messages.ResolveAsync(MessageKinds.UserPending, "user", userId, ct);
            await messages.CreateAsync(new[] { userId }, MessageKinds.UserApproved, "user", userId, null, ct);
            await admin.RecordAdminActionAsync(ActorId(caller), AdminActions.Approve, "user", userId, ct);
            if (makeAdmin) await admin.RecordAdminActionAsync(ActorId(caller), AdminActions.MakeAdmin, "user", userId, ct);

            return Results.Ok(new { id = userId, state = UserStates.Active, quotaBytes = quota, isAdmin = makeAdmin });
        })
        .WithName("ApproveUser")
        .WithSummary("Activates a pending account, optionally with a quota and as an admin.")
        .RequireAuthorization();

        group.MapPost("/users/{userId}/reject", async (
            string userId,
            ClaimsPrincipal caller,
            ISystemRepository system,
            IUserAdminRepository admin,
            IMessageRepository messages,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(caller, system, ct)) return Results.Forbid();
            var target = await system.GetUserAsync(userId, ct);
            if (target is null) return ApiErrors.NotFound("no_such_user", "No such user.");
            if (target.State != UserStates.Pending)
                return Results.Conflict(new { error = "not-pending", state = target.State });

            // The shell goes: row + mappings. A later sign-in asks again.
            if (!await admin.DeletePendingAsync(userId, ct))
                return Results.Conflict(new { error = "not-pending" });
            await messages.ResolveAsync(MessageKinds.UserPending, "user", userId, ct);
            await messages.DeleteForRecipientAsync(userId, ct);
            await admin.RecordAdminActionAsync(ActorId(caller), AdminActions.Reject, "user", userId, ct);
            return Results.NoContent();
        })
        .WithName("RejectUser")
        .WithSummary("Deletes a pending account (row + sign-in mappings). The identity may ask again.")
        .RequireAuthorization();

        group.MapPost("/users/{userId}/block", async (
            string userId,
            ClaimsPrincipal caller,
            ISystemRepository system,
            IUserAdminRepository admin,
            IMessageRepository messages,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(caller, system, ct)) return Results.Forbid();
            if (userId == ActorId(caller))
                return ApiErrors.BadRequest("self_block", "You can't block yourself.");
            var target = await system.GetUserAsync(userId, ct);
            if (target is null) return ApiErrors.NotFound("no_such_user", "No such user.");
            if (target.State == UserStates.Blocked) return Results.NoContent();
            if (target.IsAdmin && target.State == UserStates.Active && await admin.CountActiveAdminsAsync(ct) <= 1)
                return Results.Conflict(new { error = "last-admin" });

            // Kept as a row, so the same identity can't ask again; the request
            // gate ends a live session at its next request. The write itself
            // refuses the last active admin (two admins blocking each other).
            if (!await admin.SetStateAsync(userId, UserStates.Blocked, ct))
                return await system.GetUserAsync(userId, ct) is null
                    ? ApiErrors.NotFound("no_such_user", "No such user.")
                    : Results.Conflict(new { error = "last-admin" });
            await messages.ResolveAsync(MessageKinds.UserPending, "user", userId, ct);
            await admin.RecordAdminActionAsync(ActorId(caller), AdminActions.Block, "user", userId, ct);
            return Results.NoContent();
        })
        .WithName("BlockUser")
        .WithSummary("Blocks an account: no sign-in, a live session ends at its next request, the identity can't ask again.")
        .RequireAuthorization();

        group.MapPost("/users/{userId}/unblock", async (
            string userId,
            ClaimsPrincipal caller,
            ISystemRepository system,
            IUserAdminRepository admin,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(caller, system, ct)) return Results.Forbid();
            var target = await system.GetUserAsync(userId, ct);
            if (target is null) return ApiErrors.NotFound("no_such_user", "No such user.");
            if (target.State != UserStates.Blocked)
                return Results.Conflict(new { error = "not-blocked", state = target.State });

            // Never a way around approval: an account without an approval
            // stamp goes back to pending (the admin approves it next), one
            // that was approved becomes active again. Admins are always
            // restored — they were active to become admins.
            var next = target.ApprovedAt is null && !target.IsAdmin ? UserStates.Pending : UserStates.Active;
            await admin.SetStateAsync(userId, next, ct);
            await admin.RecordAdminActionAsync(ActorId(caller), AdminActions.Unblock, "user", userId, ct);
            return Results.Ok(new { id = userId, state = next });
        })
        .WithName("UnblockUser")
        .WithSummary("Undoes a block: an approved account becomes active, anything else goes back to pending.")
        .RequireAuthorization();

        MapManage(group);
        MapLifecycle(group);
    }

    // A2 — managing accounts that exist: add a local user, change the quota,
    // make or remove an admin, disable / enable. Every change is one audit
    // row per field; responses carry metadata only.
    private static void MapManage(RouteGroupBuilder group)
    {
        group.MapPost("/users", async (
            HttpRequest request,
            ClaimsPrincipal caller,
            ISystemRepository system,
            IUserAdminRepository admin,
            UserAdminRepository accounts,
            IPasswordHasher hasher,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(caller, system, ct)) return Results.Forbid();

            CreateLocalUserRequest? body;
            try { body = await request.ReadFromJsonAsync<CreateLocalUserRequest>(ct); }
            catch (JsonException) { body = null; }
            catch (InvalidOperationException) { body = null; }
            if (body is null) return ApiErrors.BadRequest("invalid_body", "Body must be JSON: { username, displayName?, quotaBytes? }.");

            var username = body.Username?.Trim().ToLowerInvariant() ?? string.Empty;
            var usernameError = ValidateUsername(username);
            if (usernameError is not null) return ApiErrors.BadRequest("invalid_username", usernameError, new { reason = usernameError });
            if (body.QuotaBytes is < 0) return ApiErrors.BadRequest("invalid_value", "quotaBytes must be 0 (unlimited) or more.", new { field = "quotaBytes" });
            var displayName = string.IsNullOrWhiteSpace(body.DisplayName) ? username : body.DisplayName.Trim();
            if (displayName.Length > 100) return ApiErrors.BadRequest("max_length", "Display name must be 100 characters or fewer.", new { field = "displayName", max = 100 });
            if (await system.GetUserByLocalUsernameAsync(username, ct) is not null)
                return ApiErrors.Conflict("username_taken", "Username is already taken.");

            // Created by an admin = approved by that admin: approved_by /
            // approved_at say who, and a later unblock restores it to active,
            // not to pending. One transaction — the same username asked for
            // twice at once leaves one account and a 409, nothing else.
            var userId = Guid.NewGuid().ToString();
            var tempPassword = GenerateTempPassword();
            var hash = hasher.Hash(tempPassword);
            if (!await accounts.CreateLocalAccountAsync(userId, displayName, username, hash.Hash, hash.Salt,
                    ActorId(caller), body.QuotaBytes, ct))
                return ApiErrors.Conflict("username_taken", "Username is already taken.");
            await admin.RecordAdminActionAsync(ActorId(caller), AdminActions.CreateLocal, "user", userId, ct);

            return Results.Ok(new
            {
                id = userId,
                username,
                tempPassword,
                mustChangeOnNextLogin = true,
                state = UserStates.Active,
                quotaBytes = body.QuotaBytes,
            });
        })
        .WithName("CreateLocalUser")
        .WithSummary("Adds an active local-password account; returns its temporary password exactly once.")
        .RequireAuthorization();

        // Partial: { quotaBytes?: number|null, isAdmin?: bool, disabled?: bool }.
        // Absent fields stay as they are; quotaBytes null = the default. The
        // whole patch is validated before anything is written.
        group.MapPatch("/users/{userId}", async (
            string userId,
            HttpRequest request,
            ClaimsPrincipal caller,
            ISystemRepository system,
            IUserAdminRepository admin,
            IApiKeyRepository keys,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(caller, system, ct)) return Results.Forbid();

            JsonElement body;
            try { body = await request.ReadFromJsonAsync<JsonElement>(ct); }
            catch (JsonException) { return ApiErrors.BadRequest("invalid_body", "Body must be a JSON object."); }
            catch (InvalidOperationException) { return ApiErrors.BadRequest("invalid_body", "Body must be a JSON object."); }
            if (body.ValueKind != JsonValueKind.Object)
                return ApiErrors.BadRequest("invalid_body", "Body must be a JSON object.");

            var setQuota = body.TryGetProperty("quotaBytes", out var quotaEl);
            long? quota = null;
            if (setQuota)
            {
                if (quotaEl.ValueKind == JsonValueKind.Number && quotaEl.TryGetInt64(out var q) && q >= 0) quota = q;
                else if (quotaEl.ValueKind != JsonValueKind.Null)
                    return ApiErrors.BadRequest("invalid_value", "quotaBytes must be null (default), 0 (unlimited) or more.", new { field = "quotaBytes" });
            }
            bool? isAdmin = null, disabled = null;
            if (body.TryGetProperty("isAdmin", out var adminEl))
            {
                if (adminEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return ApiErrors.BadRequest("invalid_value", "isAdmin must be true or false.", new { field = "isAdmin" });
                isAdmin = adminEl.GetBoolean();
            }
            if (body.TryGetProperty("disabled", out var disEl))
            {
                if (disEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return ApiErrors.BadRequest("invalid_value", "disabled must be true or false.", new { field = "disabled" });
                disabled = disEl.GetBoolean();
            }
            if (!setQuota && isAdmin is null && disabled is null)
                return ApiErrors.BadRequest("nothing_to_change", "Nothing to change: send quotaBytes, isAdmin or disabled.");

            var target = await system.GetUserAsync(userId, ct);
            if (target is null) return ApiErrors.NotFound("no_such_user", "No such user.");
            var self = userId == ActorId(caller);
            var lastAdmin = target.IsAdmin && target.State == UserStates.Active
                && await admin.CountActiveAdminsAsync(ct) <= 1;

            // Validate every requested change first; write only if all pass.
            var grant = isAdmin == true && !target.IsAdmin;
            var revoke = isAdmin == false && target.IsAdmin;
            var disable = disabled == true && target.State != UserStates.Disabled;
            var enable = disabled == false && target.State == UserStates.Disabled;
            if (grant && target.State != UserStates.Active)
                return Results.Conflict(new { error = "not-active", state = target.State });
            if (revoke && lastAdmin)
                return Results.Conflict(new { error = "last-admin" });
            if (disable)
            {
                if (self) return ApiErrors.BadRequest("self_disable", "You can't disable yourself.");
                if (target.State != UserStates.Active)
                    return Results.Conflict(new { error = "not-active", state = target.State });
                if (lastAdmin) return Results.Conflict(new { error = "last-admin" });
            }
            if (disabled == false && target.State is not (UserStates.Disabled or UserStates.Active))
                return Results.Conflict(new { error = "not-disabled", state = target.State });

            // The writes that can lose the last active admin go first: they
            // re-check it themselves (another admin may be demoting at the
            // same moment), and a refusal then leaves the rest unwritten.
            var actor = ActorId(caller);
            if (revoke)
            {
                if (!await system.SetAdminAsync(userId, false, ct))
                    return Results.Conflict(new { error = "last-admin" });
                await admin.RecordAdminActionAsync(actor, AdminActions.RemoveAdmin, "user", userId, ct);
            }
            if (disable)
            {
                // Sign-in is refused and the request gate ends a live session
                // at its next request; the keys go too, so nothing keeps
                // working behind the admin's back. The data stays.
                if (!await admin.SetStateAsync(userId, UserStates.Disabled, ct))
                    return Results.Conflict(new { error = "last-admin" });
                foreach (var key in await keys.ListByUserAsync(userId, ct))
                    await keys.RevokeAsync(key.Id, userId, ct);
                await admin.RecordAdminActionAsync(actor, AdminActions.Disable, "user", userId, ct);
            }
            if (setQuota && quota != target.QuotaBytes)
            {
                await admin.SetQuotaAsync(userId, quota, ct);
                await admin.RecordAdminActionAsync(actor, AdminActions.SetQuota, "user", userId, ct);
            }
            if (grant)
            {
                await system.SetAdminAsync(userId, true, ct);
                await admin.RecordAdminActionAsync(actor, AdminActions.MakeAdmin, "user", userId, ct);
            }
            if (enable)
            {
                await admin.SetStateAsync(userId, UserStates.Active, ct);
                await admin.RecordAdminActionAsync(actor, AdminActions.Enable, "user", userId, ct);
            }

            var now = await system.GetUserAsync(userId, ct);
            return Results.Ok(new
            {
                id = userId,
                isAdmin = now!.IsAdmin,
                state = now.State,
                quotaBytes = now.QuotaBytes,
            });
        })
        .WithName("UpdateAdminUser")
        .WithSummary("Changes an account's quota, admin flag or disabled state. Disabling ends live sessions and revokes the account's API keys; data stays.")
        .RequireAuthorization();
    }

    // A3 — lifecycle: deleting an account (archive first by default) and the
    // System page's facts. Metadata only, like everything here: the archive
    // is a server-side copy the admin never opens.
    private static void MapLifecycle(RouteGroupBuilder group)
    {
        // What the delete dialog needs to know before it offers the button.
        group.MapGet("/users/{userId}/delete-check", async (
            string userId,
            ClaimsPrincipal caller,
            ISystemRepository system,
            IUserAdminRepository admin,
            DatabaseFactory dbFactory,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(caller, system, ct)) return Results.Forbid();
            var target = await system.GetUserAsync(userId, ct);
            if (target is null) return ApiErrors.NotFound("no_such_user", "No such user.");
            var owned = await admin.ListSolelyOwnedSpacesAsync(userId, ct);
            var lastAdmin = target.IsAdmin && target.State == UserStates.Active
                && await admin.CountActiveAdminsAsync(ct) <= 1;
            return Results.Ok(new
            {
                id = userId,
                self = userId == ActorId(caller),
                lastAdmin,
                hasData = Directory.Exists(dbFactory.ResolveContextFolder(ContextRef.User(userId))),
                ownedSpaces = owned.Select(sp => new { slug = sp.Slug, name = sp.Name }),
            });
        })
        .WithName("CheckDeleteUser")
        .WithSummary("Whether an account can be deleted now: yourself, the last admin, and the spaces it alone owns block it.")
        .RequireAuthorization();

        // DELETE /users/{id}?archive=true|false — archive defaults to true.
        group.MapDelete("/users/{userId}", async (
            string userId,
            bool? archive,
            ClaimsPrincipal caller,
            ISystemRepository system,
            IUserAdminRepository admin,
            IMessageRepository messages,
            IUserArchiveService archiver,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(caller, system, ct)) return Results.Forbid();
            var actor = ActorId(caller);
            if (userId == actor) return ApiErrors.BadRequest("self_delete", "You can't delete yourself.");
            var target = await system.GetUserAsync(userId, ct);
            if (target is null) return ApiErrors.NotFound("no_such_user", "No such user.");
            if (target.IsAdmin && target.State == UserStates.Active && await admin.CountActiveAdminsAsync(ct) <= 1)
                return Results.Conflict(new { error = "last-admin" });
            var owned = await admin.ListSolelyOwnedSpacesAsync(userId, ct);
            if (owned.Count > 0)
                return Results.Conflict(new
                {
                    error = "owns-spaces",
                    spaces = owned.Select(sp => new { slug = sp.Slug, name = sp.Name }),
                });

            // The account stops first: disabled, its sessions and keys answer
            // 403 from the next request on, so nothing is written after the
            // archive's copy. (The write refuses the last active admin.) If
            // the delete doesn't go through, it is active again.
            var stopped = false;
            if (target.State == UserStates.Active)
            {
                if (!await admin.SetStateAsync(userId, UserStates.Disabled, ct))
                    return Results.Conflict(new { error = "last-admin" });
                stopped = true;
            }
            var deleted = false;
            ArchivedUser? archived = null;
            try
            {
                // Archive first: only a verified ZIP lets the delete go on.
                if (archive != false)
                {
                    try { archived = await archiver.ArchiveAsync(userId, target.Name, target.CreatedAt, actor, ct); }
                    catch (FileStoreException ex) { return FilesApi.Fail(ex); }
                }

                // Re-checks inside its transaction: a space the account
                // created or restored meanwhile keeps it (and the admin hears why).
                if (!await admin.DeleteUserAsync(userId, ct))
                {
                    if (await system.GetUserAsync(userId, CancellationToken.None) is null)
                        return ApiErrors.NotFound("no_such_user", "No such user.");
                    var nowOwned = await admin.ListSolelyOwnedSpacesAsync(userId, CancellationToken.None);
                    return nowOwned.Count > 0
                        ? Results.Conflict(new { error = "owns-spaces", spaces = nowOwned.Select(sp => new { slug = sp.Slug, name = sp.Name }) })
                        : Results.Conflict(new { error = "last-admin" });
                }
                deleted = true;
            }
            finally
            {
                if (stopped && !deleted) await admin.SetStateAsync(userId, UserStates.Active, CancellationToken.None);
            }

            await messages.ResolveAsync(MessageKinds.UserPending, "user", userId, ct);
            await archiver.DeleteUserFolderAsync(userId, ct);
            await admin.RecordAdminActionAsync(actor, AdminActions.Delete, "user", userId, ct);

            return Results.Ok(new
            {
                id = userId,
                archive = archived is null ? null : new
                {
                    id = archived.Id,
                    sizeBytes = archived.SizeBytes,
                    expiresAt = archived.ExpiresAt,
                },
            });
        })
        .WithName("DeleteUser")
        .WithSummary("Deletes an account: archives users/<id>/ first unless ?archive=false, then removes its rows, keys, memberships and folder.")
        .RequireAuthorization();

        group.MapGet("/system", async (
            HttpContext http,
            ClaimsPrincipal caller,
            ISystemRepository system,
            DatabaseFactory dbFactory,
            IUserArchiveService userArchives,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(caller, system, ct)) return Results.Forbid();
            var dataRoot = Path.GetDirectoryName(dbFactory.UsersRoot)!;

            var userCount = (await system.ListUserIdsAsync(ct)).Count;
            long spaceCount;
            using (var sys = dbFactory.CreateSystemConnection())
                spaceCount = await sys.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM spaces", cancellationToken: ct));

            var systemBytes = Directory.EnumerateFiles(dataRoot, "system.db*").Sum(f => new FileInfo(f).Length);
            var userArchiveList = await userArchives.ListAsync(ct);
            var spaceZips = ZipsIn(Path.Combine(dataRoot, "archive", "spaces"));
            var retention = await system.GetConfigAsync(DataLifecycleLimits.ArchiveRetentionDaysKey, ct);

            var downloader = http.RequestServices.GetService(typeof(Fishbowl.Search.ModelDownloader)) as Fishbowl.Search.ModelDownloader;
            var status = http.RequestServices.GetService(typeof(SchedulerStatus)) as SchedulerStatus;

            return Results.Ok(new
            {
                version = VersionApi.Running,
                data = new
                {
                    users = new { count = userCount, bytes = DiskFileStore.Measure(dbFactory.UsersRoot).Bytes },
                    spaces = new { count = spaceCount, bytes = DiskFileStore.Measure(dbFactory.SpacesRoot).Bytes },
                    systemBytes,
                    modelsBytes = DiskFileStore.Measure(Path.Combine(dataRoot, "models")).Bytes,
                    logsBytes = DiskFileStore.Measure(Path.Combine(dataRoot, "logs")).Bytes,
                },
                archives = new
                {
                    spaces = new { count = spaceZips.Count, bytes = spaceZips.Sum(f => f.Length) },
                    users = new { count = userArchiveList.Count, bytes = userArchiveList.Sum(a => a.SizeBytes) },
                    retentionDays = int.TryParse(retention, out var days) && days >= 0
                        ? days : DataLifecycleLimits.DefaultArchiveRetentionDays,
                },
                embedding = new { status = downloader is null ? "off" : downloader.IsReady() ? "ready" : "downloading" },
                scheduler = new
                {
                    reminderTickAt = status?.LastReminderTickAt,
                    maintenanceAt = status?.LastMaintenanceAt,
                },
            });
        })
        .WithName("GetAdminSystem")
        .WithSummary("The System page: version, data size by context type, archives, embedding model status, when the scheduler last ran.")
        .RequireAuthorization();
    }

    private static List<FileInfo> ZipsIn(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.zip")
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .Select(f => new FileInfo(f)).ToList()
            : new List<FileInfo>();

    // Same rules as setup and cold import.
    private static string? ValidateUsername(string username)
    {
        if (username.Length < 3 || username.Length > 64) return "Username must be 3–64 characters.";
        foreach (var ch in username)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-'))
                return "Username may only contain letters, digits, _, . or -.";
        }
        return null;
    }

    private static async Task<long> DefaultQuotaAsync(ISystemRepository system, CancellationToken ct)
    {
        var raw = await system.GetConfigAsync(Fishbowl.Core.Files.FileLimits.DefaultUserQuotaBytesKey, ct);
        return long.TryParse(raw, out var v) && v >= 0 ? v : Fishbowl.Core.Files.FileLimits.DefaultUserQuotaBytes;
    }

    private static string ActorId(ClaimsPrincipal user) =>
        user.FindFirst(McpContextClaims.UserId)?.Value ?? "";

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

public sealed record ApproveUserRequest(long? QuotaBytes = null, bool? MakeAdmin = null);

public sealed record CreateLocalUserRequest(string? Username, string? DisplayName = null, long? QuotaBytes = null);

// The action names in admin_audit. Only ids ride along, never values.
public static class AdminActions
{
    public const string Approve = "user.approve";
    public const string Reject = "user.reject";
    public const string Block = "user.block";
    public const string Unblock = "user.unblock";
    public const string MakeAdmin = "user.make-admin";
    public const string RemoveAdmin = "user.remove-admin";
    public const string SetQuota = "user.quota";
    public const string Disable = "user.disable";
    public const string Enable = "user.enable";
    public const string CreateLocal = "user.create";
    public const string ImportUser = "user.import";
    public const string ResetPassword = "user.reset-password";
    public const string Delete = "user.delete";
    public const string ConfigSet = "config.set";
    public const string ConfigClear = "config.clear";
}

public sealed record ImportUserRequest(
    string FolderName,
    string Username,
    string Password,
    string? DisplayName = null);
