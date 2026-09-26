using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Auth;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// Admin phase A1 with the real auth schemes (no TestAuthHandler): Bearer
// tokens never reach messages or admin routes, a blocked account's key stops
// working at once, and the local login refuses blocked accounts and ends a
// live cookie session at its next request.
public class AdminAuthFlowTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dir;
    private readonly DatabaseFactory _db;
    private readonly SystemRepository _system;
    private readonly UserAdminRepository _admin;
    private readonly ApiKeyRepository _keys;

    public AdminAuthFlowTests(WebApplicationFactory<Program> factory)
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_admin_auth_" + Path.GetRandomFileName());
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
            });
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Bearer(string token)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> AdminKeyAsync()
    {
        await _system.CreateUserAsync("boss", "Boss", null, null, Ct);
        await _system.SetAdminAsync("boss", true, Ct);
        var issued = await _keys.IssueAsync("boss", ContextRef.User("boss"), "admin-key",
            new[] { "read:notes", "write:notes" }, Ct);
        return issued.RawToken;
    }

    [Fact]
    public async Task Bearer_IsForbidden_OnMessagesAndAdminRoutes()
    {
        var client = Bearer(await AdminKeyAsync());
        await _system.CreateUserAsync("p", "P", null, null, Ct);
        await _admin.SetStateAsync("p", UserStates.Pending, Ct);

        foreach (var path in new[] { "/api/v1/messages", "/api/v1/messages/unread-count", "/api/v1/admin/users", "/api/v1/admin/config" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path, Ct)).StatusCode);
        foreach (var action in new[] { "approve", "reject", "block", "unblock" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/admin/users/p/{action}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/messages/x/read", null, Ct)).StatusCode);
        // A2: managing accounts and settings is cookie-only too.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync("/api/v1/admin/users/p", new { quotaBytes = 1 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/admin/users", new { username = "viakey" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/v1/admin/config/Digest:Enabled", new { value = "true" }, Ct)).StatusCode);
        Assert.Null(await _system.GetUserByLocalUsernameAsync("viakey", Ct));
        Assert.Equal(UserStates.Pending, (await _system.GetUserAsync("p", Ct))!.State);
    }

    [Fact]
    public async Task BlockedAccount_KeyStopsWorking_Immediately()
    {
        await _system.CreateUserAsync("agent-owner", "Owner", null, null, Ct);
        var issued = await _keys.IssueAsync("agent-owner", ContextRef.User("agent-owner"), "k",
            new[] { "read:notes" }, Ct);
        var client = Bearer(issued.RawToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/notes", Ct)).StatusCode);

        await _admin.SetStateAsync("agent-owner", UserStates.Blocked, Ct);
        var resp = await client.GetAsync("/api/v1/notes", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("blocked", (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task LocalLogin_BlockedIsRefused_AndALiveSessionEnds()
    {
        const string Password = "a-long-enough-password";
        var hasher = _factory.Services.GetRequiredService<IPasswordHasher>();
        await _system.CreateUserAsync("local-1", "Local", null, null, Ct);
        await _system.CreateUserMappingAsync("local-1", "local", "localone", Ct);
        var hash = hasher.Hash(Password);
        await _system.SetPasswordAsync("local-1", hash.Hash, hash.Salt, ct: Ct);
        // Somebody else is already the admin, so no bootstrap happens.
        await _system.CreateUserAsync("boss", "Boss", null, null, Ct);
        await _system.SetAdminAsync("boss", true, Ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "localone", password = Password }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/notes", Ct)).StatusCode);
        Assert.NotNull((await _system.GetUserAsync("local-1", Ct))!.LastSignInAt);

        await _admin.SetStateAsync("local-1", UserStates.Blocked, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/notes", Ct)).StatusCode);
        // The cookie was dropped with that answer: now the request is anonymous.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/notes", Ct)).StatusCode);

        var fresh = _factory.CreateClient();
        var again = await fresh.PostAsJsonAsync("/api/auth/login", new { username = "localone", password = Password }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal("blocked", (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task LocalLogin_WithoutAnyAdmin_Bootstraps()
    {
        const string Password = "another-long-password";
        var hasher = _factory.Services.GetRequiredService<IPasswordHasher>();
        await _system.CreateUserAsync("solo", "Solo", null, null, Ct);
        await _system.CreateUserMappingAsync("solo", "local", "solo", Ct);
        var hash = hasher.Hash(Password);
        await _system.SetPasswordAsync("solo", hash.Hash, hash.Salt, ct: Ct);

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "solo", password = Password }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.True((await _system.GetUserAsync("solo", Ct))!.IsAdmin);
    }
}
