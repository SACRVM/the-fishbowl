using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Fishbowl.Api.Accounts;
using Fishbowl.Core;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Models;
using Fishbowl.Core.Plugins;
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

// Admin phase A2 over HTTP: adding a local user, quota, admin flag, disable /
// enable (which also revokes keys), the audit trail, metadata-only answers —
// and the chat fan-out that carries a new system message's subject line.
public class AdminManageApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dir;
    private readonly DatabaseFactory _db;
    private readonly SystemRepository _system;
    private readonly UserAdminRepository _admin;
    private readonly ApiKeyRepository _keys;
    private readonly FakeBot _bot = new();

    public AdminManageApiTests(WebApplicationFactory<Program> factory)
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_admin_manage_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _db = new DatabaseFactory(_dir);
        _system = new SystemRepository(_db);
        _admin = new UserAdminRepository(_db);
        _keys = new ApiKeyRepository(_db);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton(_db);
                // Only the fake bot: the real Discord client stays out of it.
                foreach (var d in services.Where(d => d.ServiceType == typeof(IBotClient)).ToList()) services.Remove(d);
                services.AddSingleton<IBotClient>(_bot);
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

    private HttpClient As(string userId)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
        return client;
    }

    private async Task SeedAdminAsync(string id)
    {
        await _system.CreateUserAsync(id, "Admin " + id, id + "@admins.example", null, Ct);
        await _system.SetAdminAsync(id, true, Ct);
    }

    private async Task SeedUserAsync(string id)
    {
        await _system.CreateUserAsync(id, "Member " + id, id + "@example.com", null, Ct);
    }

    private static Task<HttpResponseMessage> Patch(HttpClient c, string id, object body) =>
        c.PatchAsJsonAsync($"/api/v1/admin/users/{id}", body, Ct);

    private int AuditCount(string action)
    {
        using var db = _db.CreateSystemConnection();
        return db.ExecuteScalar<int>("SELECT COUNT(*) FROM admin_audit WHERE action = @action", new { action });
    }

    [Fact]
    public async Task AddLocalUser_IsActive_ApprovedByTheAdmin_WithATempPasswordOnce()
    {
        await SeedAdminAsync("boss");
        var boss = As("boss");

        var resp = await boss.PostAsJsonAsync("/api/v1/admin/users",
            new { username = "Ada.L", displayName = "Ada Lovelace", quotaBytes = 3L * 1024 * 1024 * 1024 }, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = body.GetProperty("id").GetString()!;
        Assert.Equal("ada.l", body.GetProperty("username").GetString());
        Assert.Equal(12, body.GetProperty("tempPassword").GetString()!.Length);
        Assert.True(body.GetProperty("mustChangeOnNextLogin").GetBoolean());

        var user = await _system.GetUserAsync(id, Ct);
        Assert.Equal(UserStates.Active, user!.State);
        Assert.Equal("boss", user.ApprovedBy);
        Assert.Equal(3L * 1024 * 1024 * 1024, user.QuotaBytes);
        Assert.Equal("Ada Lovelace", user.Name);
        Assert.True(user.MustChangePassword);
        var hasher = _factory.Services.GetRequiredService<IPasswordHasher>();
        Assert.True(hasher.Verify(body.GetProperty("tempPassword").GetString()!, user.PasswordHash!, user.PasswordSalt!));
        Assert.Equal(id, (await _system.GetUserByLocalUsernameAsync("ada.l", Ct))!.Id);
        Assert.Equal(1, AuditCount("user.create"));

        // The listing never repeats the password.
        var list = await boss.GetStringAsync("/api/v1/admin/users", Ct);
        Assert.DoesNotContain(body.GetProperty("tempPassword").GetString()!, list);

        // Taken name, bad name, bad quota.
        Assert.Equal(HttpStatusCode.Conflict,
            (await boss.PostAsJsonAsync("/api/v1/admin/users", new { username = "ada.l" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await boss.PostAsJsonAsync("/api/v1/admin/users", new { username = "a b" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await boss.PostAsJsonAsync("/api/v1/admin/users", new { username = "okname", quotaBytes = -1 }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Quota_IsSetAndClearedToTheDefault_OneLogRowPerChange()
    {
        await SeedAdminAsync("boss");
        await SeedUserAsync("m");
        var boss = As("boss");

        var resp = await Patch(boss, "m", new { quotaBytes = 1024L * 1024 * 1024 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1024L * 1024 * 1024, (await _system.GetUserAsync("m", Ct))!.QuotaBytes);

        resp = await boss.PatchAsync("/api/v1/admin/users/m",
            new StringContent("{\"quotaBytes\":null}", System.Text.Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Null((await _system.GetUserAsync("m", Ct))!.QuotaBytes);
        Assert.Equal(2, AuditCount("user.quota"));

        // Same value again changes nothing and logs nothing.
        await boss.PatchAsync("/api/v1/admin/users/m",
            new StringContent("{\"quotaBytes\":null}", System.Text.Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(2, AuditCount("user.quota"));

        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(boss, "m", new { quotaBytes = -5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(boss, "m", new { quotaBytes = "lots" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(boss, "m", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Patch(boss, "ghost", new { quotaBytes = 0 })).StatusCode);
    }

    [Fact]
    public async Task Admin_CanBeGrantedAndRemoved_ButNeverTheLastOne()
    {
        await SeedAdminAsync("boss");
        await SeedUserAsync("m");
        var boss = As("boss");

        // The only admin can't step down.
        var last = await Patch(boss, "boss", new { isAdmin = false });
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        Assert.Equal("last-admin", (await last.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Patch(boss, "m", new { isAdmin = true })).StatusCode);
        Assert.True((await _system.GetUserAsync("m", Ct))!.IsAdmin);

        // With a second admin, stepping down works — and then the new one is last.
        Assert.Equal(HttpStatusCode.OK, (await Patch(boss, "boss", new { isAdmin = false })).StatusCode);
        Assert.False((await _system.GetUserAsync("boss", Ct))!.IsAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(boss, "m", new { isAdmin = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Patch(As("m"), "m", new { isAdmin = false })).StatusCode);

        Assert.Equal(1, AuditCount("user.make-admin"));
        Assert.Equal(1, AuditCount("user.remove-admin"));

        // A pending account can't be made an admin behind the approval.
        await _system.CreateUserAsync("p", "P", null, null, Ct);
        await _admin.SetStateAsync("p", UserStates.Pending, Ct);
        Assert.Equal(HttpStatusCode.Conflict, (await Patch(As("m"), "p", new { isAdmin = true })).StatusCode);
        Assert.False((await _system.GetUserAsync("p", Ct))!.IsAdmin);
    }

    [Fact]
    public async Task Disable_EndsALiveSession_RevokesKeys_KeepsData_AndEnableRestores()
    {
        await SeedAdminAsync("boss");
        await SeedUserAsync("m");
        var member = As("m");
        Assert.True((await member.PostAsJsonAsync("/api/v1/notes", new { title = "Kept", content = "x" }, Ct)).IsSuccessStatusCode);
        await _keys.IssueAsync("m", ContextRef.User("m"), "k1", new[] { "read:notes" }, Ct);
        await _keys.IssueAsync("m", ContextRef.User("m"), "k2", new[] { "read:notes" }, Ct);

        var boss = As("boss");
        var resp = await Patch(boss, "m", new { disabled = true });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("disabled", (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("state").GetString());

        var next = await member.GetAsync("/api/v1/notes", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, next.StatusCode);
        Assert.Equal("disabled", (await next.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Empty(await _keys.ListByUserAsync("m", Ct));
        Assert.True(File.Exists(Path.Combine(_dir, "users", "m", DatabaseFactory.PersonalDbFileName)));

        // Only an active account can be disabled, and never yourself.
        Assert.Equal(HttpStatusCode.OK, (await Patch(boss, "m", new { disabled = true })).StatusCode);
        Assert.Equal(1, AuditCount("user.disable"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(boss, "boss", new { disabled = true })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Patch(boss, "m", new { disabled = false })).StatusCode);
        Assert.Equal(UserStates.Active, (await _system.GetUserAsync("m", Ct))!.State);
        var notes = await member.GetFromJsonAsync<JsonElement>("/api/v1/notes", Ct);
        Assert.Contains(notes.EnumerateArray(), n => n.GetProperty("title").GetString() == "Kept");
        Assert.Equal(1, AuditCount("user.enable"));

        // Disabled isn't a way around blocked or pending.
        await _system.CreateUserAsync("b", "B", null, null, Ct);
        await _admin.SetStateAsync("b", UserStates.Blocked, Ct);
        Assert.Equal(HttpStatusCode.Conflict, (await Patch(boss, "b", new { disabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Patch(boss, "b", new { disabled = true })).StatusCode);
        Assert.Equal(UserStates.Blocked, (await _system.GetUserAsync("b", Ct))!.State);
    }

    [Fact]
    public async Task AFailingPatch_WritesNothing()
    {
        await SeedAdminAsync("boss");
        await _system.CreateUserAsync("p", "P", null, null, Ct);
        await _admin.SetStateAsync("p", UserStates.Pending, Ct);

        // The admin grant is refused (pending), so the quota in the same patch isn't written either.
        var resp = await Patch(As("boss"), "p", new { quotaBytes = 5, isAdmin = true });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Null((await _system.GetUserAsync("p", Ct))!.QuotaBytes);
        Assert.Equal(0, AuditCount("user.quota"));
    }

    [Fact]
    public async Task NonAdmin_IsForbidden_AndManageAnswersCarryNoContent()
    {
        const string Sentinel = "SENTINEL-MANAGE-91c2";
        await SeedAdminAsync("boss");
        await SeedUserAsync("plain");
        await SeedUserAsync("writer");
        var notes = new NoteRepository(_db, new TagRepository(_db));
        await notes.CreateAsync(ContextRef.User("writer"), "writer",
            new Note { Title = Sentinel, Content = Sentinel + " body" }, Ct);

        var plain = As("plain");
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(plain, "writer", new { disabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsJsonAsync("/api/v1/admin/users", new { username = "sneaky" }, Ct)).StatusCode);
        Assert.Equal(UserStates.Active, (await _system.GetUserAsync("writer", Ct))!.State);

        var boss = As("boss");
        foreach (var resp in new[]
        {
            await Patch(boss, "writer", new { quotaBytes = 0 }),
            await Patch(boss, "writer", new { isAdmin = true }),
            await Patch(boss, "writer", new { isAdmin = false }),
            await Patch(boss, "writer", new { disabled = true }),
            await Patch(boss, "writer", new { disabled = false }),
        })
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var text = await resp.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain(Sentinel, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"title\"", text);
            Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@", text);
        }
    }

    [Fact]
    public async Task EveryManageWrite_IsLoggedWithIdsOnly()
    {
        await SeedAdminAsync("boss");
        await SeedUserAsync("m");
        var boss = As("boss");
        await boss.PostAsJsonAsync("/api/v1/admin/users", new { username = "newbie", displayName = "Newbie Person" }, Ct);
        await Patch(boss, "m", new { quotaBytes = 42, isAdmin = true });
        await Patch(boss, "m", new { isAdmin = false, disabled = true });
        await Patch(boss, "m", new { disabled = false });

        using var db = _db.CreateSystemConnection();
        var actions = db.Query<string>("SELECT action FROM admin_audit ORDER BY at, id").ToList();
        Assert.Equal(new[] { "user.create", "user.quota", "user.make-admin", "user.remove-admin", "user.disable", "user.enable" }, actions);
        var dump = string.Join("|", db.Query<string>(
            "SELECT action || '/' || COALESCE(target_type,'') || '/' || COALESCE(target_id,'') FROM admin_audit"));
        Assert.DoesNotContain("newbie", dump, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/42", dump);
    }

    [Fact]
    public async Task NewMessage_SendsItsSubjectLine_ToALinkedChannel_Only()
    {
        await SeedAdminAsync("boss");
        await _system.CreateUserAsync("ada", "Ada Secretname", "ada@example.com", null, Ct);
        await _admin.SetStateAsync("ada", UserStates.Pending, Ct);
        await _system.CreateUserAsync("quiet", "Quiet", null, null, Ct);
        await _admin.SetStateAsync("quiet", UserStates.Pending, Ct);
        var channels = new NotificationChannelRepository(_db);
        await channels.UpsertAsync("ada", _bot.Name, "dm-ada", Ct);
        await channels.UpsertAsync("quiet", _bot.Name, "dm-quiet", Ct);
        await channels.SetEnabledAsync("quiet", _bot.Name, false, Ct);

        var boss = As("boss");
        Assert.Equal(HttpStatusCode.OK, (await boss.PostAsync("/api/v1/admin/users/ada/approve", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await boss.PostAsync("/api/v1/admin/users/quiet/approve", null, Ct)).StatusCode);

        var sent = await _bot.WaitForAsync("ada");
        Assert.Equal(SystemMessageNotifier.SubjectFor(MessageKinds.UserApproved), sent);
        Assert.DoesNotContain("Ada", sent);
        Assert.DoesNotContain("@", sent);
        await Task.Delay(300, Ct);
        Assert.False(_bot.Sent.ContainsKey("quiet"));
        // The admin has no channel: nothing, and nothing breaks.
        Assert.False(_bot.Sent.ContainsKey("boss"));
    }

    [Fact]
    public async Task AFailingBot_NeverBlocksTheMessage()
    {
        _bot.Fail = true;
        await SeedAdminAsync("boss");
        await _system.CreateUserAsync("ada", "Ada", null, null, Ct);
        await _admin.SetStateAsync("ada", UserStates.Pending, Ct);
        await new NotificationChannelRepository(_db).UpsertAsync("ada", _bot.Name, "dm-ada", Ct);

        Assert.Equal(HttpStatusCode.OK, (await As("boss").PostAsync("/api/v1/admin/users/ada/approve", null, Ct)).StatusCode);
        var inbox = await As("ada").GetFromJsonAsync<JsonElement>("/api/v1/messages", Ct);
        Assert.Single(inbox.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public void Subjects_AreFixedSentences_PerKind()
    {
        Assert.Contains("approval", SystemMessageNotifier.SubjectFor(MessageKinds.UserPending));
        Assert.Contains("storage", SystemMessageNotifier.SubjectFor("quota.warning"));
        Assert.Equal("Fishbowl: you have a new message.", SystemMessageNotifier.SubjectFor("something.new"));
    }

    private sealed class FakeBot : IBotClient
    {
        public string Name => "discord";
        public bool Fail { get; set; }
        public ConcurrentDictionary<string, string> Sent { get; } = new();

        public Task SendAsync(string userId, string message, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("chat platform down");
            Sent[userId] = message;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<IncomingMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async Task<string> WaitForAsync(string userId)
        {
            for (var i = 0; i < 100; i++)
            {
                if (Sent.TryGetValue(userId, out var m)) return m;
                await Task.Delay(50);
            }
            throw new TimeoutException("No message for " + userId);
        }
    }
}
