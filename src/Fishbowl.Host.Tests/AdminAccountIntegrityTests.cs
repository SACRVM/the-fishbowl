using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Files;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// Account administration that must hold under a case-insensitive file system
// and concurrent admins: the cold import never aliases another user's folder,
// an imported account is approved and must change its password, the last
// active admin can't be lost to a race, a delete stops the account before
// archiving and re-checks owned spaces, and adding a local user is one write.
public class AdminAccountIntegrityTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dir;
    private readonly DatabaseFactory _db;
    private readonly SystemRepository _system;
    private readonly UserAdminRepository _admin;

    public AdminAccountIntegrityTests(WebApplicationFactory<Program> factory)
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_admin_integrity_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _db = new DatabaseFactory(_dir);
        _system = new SystemRepository(_db);
        _admin = new UserAdminRepository(_db);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton(_db);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.AuthenticationScheme, _ => { });
            });
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient As(string userId, WebApplicationFactory<Program>? factory = null)
    {
        var client = (factory ?? _factory).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
        return client;
    }

    private async Task SeedAdminAsync(string id)
    {
        await _system.CreateUserAsync(id, "Admin " + id, null, null, Ct);
        await _system.SetAdminAsync(id, true, Ct);
    }

    private void SeedFolderOnDisk(string folderName)
    {
        var dir = Path.Combine(_dir, "users", folderName);
        Directory.CreateDirectory(dir);
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(dir, "personal.db")};Pooling=False"))
        {
            conn.Open();
            conn.Execute(@"
                CREATE TABLE notes (
                    id TEXT PRIMARY KEY, title TEXT NOT NULL, content TEXT,
                    content_secret BLOB, type TEXT NOT NULL DEFAULT 'note', tags TEXT,
                    created_by TEXT NOT NULL, created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL, pinned INTEGER DEFAULT 0,
                    archived INTEGER DEFAULT 0);
                CREATE VIRTUAL TABLE notes_fts USING fts5(title, content, tags);
                PRAGMA user_version = 1;");
        }
        SqliteConnection.ClearAllPools();
    }

    private static async Task<string?> Error(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("error", out var e) ? e.GetString() : null;

    private Task<HttpResponseMessage> Import(HttpClient client, string folder, string username) =>
        client.PostAsJsonAsync("/api/v1/admin/users/import",
            new { FolderName = folder, Username = username, Password = "supersecret-password" }, Ct);

    // ── Cold import ──────────────────────────────────────────────────────

    [Fact]
    public async Task Import_ARegisteredIdInAnotherCase_IsThatUser()
    {
        await SeedAdminAsync("boss");
        await _system.CreateUserAsync("victim-id", "Victim", null, null, Ct);
        SeedFolderOnDisk("victim-id");

        var resp = await Import(As("boss"), "VICTIM-ID", "mallory");

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("user_exists", await Error(resp));
        Assert.Null(await _system.GetUserAsync("VICTIM-ID", Ct));
        Assert.Null(await _system.GetUserByLocalUsernameAsync("mallory", Ct));
    }

    [Fact]
    public async Task Import_OnlyAFolderThatIsThereUnderExactlyThatName()
    {
        await SeedAdminAsync("boss");
        SeedFolderOnDisk("incoming-1");
        var boss = As("boss");

        // Windows would open users/incoming-1/ for both spellings.
        foreach (var alias in new[] { "incoming-1.", "INCOMING-1" })
        {
            var resp = await Import(boss, alias, "alias-" + alias.Length);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Equal("no_personal_db", await Error(resp));
            Assert.Null(await _system.GetUserAsync(alias, Ct));
        }

        Assert.Equal(HttpStatusCode.OK, (await Import(boss, "incoming-1", "alice")).StatusCode);
    }

    [Fact]
    public async Task Import_ATombstoneCoversItsIdInAnyCase_AndTheListSkipsIt()
    {
        await SeedAdminAsync("boss");
        using (var sys = _db.CreateSystemConnection())
            sys.Execute("INSERT INTO deleted_users (id, deleted_at) VALUES ('gone-id', @now)", new { now = DateTime.UtcNow.ToString("o") });
        await _system.CreateUserAsync("carol-id", "Carol", null, null, Ct);
        SeedFolderOnDisk("GONE-ID");
        SeedFolderOnDisk("CAROL-ID");
        var boss = As("boss");

        var resp = await Import(boss, "GONE-ID", "ghost");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_folder_name", await Error(resp));

        var list = await boss.GetFromJsonAsync<List<JsonElement>>("/api/v1/admin/users/importable", Ct);
        Assert.Empty(list!);
    }

    [Fact]
    public async Task Import_IsApprovedByTheAdmin_AndThePasswordMustBeChanged()
    {
        await SeedAdminAsync("boss");
        SeedFolderOnDisk("incoming-2");
        var boss = As("boss");

        Assert.Equal(HttpStatusCode.OK, (await Import(boss, "incoming-2", "bea")).StatusCode);

        var user = await _system.GetUserAsync("incoming-2", Ct);
        Assert.NotNull(user);
        Assert.True(user!.MustChangePassword);
        Assert.Equal(UserStates.Active, user.State);
        Assert.Equal("boss", user.ApprovedBy);
        Assert.NotNull(user.ApprovedAt);

        // Block → unblock brings an approved account back, not to pending.
        Assert.Equal(HttpStatusCode.NoContent, (await boss.PostAsync("/api/v1/admin/users/incoming-2/block", null, Ct)).StatusCode);
        var unblock = await boss.PostAsync("/api/v1/admin/users/incoming-2/unblock", null, Ct);
        Assert.Equal(UserStates.Active, (await unblock.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("state").GetString());
    }

    // ── The last active admin ────────────────────────────────────────────

    [Fact]
    public async Task TheGuardedWrites_NeverLoseTheLastActiveAdmin()
    {
        await SeedAdminAsync("a");
        await SeedAdminAsync("b");

        Assert.True(await _admin.SetStateAsync("a", UserStates.Blocked, Ct));
        // b is now the only active admin: every way of taking it out refuses.
        Assert.False(await _admin.SetStateAsync("b", UserStates.Blocked, Ct));
        Assert.False(await _admin.SetStateAsync("b", UserStates.Disabled, Ct));
        Assert.False(await _system.SetAdminAsync("b", false, Ct));
        Assert.False(await _admin.DeleteUserAsync("b", Ct));
        Assert.Equal(1, await _admin.CountActiveAdminsAsync(Ct));

        // Back to two: either may go again.
        Assert.True(await _admin.SetStateAsync("a", UserStates.Active, Ct));
        Assert.True(await _system.SetAdminAsync("b", false, Ct));
        Assert.Equal(1, await _admin.CountActiveAdminsAsync(Ct));
    }

    [Fact]
    public async Task TwoAdminsDemotingEachOtherAtOnce_LeaveOne()
    {
        for (var round = 0; round < 5; round++)
        {
            var a = "ra" + round;
            var b = "rb" + round;
            await SeedAdminAsync(a);
            await SeedAdminAsync(b);
            using (var sys = _db.CreateSystemConnection())   // only this pair is active admin
                sys.Execute("UPDATE users SET state = 'disabled' WHERE is_admin = 1 AND id NOT IN (@a, @b)", new { a, b });

            var results = await Task.WhenAll(
                As(a).PatchAsJsonAsync($"/api/v1/admin/users/{b}", new { disabled = true }, Ct),
                As(b).PostAsync($"/api/v1/admin/users/{a}/block", null, Ct));

            // One wins; the other is refused — by the guard (409) or, if it
            // came second, because its own account is already out (403).
            Assert.Equal(1, await _admin.CountActiveAdminsAsync(Ct));
            Assert.Single(results, r => r.IsSuccessStatusCode);
        }
    }

    // ── Delete ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_StopsTheAccountBeforeArchiving_AndASpaceMadeMeanwhileKeepsIt()
    {
        await SeedAdminAsync("boss");
        await _system.CreateUserAsync("leaver", "Leaver", null, null, Ct);
        var archiver = new SpaceMakingArchiver(_db);
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            foreach (var d in s.Where(d => d.ServiceType == typeof(IUserArchiveService)).ToList()) s.Remove(d);
            s.AddSingleton<IUserArchiveService>(archiver);
        }));

        var resp = await As("boss", factory).DeleteAsync("/api/v1/admin/users/leaver", Ct);

        Assert.Equal(UserStates.Disabled, archiver.StateWhileArchiving);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("owns-spaces", body.GetProperty("error").GetString());
        Assert.Single(body.GetProperty("spaces").EnumerateArray());
        // Nothing went: the account is back as it was.
        var leaver = await _system.GetUserAsync("leaver", Ct);
        Assert.Equal(UserStates.Active, leaver!.State);
        Assert.False(await _admin.IsDeletedAsync("leaver", Ct));
    }

    [Fact]
    public async Task DeleteUser_TakesTheAccountsOAuthCodesAlong()
    {
        await SeedAdminAsync("boss");
        await _system.CreateUserAsync("coder", "Coder", null, null, Ct);
        await new OAuthRepository(_db).SaveCodeAsync(new OAuthCode("hash-1", "fbc_x", "coder", "user", "coder",
            new[] { "read:notes" }, "https://claude.ai/cb", new string('c', 43), DateTime.UtcNow.AddMinutes(5)), Ct);

        Assert.True(await _admin.DeleteUserAsync("coder", Ct));

        using var sys = _db.CreateSystemConnection();
        Assert.Equal(0, sys.ExecuteScalar<int>("SELECT COUNT(*) FROM oauth_codes WHERE user_id = 'coder'"));
    }

    // ── Adding a local user ──────────────────────────────────────────────

    [Fact]
    public async Task AddingTheSameUsernameTwiceAtOnce_MakesOneAccount_AndNothingElse()
    {
        await SeedAdminAsync("boss");
        var boss = As("boss");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            boss.PostAsJsonAsync("/api/v1/admin/users", new { username = "twin" }, Ct)));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        using var sys = _db.CreateSystemConnection();
        Assert.Equal(1, sys.ExecuteScalar<int>("SELECT COUNT(*) FROM users WHERE id <> 'boss'"));
        var user = await _system.GetUserByLocalUsernameAsync("twin", Ct);
        Assert.Equal(UserStates.Active, user!.State);
        Assert.True(user.MustChangePassword);
    }

    // Archives nothing; while "archiving" the account creates a space it
    // alone owns — what a request in flight could do during a long archive.
    private sealed class SpaceMakingArchiver(DatabaseFactory db) : IUserArchiveService
    {
        public string? StateWhileArchiving { get; private set; }

        public async Task<ArchivedUser?> ArchiveAsync(string userId, string? name, DateTime createdAt, string actorId, CancellationToken ct = default)
        {
            StateWhileArchiving = (await new SystemRepository(db).GetUserAsync(userId, ct))?.State;
            await new SpaceRepository(db).CreateAsync(userId, "Made meanwhile", ct);
            return null;
        }

        public Task DeleteUserFolderAsync(string userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ArchivedUser>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ArchivedUser>>(Array.Empty<ArchivedUser>());
        public Task<int> PurgeAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}
