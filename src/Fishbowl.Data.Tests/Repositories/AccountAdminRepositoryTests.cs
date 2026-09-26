using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

// System schema v11: account state, the system inbox, the admin log — and
// the second wall (no storage for an account that isn't active).
public class AccountAdminRepositoryTests : IDisposable
{
    private readonly string _dir;
    private readonly DatabaseFactory _factory;
    private readonly SystemRepository _system;
    private readonly UserAdminRepository _admin;
    private readonly MessageRepository _messages;

    public AccountAdminRepositoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_account_admin_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _factory = new DatabaseFactory(_dir);
        _system = new SystemRepository(_factory);
        _admin = new UserAdminRepository(_factory);
        _messages = new MessageRepository(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NewUser_IsActive_ByDefault()
    {
        await _system.CreateUserAsync("u1", "Ada", "ada@example.com", null, Ct);
        var user = await _system.GetUserAsync("u1", Ct);
        Assert.Equal(UserStates.Active, user!.State);
        Assert.Null(user.QuotaBytes);
    }

    [Fact]
    public void UpgradeFromV10_ExistingUsersStayActive()
    {
        var dir = Path.Combine(_dir, "v10");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "system.db");
        using (var seed = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            seed.Open();
            seed.Execute(@"CREATE TABLE users (id TEXT PRIMARY KEY, name TEXT, email TEXT, avatar_url TEXT, created_at TEXT NOT NULL);
                           INSERT INTO users (id, name, created_at) VALUES ('old', 'Old', '2026-01-01T00:00:00Z');
                           PRAGMA user_version = 10;");
        }

        var factory = new DatabaseFactory(dir);
        using var db = factory.CreateSystemConnection();
        Assert.Equal(11, db.ExecuteScalar<long>("PRAGMA user_version"));
        Assert.Equal("active", db.ExecuteScalar<string>("SELECT state FROM users WHERE id = 'old'"));
        Assert.Equal(1, db.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'messages'"));
        Assert.Equal(1, db.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'admin_audit'"));
    }

    [Fact]
    public async Task Approve_OnlyMovesPendingToActive()
    {
        await _system.CreateUserAsync("p", "P", null, null, Ct);
        await _admin.SetStateAsync("p", UserStates.Pending, Ct);

        Assert.True(await _admin.ApproveAsync("p", "admin", 5_000, Ct));
        var user = await _system.GetUserAsync("p", Ct);
        Assert.Equal(UserStates.Active, user!.State);
        Assert.Equal(5_000, user.QuotaBytes);
        Assert.Equal("admin", user.ApprovedBy);
        Assert.NotNull(user.ApprovedAt);

        // A second approval (another admin, same moment) is a no-op.
        Assert.False(await _admin.ApproveAsync("p", "admin2", null, Ct));
    }

    [Fact]
    public async Task DeletePending_RemovesShell_ButNeverAnActiveAccount()
    {
        await _system.CreateUserAsync("p", "P", null, null, Ct);
        await _system.CreateUserMappingAsync("p", "google", "g-1", Ct);
        await _admin.SetStateAsync("p", UserStates.Pending, Ct);
        await _system.CreateUserAsync("a", "A", null, null, Ct);

        Assert.False(await _admin.DeletePendingAsync("a", Ct));
        Assert.True(await _admin.DeletePendingAsync("p", Ct));
        Assert.Null(await _system.GetUserAsync("p", Ct));
        Assert.Null(await _system.GetUserIdByMappingAsync("google", "g-1", Ct));
        Assert.NotNull(await _system.GetUserAsync("a", Ct));
    }

    [Fact]
    public async Task ActiveAdmins_CountOnlyActiveOnes()
    {
        await _system.CreateUserAsync("a1", "A1", null, null, Ct);
        await _system.CreateUserAsync("a2", "A2", null, null, Ct);
        await _system.SetAdminAsync("a1", true, Ct);
        await _system.SetAdminAsync("a2", true, Ct);
        await _admin.SetStateAsync("a2", UserStates.Blocked, Ct);

        Assert.Equal(1, await _admin.CountActiveAdminsAsync(Ct));
        Assert.Equal(new[] { "a1" }, await _admin.ListAdminIdsAsync(Ct));
        Assert.DoesNotContain("a2", await _system.ListActiveUserIdsAsync(Ct));
    }

    [Fact]
    public async Task ListUsers_CarriesProviders_AndNoContent()
    {
        await _system.CreateUserAsync("u", "Ada", "ada@example.com", null, Ct);
        await _system.CreateUserMappingAsync("u", "google", "g-u", Ct);
        await _system.CreateUserMappingAsync("u", "local", "ada", Ct);

        var row = Assert.Single(await _admin.ListUsersAsync(Ct));
        Assert.Equal(new[] { "google", "local" }, row.Providers);
        Assert.Equal(UserStates.Active, row.State);
    }

    [Fact]
    public async Task Messages_AreOwnOnly_AndResolveForEveryRecipient()
    {
        await _messages.CreateAsync(new[] { "admin1", "admin2" }, MessageKinds.UserPending, "user", "p", null, Ct);

        var mine = Assert.Single(await _messages.ListAsync("admin1", ct: Ct));
        Assert.Equal(MessageKinds.UserPending, mine.Kind);
        Assert.Equal("p", mine.SubjectId);
        Assert.Empty(await _messages.ListAsync("someone-else", ct: Ct));

        // Someone else's id can't be marked read.
        Assert.False(await _messages.MarkReadAsync(mine.Id, "admin2", Ct));
        Assert.True(await _messages.MarkReadAsync(mine.Id, "admin1", Ct));
        Assert.Equal(0, await _messages.CountUnreadAsync("admin1", Ct));
        Assert.Equal(1, await _messages.CountUnreadAsync("admin2", Ct));

        Assert.Equal(2, await _messages.ResolveAsync(MessageKinds.UserPending, "user", "p", Ct));
        var theirs = Assert.Single(await _messages.ListAsync("admin2", ct: Ct));
        Assert.NotNull(theirs.DoneAt);
        Assert.NotNull(theirs.ReadAt);
        Assert.Equal(0, await _messages.CountUnreadAsync("admin2", Ct));
    }

    [Fact]
    public async Task AdminLog_HoldsIdsOnly()
    {
        await _admin.RecordAdminActionAsync("actor", "user.approve", "user", "target", Ct);
        var entry = Assert.Single(await _admin.ListAuditAsync(ct: Ct));
        Assert.Equal("actor", entry.ActorId);
        Assert.Equal("user.approve", entry.Action);
        Assert.Equal("target", entry.TargetId);

        using var db = _factory.CreateSystemConnection();
        var columns = db.Query<string>("SELECT name FROM pragma_table_info('admin_audit')").ToList();
        Assert.Equal(new[] { "id", "actor_id", "action", "target_type", "target_id", "at" }, columns);
    }

    [Theory]
    [InlineData(UserStates.Pending)]
    [InlineData(UserStates.Disabled)]
    [InlineData(UserStates.Blocked)]
    public async Task InactiveAccount_GetsNoPersonalDb(string state)
    {
        await _system.CreateUserAsync("x", "X", null, null, Ct);
        await _admin.SetStateAsync("x", state, Ct);

        var ex = Assert.Throws<InactiveAccountException>(() => _factory.CreateContextConnection(ContextRef.User("x")));
        Assert.Equal(state, ex.State);
        Assert.False(Directory.Exists(Path.Combine(_dir, "users", "x")));
    }

    [Fact]
    public async Task ActiveOrUnregisteredAccount_GetsItsDb()
    {
        await _system.CreateUserAsync("ok", "Ok", null, null, Ct);
        using (_factory.CreateContextConnection(ContextRef.User("ok"))) { }
        using (_factory.CreateContextConnection(ContextRef.User("never-registered"))) { }
        Assert.True(File.Exists(Path.Combine(_dir, "users", "ok", "personal.db")));
        Assert.True(File.Exists(Path.Combine(_dir, "users", "never-registered", "personal.db")));
    }
}
