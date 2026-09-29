using Dapper;
using Fishbowl.Api.Accounts;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Host.Tests;

// The account rules every sign-in goes through (Google's OnTicketReceived
// and the local login both call AccountGate): the domain allow-list,
// blocked/disabled, the admin bootstrap and the sign-up policy.
public class AccountGateTests : IDisposable
{
    private readonly string _dir;
    private readonly DatabaseFactory _factory;
    private readonly SystemRepository _system;
    private readonly UserAdminRepository _admin;
    private readonly MessageRepository _messages;
    private readonly SpaceInviteRepository _invites;
    private readonly SpaceRepository _spaces;
    private readonly AccountGate _gate;

    public AccountGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_account_gate_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _factory = new DatabaseFactory(_dir);
        _system = new SystemRepository(_factory);
        _admin = new UserAdminRepository(_factory);
        _messages = new MessageRepository(_factory);
        _invites = new SpaceInviteRepository(_factory);
        _spaces = new SpaceRepository(_factory);
        _gate = new AccountGate(_system, _admin, _messages, _invites, _spaces);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<SignInDecision> Google(string id, string? email = null) =>
        _gate.SignInExternalAsync("google", id, "Name " + id, email ?? $"{id}@example.com", null, ct: Ct);

    [Fact]
    public async Task FirstSignIn_OnAnInstanceWithoutAdmin_BecomesActiveAdmin()
    {
        var d = await Google("first");
        Assert.True(d.Allowed);
        Assert.Equal(UserStates.Active, d.State);
        var user = await _system.GetUserAsync(d.UserId!, Ct);
        Assert.True(user!.IsAdmin);
        Assert.NotNull(user.LastSignInAt);
        var log = Assert.Single(await _admin.ListAuditAsync(ct: Ct));
        Assert.Equal(AccountGate.AuditBootstrap, log.Action);
    }

    [Fact]
    public async Task NewSignIn_UnderDefaultPolicy_IsPending_AndEveryAdminIsTold()
    {
        var admin1 = (await Google("admin1")).UserId!;
        await _system.CreateUserAsync("admin2", "A2", null, null, ct: Ct);
        await _system.SetAdminAsync("admin2", true, Ct);

        var d = await Google("newbie");
        Assert.True(d.Allowed);
        Assert.Equal(UserStates.Pending, d.State);
        Assert.False((await _system.GetUserAsync(d.UserId!, Ct))!.IsAdmin);

        foreach (var admin in new[] { admin1, "admin2" })
        {
            var msg = Assert.Single(await _messages.ListAsync(admin, ct: Ct));
            Assert.Equal(MessageKinds.UserPending, msg.Kind);
            Assert.Equal(d.UserId, msg.SubjectId);
            // Ids only: nothing about the person is copied into the row.
            Assert.Null(msg.Data);
        }
        Assert.Empty(await _messages.ListAsync(d.UserId!, ct: Ct));

        // Signing in again while pending stays pending and doesn't nag again.
        var again = await Google("newbie");
        Assert.Equal(d.UserId, again.UserId);
        Assert.Equal(UserStates.Pending, again.State);
        Assert.Single(await _messages.ListAsync(admin1, ct: Ct));
    }

    private async Task<(string Slug, string Token)> InviteAsync(string ownerId, SpaceRole role = SpaceRole.Member, TimeSpan? valid = null)
    {
        var space = await _spaces.CreateAsync(ownerId, "Club " + Guid.NewGuid().ToString("N")[..6], Ct);
        var (_, token) = await _invites.CreateAsync(space.Id, role, ownerId, valid ?? TimeSpan.FromDays(7), Ct);
        return (space.Slug, token);
    }

    // An invitation is an invitation: closed sign-up and the domain list don't
    // apply, the account starts active in the space, the admins are told.
    [Fact]
    public async Task Invitation_LetsANewAccountIn_WhateverThePolicy()
    {
        var admin = (await Google("admin")).UserId!;
        await _system.SetConfigAsync(SignUpPolicy.ModeKey, "closed", Ct);
        await _system.SetConfigAsync(SignUpPolicy.AllowedDomainsKey, "club.example", Ct);
        var (slug, token) = await InviteAsync(admin, SpaceRole.Designer);

        var d = await _gate.SignInExternalAsync("google", "guest", "Guest", "guest@elsewhere.example", null, token, Ct);
        Assert.True(d.Allowed);
        Assert.Equal(UserStates.Active, d.State);
        Assert.Equal(slug, d.JoinedSpace);
        var space = await _spaces.GetBySlugAsync(slug, Ct);
        Assert.Equal(SpaceRole.Designer, await _spaces.GetMembershipAsync(space!.Id, d.UserId!, Ct));
        var msg = Assert.Single(await _messages.ListAsync(admin, ct: Ct));
        Assert.Equal(MessageKinds.UserInvited, msg.Kind);
        Assert.Equal(d.UserId, msg.SubjectId);

        // One use: the next stranger with the same link is refused again.
        var second = await _gate.SignInExternalAsync("google", "guest2", "G2", "g2@elsewhere.example", null, token, Ct);
        Assert.False(second.Allowed);
    }

