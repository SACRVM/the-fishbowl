using System.Net;
using System.Net.Http.Json;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Host.Tests;

// End-to-end exercise: admin resets a user's password to a temp value, the
// user logs in, gets blocked by the must-change branch, rotates, and finally
// signs in. Same fixture pattern as AdminImportTests so the cookie-style
// auth (TestAuthHandler) covers admin calls.
public class PasswordResetFlowTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _testDataDir;

    public PasswordResetFlowTests(WebApplicationFactory<Program> factory)
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "fishbowl_pwreset_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_testDataDir);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (dbDescriptor != null) services.Remove(dbDescriptor);
                services.AddSingleton<DatabaseFactory>(new DatabaseFactory(_testDataDir));

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.AuthenticationScheme, _ => { });
            });
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    private async Task<string> SeedAdminAsync(string id, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        await repo.CreateUserAsync(id, "Admin", "admin@local", null, ct);
        await repo.SetAdminAsync(id, isAdmin: true, ct);
        return id;
    }

    private async Task<string> SeedLocalUserAsync(
        string id, string username, string password, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        var hasher = scope.ServiceProvider.GetRequiredService<Fishbowl.Core.Auth.IPasswordHasher>();
        await repo.CreateUserAsync(id, username, null, null, ct);
        await repo.CreateUserMappingAsync(id, "local", username, ct);
        var hash = hasher.Hash(password);
        await repo.SetPasswordAsync(id, hash.Hash, hash.Salt, mustChange: false, ct);
        return id;
    }

    private HttpClient AdminClient(string adminId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", adminId);
        return client;
    }

    private HttpClient AnonClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    [Fact]
    public async Task Admin_ResetsPassword_ReturnsTempAndFlipsMustChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await SeedAdminAsync("admin-1", ct);
        var alice = await SeedLocalUserAsync("alice", "alice", "old-password-here", ct);

        var resp = await AdminClient(admin).PostAsync(
            $"/api/v1/admin/users/{alice}/reset-password", content: null, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ResetResult>(ct);
        Assert.NotNull(body);
        Assert.Equal(alice, body!.UserId);
        Assert.Equal(12, body.TempPassword.Length);
        Assert.True(body.MustChangeOnNextLogin);

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        var refreshed = await repo.GetUserAsync(alice, ct);
        Assert.True(refreshed!.MustChangePassword);
    }

    [Fact]
    public async Task Admin_ResetOnOAuthOnlyUser_RejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await SeedAdminAsync("admin-1", ct);

        // OAuth-only user — no local mapping, no password.
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
            await repo.CreateUserAsync("googler", "Googler", "g@google", null, ct);
            await repo.CreateUserMappingAsync("googler", "google", "google-sub-id", ct);
        }

        var resp = await AdminClient(admin).PostAsync(
            "/api/v1/admin/users/googler/reset-password", content: null, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_GetsForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLocalUserAsync("alice", "alice", "old-password-here", ct);
        // bob exists but is not admin
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
            await repo.CreateUserAsync("bob", "Bob", null, null, ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", "bob");

        var resp = await client.PostAsync("/api/v1/admin/users/alice/reset-password", null, ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Login_AfterReset_ReturnsMustChange_NoCookie()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await SeedAdminAsync("admin-1", ct);
        var alice = await SeedLocalUserAsync("alice", "alice", "old-password-here", ct);

        var reset = await AdminClient(admin).PostAsync(
            $"/api/v1/admin/users/{alice}/reset-password", null, ct);
        var resetBody = await reset.Content.ReadFromJsonAsync<ResetResult>(ct);
        var temp = resetBody!.TempPassword;

        var anon = AnonClient();
        var login = await anon.PostAsJsonAsync("/api/auth/login",
            new { Username = "alice", Password = temp }, ct);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginBody = await login.Content.ReadFromJsonAsync<MustChangeResponse>(ct);
        Assert.True(loginBody!.MustChangePassword);

        // Old (pre-reset) password no longer works.
        var oldLogin = await anon.PostAsJsonAsync("/api/auth/login",
            new { Username = "alice", Password = "old-password-here" }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        // Cookie was NOT issued — /api/v1/me should still 401.
        var me = await anon.GetAsync("/api/v1/me", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_AfterReset_ClearsFlag_AndSignsIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await SeedAdminAsync("admin-1", ct);
        var alice = await SeedLocalUserAsync("alice", "alice", "old-password-here", ct);

        var reset = await AdminClient(admin).PostAsync(
            $"/api/v1/admin/users/{alice}/reset-password", null, ct);
        var resetBody = await reset.Content.ReadFromJsonAsync<ResetResult>(ct);

        var anon = AnonClient();
        var change = await anon.PostAsJsonAsync("/api/auth/change-password",
            new
            {
                Username = "alice",
                CurrentPassword = resetBody!.TempPassword,
                NewPassword = "fresh-password-xyz"
            }, ct);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        // (Cookie issuance after sign-in is covered in LocalAuthFlowTests —
        // this fixture installs TestAuthHandler as the default scheme so the
        // admin-side X-Test-User-Id calls work, which means real cookies set
        // here aren't honoured by the test's auth pipeline. We assert the
        // post-conditions directly: flag cleared + new password is live.)

        // Flag is cleared.
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        var refreshed = await repo.GetUserAsync(alice, ct);
        Assert.False(refreshed!.MustChangePassword);

        // The new password is the live one — login with it returns 204
        // (no must-change flag, real cookie issued from the server's
        // perspective regardless of test-auth shim).
        var anon2 = AnonClient();
        var loginAgain = await anon2.PostAsJsonAsync("/api/auth/login",
            new { Username = "alice", Password = "fresh-password-xyz" }, ct);
        Assert.Equal(HttpStatusCode.NoContent, loginAgain.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_RejectsShortNew()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLocalUserAsync("alice", "alice", "old-password-here", ct);

        var resp = await AnonClient().PostAsJsonAsync("/api/auth/change-password",
            new { Username = "alice", CurrentPassword = "old-password-here", NewPassword = "tooshort" },
            ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_RejectsSameAsCurrent()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLocalUserAsync("alice", "alice", "old-password-here", ct);

        var resp = await AnonClient().PostAsJsonAsync("/api/auth/change-password",
            new { Username = "alice", CurrentPassword = "old-password-here", NewPassword = "old-password-here" },
            ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrent_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLocalUserAsync("alice", "alice", "old-password-here", ct);

        var resp = await AnonClient().PostAsJsonAsync("/api/auth/change-password",
            new { Username = "alice", CurrentPassword = "wrong-password!", NewPassword = "fresh-password-xyz" },
            ct);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    private sealed record ResetResult(string UserId, string TempPassword, bool MustChangeOnNextLogin, string Message);
    private sealed record MustChangeResponse(bool MustChangePassword, string? Username);
}
