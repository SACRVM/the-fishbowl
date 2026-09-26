using Fishbowl.Core.Auth;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Api.Accounts;

// Why a sign-in was refused. The login page turns the code into a sentence.
public static class SignInRefusals
{
    public const string Domain = "domain";
    public const string Closed = "closed";
    public const string Blocked = UserStates.Blocked;
    public const string Disabled = UserStates.Disabled;

    public static string Describe(string code) => code switch
    {
        Domain => "This Fishbowl only accepts accounts from certain e-mail domains.",
        Closed => "This Fishbowl isn't open for new accounts. Ask its admin to add you.",
        Blocked => "This account can't sign in to this Fishbowl.",
        Disabled => "This account is disabled. Ask the admin of this Fishbowl.",
        _ => "Sign-in refused.",
    };
}

public sealed record SignInDecision(bool Allowed, string? UserId, string? Refusal, string State)
{
    public static SignInDecision Refuse(string code) => new(false, null, code, "");
    public static SignInDecision Allow(string userId, string state) => new(true, userId, null, state);
}

// Every sign-in goes through here, whatever the provider: it decides whether
// the identity may in, creates the account for a new one, and keeps the rules
// of the admin spec in one place —
//   1. the domain allow-list (new accounts only: an existing account is
//      managed by block/disable, and an admin can't lock themselves out by
//      tightening the list)
//   2. blocked / disabled refuse
//   3. bootstrap: with no active admin, the signing-in account becomes one
//   4. the sign-up policy for a new account: open → active, closed → refused,
//      approval → pending + a user.pending message to every admin.
// A pending account still gets its cookie; the request gate keeps it to the
// waiting page.
public class AccountGate
{
    public const string AuditBootstrap = "admin.bootstrap";

    private readonly ISystemRepository _system;
    private readonly IUserAdminRepository _admin;
    private readonly IMessageRepository _messages;
    private readonly ILogger<AccountGate> _logger;

    public AccountGate(
        ISystemRepository system, IUserAdminRepository admin, IMessageRepository messages,
        ILogger<AccountGate>? logger = null)
    {
        _system = system;
        _admin = admin;
        _messages = messages;
        _logger = logger ?? NullLogger<AccountGate>.Instance;
    }

    // An external identity (Google today): look up or create the account.
    public async Task<SignInDecision> SignInExternalAsync(
        string provider, string providerId, string? name, string? email, string? avatarUrl,
        CancellationToken ct = default)
    {
        var userId = await _system.GetUserIdByMappingAsync(provider, providerId, ct);
        if (!string.IsNullOrEmpty(userId))
        {
            var existing = await _system.GetUserAsync(userId, ct);
            if (existing is not null)
            {
                var refusal = RefusalFor(existing.State);
                if (refusal is not null) return SignInDecision.Refuse(refusal);
            }
            // Refresh the profile snapshot — name/avatar may change on the
            // provider's side. A no-op when nothing changed.
            await _system.UpsertUserAsync(userId, name, email, avatarUrl, ct);
            return await AdmitAsync(userId, ct);
        }

        var domains = SignUpPolicy.ParseDomains(await _system.GetConfigAsync(SignUpPolicy.AllowedDomainsKey, ct));
        if (!SignUpPolicy.IsEmailAllowed(email, domains))
            return SignInDecision.Refuse(SignInRefusals.Domain);

        var noAdmin = await _admin.CountActiveAdminsAsync(ct) == 0;
        var mode = SignUpPolicy.ParseMode(await _system.GetConfigAsync(SignUpPolicy.ModeKey, ct));
        if (!noAdmin && mode == SignUpPolicy.Closed)
            return SignInDecision.Refuse(SignInRefusals.Closed);

        userId = Guid.NewGuid().ToString();
        await _system.CreateUserAsync(userId, name, email, avatarUrl, ct);
        var pending = !noAdmin && mode == SignUpPolicy.Approval;
        // Pending before the mapping exists, so there is no moment in which
        // the identity maps to an active account it wasn't granted.
        if (pending) await _admin.SetStateAsync(userId, UserStates.Pending, ct);
        await _system.CreateUserMappingAsync(userId, provider, providerId, ct);
        _logger.LogInformation("Provisioned user {UserId} via {Provider} ({State})",
            userId, provider, pending ? UserStates.Pending : UserStates.Active);

        if (pending)
        {
            await _admin.TouchSignInAsync(userId, ct);
            var admins = await _admin.ListAdminIdsAsync(ct);
            await _messages.CreateAsync(admins, MessageKinds.UserPending, "user", userId, null, ct);
            return SignInDecision.Allow(userId, UserStates.Pending);
        }
        return await AdmitAsync(userId, ct);
    }

    // A local username/password sign-in whose password already verified.
    public async Task<SignInDecision> SignInExistingAsync(User user, CancellationToken ct = default)
    {
        var refusal = RefusalFor(user.State);
        if (refusal is not null) return SignInDecision.Refuse(refusal);
        return await AdmitAsync(user.Id, ct);
    }

    private static string? RefusalFor(string? state) => state switch
    {
        UserStates.Blocked => SignInRefusals.Blocked,
        UserStates.Disabled => SignInRefusals.Disabled,
        _ => null,
    };

    // The account is in (active or pending): stamp the sign-in, and make an
    // active account the admin when the instance has none.
    private async Task<SignInDecision> AdmitAsync(string userId, CancellationToken ct)
    {
        await _admin.TouchSignInAsync(userId, ct);
        var user = await _system.GetUserAsync(userId, ct);
        var state = user?.State ?? UserStates.Active;
        if (state == UserStates.Active && user?.IsAdmin != true && await _admin.CountActiveAdminsAsync(ct) == 0)
        {
            await _system.SetAdminAsync(userId, true, ct);
            await _admin.RecordAdminActionAsync(userId, AuditBootstrap, "user", userId, ct);
            _logger.LogInformation("No admin existed; user {UserId} became the admin", userId);
        }
        return SignInDecision.Allow(userId, state);
    }
}