    [Fact]
    public async Task Invitation_ActivatesAPendingAccount_AndResolvesItsRequest()
    {
        var admin = (await Google("admin")).UserId!;
        var pending = await Google("waiting");
        Assert.Equal(UserStates.Pending, pending.State);
        var (slug, token) = await InviteAsync(admin);

        Assert.Equal(slug, await _gate.AcceptInviteAsync(pending.UserId!, token, Ct));
        Assert.Equal(UserStates.Active, (await _system.GetUserAsync(pending.UserId!, Ct))!.State);
        var messages = await _messages.ListAsync(admin, ct: Ct);
        Assert.NotNull(messages.Single(m => m.Kind == MessageKinds.UserPending).DoneAt);
        Assert.Single(messages, m => m.Kind == MessageKinds.UserInvited);
        Assert.Null(await _gate.AcceptInviteAsync(pending.UserId!, token, Ct));   // spent
    }

    [Fact]
    public async Task Invitation_Expired_OrBlocked_DoesNothing()
    {
        var admin = (await Google("admin")).UserId!;
        await _system.SetConfigAsync(SignUpPolicy.ModeKey, "closed", Ct);
        var (_, old) = await InviteAsync(admin, valid: TimeSpan.FromSeconds(-1));
        Assert.False((await _gate.SignInExternalAsync("google", "late", "L", "l@x.example", null, old, Ct)).Allowed);

        var member = (await Google("admin2")).UserId;   // closed: refused, no row
        Assert.Null(member);
        await _system.CreateUserAsync("blocked", "B", null, null, ct: Ct);
        await _admin.SetStateAsync("blocked", UserStates.Blocked, Ct);
        var (_, token) = await InviteAsync(admin);
        Assert.Null(await _gate.AcceptInviteAsync("blocked", token, Ct));
        Assert.Equal(InviteProblem.None, (await _invites.CheckAsync(token, Ct)).Problem);   // not spent
    }

    [Fact]
    public async Task OpenPolicy_ActivatesImmediately()
    {
        await Google("admin");
        await _system.SetConfigAsync(SignUpPolicy.ModeKey, "open", Ct);
        var d = await Google("guest");
        Assert.Equal(UserStates.Active, d.State);
    }

    [Fact]
    public async Task ClosedPolicy_RefusesNewAccounts_WithoutARow()
    {
        await Google("admin");
        await _system.SetConfigAsync(SignUpPolicy.ModeKey, "closed", Ct);
        var d = await Google("stranger");
        Assert.False(d.Allowed);
        Assert.Equal(SignInRefusals.Closed, d.Refusal);
        Assert.Null(await _system.GetUserIdByMappingAsync("google", "stranger", Ct));
        Assert.Single(await _admin.ListUsersAsync(Ct));
    }

    [Fact]
    public async Task DomainAllowList_RefusesOutsiders_ButNotExistingAccounts()
    {
        var admin = await Google("admin", "boss@elsewhere.org");
        await _system.SetConfigAsync(SignUpPolicy.AllowedDomainsKey, "family.example", Ct);

        var outsider = await Google("outsider", "someone@gmail.test");
        Assert.False(outsider.Allowed);
        Assert.Equal(SignInRefusals.Domain, outsider.Refusal);
        Assert.Null(await _system.GetUserIdByMappingAsync("google", "outsider", Ct));
        Assert.Empty(await _messages.ListAsync(admin.UserId!, ct: Ct));

        var insider = await Google("insider", "kid@Family.Example");
        Assert.True(insider.Allowed);
        Assert.Equal(UserStates.Pending, insider.State);

        // The admin's own account predates the list: still gets in.
        Assert.True((await Google("admin", "boss@elsewhere.org")).Allowed);
    }

    [Theory]
    [InlineData(UserStates.Blocked)]
    [InlineData(UserStates.Disabled)]
    public async Task BlockedOrDisabled_AreRefused(string state)
    {
        await Google("admin");
        var d = await Google("troll");
        await _admin.SetStateAsync(d.UserId!, state, Ct);

        var again = await Google("troll");
        Assert.False(again.Allowed);
        Assert.Equal(state, again.Refusal);

        var local = await _system.GetUserAsync(d.UserId!, Ct);
        var viaLocal = await _gate.SignInExistingAsync(local!, ct: Ct);
        Assert.False(viaLocal.Allowed);
    }

    [Fact]
    public async Task ExistingInstallWithoutAdmin_FirstExistingSignInBecomesAdmin()
    {
        // A pre-v11 Google-only install: users, no admin.
        await _system.CreateUserAsync("old", "Old", "old@example.com", null, ct: Ct);
        await _system.CreateUserMappingAsync("old", "google", "old-google", Ct);

        var d = await Google("old-google", "old@example.com");
        Assert.Equal("old", d.UserId);
        Assert.True((await _system.GetUserAsync("old", Ct))!.IsAdmin);
    }

    [Fact]
    public async Task PendingAccount_NeverBootstrapsIntoAdmin()
    {
        await Google("admin");
        var p = await Google("pending-one");
        // The only admin gets blocked; the pending account signs in again.
        using (var db = _factory.CreateSystemConnection())
            db.Execute("UPDATE users SET is_admin = 0");
        var again = await Google("pending-one");
        Assert.Equal(UserStates.Pending, again.State);
        Assert.False((await _system.GetUserAsync(p.UserId!, Ct))!.IsAdmin);
    }
}
