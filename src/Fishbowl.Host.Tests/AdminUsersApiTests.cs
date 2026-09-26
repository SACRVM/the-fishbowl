using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Models;
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

// Admin phase A1 over HTTP: the pending gate, approve / reject / block /
// unblock, the system inbox, the admin log, and the invariant that no admin
// route ever returns anybody's content.
public class AdminUsersApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dir;
    private readonly DatabaseFactory _db;
    private readonly SystemRepository _system;
    private readonly UserAdminRepository _admin;
    private readonly MessageRepository _messages;

    public AdminUsersApiTests(WebApplicationFactory<Program> factory)
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_admin_users_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _db = new DatabaseFactory(_dir);
        _system = new SystemRepository(_db);
        _admin = new UserAdminRepository(_db);
        _messages = new MessageRepository(_db);

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

    // A pending request exactly as AccountGate leaves it.
    private async Task SeedPendingAsync(string id)
    {
        await _system.CreateUserAsync(id, "Ada " + id, id + "@example.com", null, Ct);
        await _admin.SetStateAsync(id, UserStates.Pending, Ct);
        await _system.CreateUserMappingAsync(id, "google", "g-" + id, Ct);
        await _messages.CreateAsync(await _admin.ListAdminIdsAsync(Ct), MessageKinds.UserPending, "user", id, null, Ct);
    }

    private int AuditCount(string action)
    {
        using var db = _db.CreateSystemConnection();
        return db.ExecuteScalar<int>("SELECT COUNT(*) FROM admin_audit WHERE action = @action", new { action });
    }

    [Fact]
    public async Task Pending_ReachesOnlyMe_AndGetsNoStorage()
    {
        await SeedAdminAsync("boss");
        await SeedPendingAsync("waiting");
        var client = As("waiting");

        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct);
        Assert.Equal("pending", me.GetProperty("state").GetString());

        foreach (var path in new[] { "/api/v1/notes", "/api/v1/todos", "/api/v1/files/list?path=", "/api/v1/messages", "/api/v1/spaces" })
        {
            var resp = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal("pending", body.GetProperty("error").GetString());
        }
        var write = await client.PostAsJsonAsync("/api/v1/notes", new { title = "sneaky" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        var mcp = await client.PostAsync("/mcp", new StringContent("{}"), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, mcp.StatusCode);

        // Pages go to the waiting page, and the waiting page is served.
        var root = await client.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/pending", root.Headers.Location!.OriginalString);
        var pending = await client.GetAsync("/pending", Ct);
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Contains("waiting for approval", await pending.Content.ReadAsStringAsync(Ct));

        Assert.False(Directory.Exists(Path.Combine(_dir, "users", "waiting")));
    }

    [Fact]
    public async Task Approve_ActivatesWithQuota_TellsTheUser_AndResolvesForEveryAdmin()
    {
        await SeedAdminAsync("boss");
        await SeedAdminAsync("boss2");
        await SeedPendingAsync("ada");

        var resp = await As("boss").PostAsJsonAsync("/api/v1/admin/users/ada/approve",
            new { quotaBytes = 5L * 1024 * 1024 * 1024 }, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var user = await _system.GetUserAsync("ada", Ct);
        Assert.Equal(UserStates.Active, user!.State);
        Assert.Equal(5L * 1024 * 1024 * 1024, user.QuotaBytes);
        Assert.Equal("boss", user.ApprovedBy);

        // The account works now, storage and all.
        var ada = As("ada");
        Assert.Equal(HttpStatusCode.OK, (await ada.GetAsync("/api/v1/notes", Ct)).StatusCode);
        var inbox = await ada.GetFromJsonAsync<JsonElement>("/api/v1/messages", Ct);
        var item = Assert.Single(inbox.GetProperty("items").EnumerateArray());
        Assert.Equal(MessageKinds.UserApproved, item.GetProperty("kind").GetString());

        // Both admins see the request as done.
        foreach (var admin in new[] { "boss", "boss2" })
        {
            var list = await As(admin).GetFromJsonAsync<JsonElement>("/api/v1/messages", Ct);
            var msg = Assert.Single(list.GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.String, msg.GetProperty("doneAt").ValueKind);
            Assert.Equal("active", msg.GetProperty("subject").GetProperty("state").GetString());
        }

        // A second approval is a conflict, not a second log row.
        var twice = await As("boss2").PostAsync("/api/v1/admin/users/ada/approve", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal(1, AuditCount("user.approve"));
    }

    [Fact]
    public async Task Approve_WithoutBody_UsesTheDefault_AndMakeAdminWorks()
    {
        await SeedAdminAsync("boss");
        await SeedPendingAsync("bare");
        await SeedPendingAsync("helper");

        Assert.Equal(HttpStatusCode.OK, (await As("boss").PostAsync("/api/v1/admin/users/bare/approve", null, Ct)).StatusCode);
        Assert.Null((await _system.GetUserAsync("bare", Ct))!.QuotaBytes);

        var resp = await As("boss").PostAsJsonAsync("/api/v1/admin/users/helper/approve", new { makeAdmin = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((await _system.GetUserAsync("helper", Ct))!.IsAdmin);
        Assert.Equal(1, AuditCount("user.make-admin"));

        var bad = await As("boss").PostAsJsonAsync("/api/v1/admin/users/helper/approve", new { quotaBytes = -1 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Reject_DeletesTheShell_AndTheIdentityMayAskAgain()
    {
        await SeedAdminAsync("boss");
        await SeedPendingAsync("nope");

        var resp = await As("boss").PostAsync("/api/v1/admin/users/nope/reject", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Null(await _system.GetUserAsync("nope", Ct));
        Assert.Null(await _system.GetUserIdByMappingAsync("google", "g-nope", Ct));

        var list = await As("boss").GetFromJsonAsync<JsonElement>("/api/v1/messages", Ct);
        var msg = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.False(msg.GetProperty("subject").GetProperty("exists").GetBoolean());
        Assert.Equal(1, AuditCount("user.reject"));

        // Rejecting an active account is not a thing.
        Assert.Equal(HttpStatusCode.Conflict, (await As("boss").PostAsync("/api/v1/admin/users/boss/reject", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Block_EndsALiveSession_AtTheNextRequest_AndUnblockRestores()
    {
        await SeedAdminAsync("boss");
        await _system.CreateUserAsync("member", "Member", "m@example.com", null, Ct);
        var member = As("member");
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v1/notes", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await As("boss").PostAsync("/api/v1/admin/users/member/block", null, Ct)).StatusCode);

        var next = await member.GetAsync("/api/v1/notes", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, next.StatusCode);
        Assert.Equal("blocked", (await next.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        var page = await member.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith("/login?authError=", page.Headers.Location!.OriginalString);

        // Never approved (a pre-approval account) → back to pending, not active.
        var unblock = await As("boss").PostAsync("/api/v1/admin/users/member/unblock", null, Ct);
        Assert.Equal(HttpStatusCode.OK, unblock.StatusCode);
        Assert.Equal("pending", (await unblock.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("state").GetString());
        Assert.Equal(1, AuditCount("user.block"));
        Assert.Equal(1, AuditCount("user.unblock"));
    }

    [Fact]
    public async Task Block_Yourself_IsRefused_AndAPendingRequestResolves()
    {
        await SeedAdminAsync("boss");
        await SeedPendingAsync("spam");
        Assert.Equal(HttpStatusCode.BadRequest, (await As("boss").PostAsync("/api/v1/admin/users/boss/block", null, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await As("boss").PostAsync("/api/v1/admin/users/spam/block", null, Ct)).StatusCode);
        Assert.Equal(UserStates.Blocked, (await _system.GetUserAsync("spam", Ct))!.State);
        var list = await As("boss").GetFromJsonAsync<JsonElement>("/api/v1/messages", Ct);
        Assert.Equal(0, list.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task NonAdmin_GetsForbidden_OnEveryAdminRoute()
    {
        await SeedAdminAsync("boss");
        await _system.CreateUserAsync("plain", "Plain", null, null, Ct);
        await SeedPendingAsync("target");
        var plain = As("plain");

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/v1/admin/users", Ct)).StatusCode);
        foreach (var action in new[] { "approve", "reject", "block", "unblock" })
            Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"/api/v1/admin/users/target/{action}", null, Ct)).StatusCode);
        Assert.Equal(UserStates.Pending, (await _system.GetUserAsync("target", Ct))!.State);
    }

    [Fact]
    public async Task Messages_AreOwnOnly()
    {
        await SeedAdminAsync("boss");
        await _system.CreateUserAsync("other", "Other", null, null, Ct);
        await SeedPendingAsync("p");

        var theirs = await As("other").GetFromJsonAsync<JsonElement>("/api/v1/messages", Ct);
        Assert.Empty(theirs.GetProperty("items").EnumerateArray());

        var bossMsg = (await _messages.ListAsync("boss", ct: Ct)).Single();
        Assert.Equal(HttpStatusCode.NotFound, (await As("other").PostAsync($"/api/v1/messages/{bossMsg.Id}/read", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await As("boss").PostAsync($"/api/v1/messages/{bossMsg.Id}/read", null, Ct)).StatusCode);
        var count = await As("boss").GetFromJsonAsync<JsonElement>("/api/v1/messages/unread-count", Ct);
        Assert.Equal(0, count.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task AdminRoutes_NeverReturnContent()
    {
        const string Sentinel = "SENTINEL-CONTENT-7f3a";
        await SeedAdminAsync("boss");
        await _system.CreateUserAsync("writer", "Writer", "w@example.com", null, Ct);
        await SeedPendingAsync("p");
        var notes = new NoteRepository(_db, new TagRepository(_db));
        await notes.CreateAsync(ContextRef.User("writer"), "writer",
            new Note { Title = Sentinel, Content = Sentinel + " body", Tags = new() { Sentinel.ToLowerInvariant() } }, Ct);

        var boss = As("boss");
        foreach (var path in new[] { "/api/v1/admin/users", "/api/v1/admin/users/importable", "/api/v1/admin/config", "/api/v1/messages" })
        {
            var resp = await boss.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var text = await resp.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain(Sentinel, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"content\"", text);
            Assert.DoesNotContain("\"title\"", text);
            Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task EveryAdminWrite_IsOneLogRow_WithIdsOnly()
    {
        await SeedAdminAsync("boss");
        await SeedPendingAsync("a");
        await SeedPendingAsync("b");
        await SeedPendingAsync("c");
        var boss = As("boss");

        await boss.PostAsync("/api/v1/admin/users/a/approve", null, Ct);
        await boss.PostAsync("/api/v1/admin/users/b/reject", null, Ct);
        await boss.PostAsync("/api/v1/admin/users/c/block", null, Ct);
        await boss.PutAsJsonAsync("/api/v1/admin/config/Auth:SignUp", new { value = "closed" }, Ct);
        await boss.DeleteAsync("/api/v1/admin/config/Auth:SignUp", Ct);

        using var db = _db.CreateSystemConnection();
        var rows = db.Query<(string Actor, string Action, string? TargetId)>(
            "SELECT actor_id, action, target_id FROM admin_audit ORDER BY at, id").ToList();
        Assert.Equal(new[] { "user.approve", "user.reject", "user.block", "config.set", "config.clear" },
            rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal("boss", r.Actor));

        // No names, e-mail addresses or config values anywhere in the log.
        var dump = string.Join("|", db.Query<string>(
            "SELECT id || actor_id || action || COALESCE(target_type,'') || COALESCE(target_id,'') FROM admin_audit"));
        Assert.DoesNotContain("@", dump);
        Assert.DoesNotContain("Ada", dump);
        Assert.DoesNotContain("closed", dump);
    }

    [Fact]
    public async Task SignUpConfig_IsValidated()
    {
        await SeedAdminAsync("boss");
        var boss = As("boss");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await boss.PutAsJsonAsync("/api/v1/admin/config/Auth:SignUp", new { value = "whatever" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await boss.PutAsJsonAsync("/api/v1/admin/config/Auth:SignUp", new { value = "open" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await boss.PutAsJsonAsync("/api/v1/admin/config/Auth:AllowedEmailDomains", new { value = "not a domain" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await boss.PutAsJsonAsync("/api/v1/admin/config/Auth:AllowedEmailDomains", new { value = "family.example, @work.example" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await boss.PutAsJsonAsync("/api/v1/admin/config/Files:DefaultUserQuotaBytes", new { value = "-5" }, Ct)).StatusCode);

        var users = await boss.GetFromJsonAsync<JsonElement>("/api/v1/admin/users", Ct);
        Assert.Equal("open", users.GetProperty("signUp").GetString());
        Assert.True(users.GetProperty("defaultQuotaBytes").GetInt64() > 0);
    }
}
