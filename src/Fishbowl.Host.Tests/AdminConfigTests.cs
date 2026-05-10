using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Host.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// /api/v1/admin/config — admin-only read/write for a whitelisted slice of
// system_config. Covers: auth gates (cookie + IsAdmin), value validation,
// hot-vs-restart-required flag, secret redaction, the clear path, and
// the ConfigurationCache update on PUT.
public class AdminConfigTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _testDataDir;

    public AdminConfigTests(WebApplicationFactory<Program> factory)
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "fishbowl_admin_config_" + Path.GetRandomFileName());
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

    private async Task SeedAdminAsync(string userId, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        await repo.CreateUserAsync(userId, "Admin", "admin@local", null, ct);
        await repo.SetAdminAsync(userId, isAdmin: true, ct);
    }

    private async Task SeedNonAdminAsync(string userId, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        await repo.CreateUserAsync(userId, "User", "user@local", null, ct);
    }

    private HttpClient ClientAs(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", userId);
        return client;
    }

    [Fact]
    public async Task NonAdmin_Get_Forbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedNonAdminAsync("bob", ct);

        var resp = await ClientAs("bob").GetAsync("/api/v1/admin/config", ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task NoUser_Get_Forbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        // No X-Test-User-Id header.
        var resp = await _factory.CreateClient().GetAsync("/api/v1/admin/config", ct);
        Assert.True(resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_Get_ReturnsWhitelist()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAdminAsync("admin", ct);

        var resp = await ClientAs("admin").GetAsync("/api/v1/admin/config", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var rows = await resp.Content.ReadFromJsonAsync<List<ConfigRow>>(ct);
        Assert.NotNull(rows);
        var keys = rows!.Select(r => r.Key).ToList();
        Assert.Contains("Google:ClientId", keys);
        Assert.Contains("Google:ClientSecret", keys);
        Assert.Contains("Acme:Domains", keys);
        Assert.Contains("Acme:Email", keys);
        Assert.Contains("Acme:AcceptTos", keys);
        Assert.Contains("Discord:BotToken", keys);

        // No values set yet — isSet should be false everywhere.
        Assert.All(rows!, r => Assert.False(r.IsSet));
    }

    [Fact]
    public async Task Admin_Put_GoogleClientId_PersistsAndIsHot()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAdminAsync("admin", ct);

        var resp = await ClientAs("admin").PutAsJsonAsync(
            "/api/v1/admin/config/Google:ClientId",
            new { Value = "fishbowl-prod.apps.googleusercontent.com" },
            ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<UpdateResult>(ct);
        Assert.NotNull(body);
        Assert.False(body!.RestartRequired);

        // Persisted to DB
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        var stored = await repo.GetConfigAsync("Google:ClientId", ct);
        Assert.Equal("fishbowl-prod.apps.googleusercontent.com", stored);

        // Updated in cache too
        var cache = _factory.Services.GetRequiredService<ConfigurationCache>();
        Assert.Equal("fishbowl-prod.apps.googleusercontent.com", cache.Get("Google:ClientId"));
    }

    [Fact]
    public async Task Admin_Put_InvalidGoogleClientId_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAdminAsync("admin", ct);

        var resp = await ClientAs("admin").PutAsJsonAsync(
            "/api/v1/admin/config/Google:ClientId",
            new { Value = "not-a-real-client-id" },
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_Put_UnknownKey_NotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAdminAsync("admin", ct);

        var resp = await ClientAs("admin").PutAsJsonAsync(
            "/api/v1/admin/config/Secret:RootPassword",
            new { Value = "anything" },
            ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_Put_AcmeEmail_FlagsRestartRequired()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAdminAsync("admin", ct);

        var resp = await ClientAs("admin").PutAsJsonAsync(
            "/api/v1/admin/config/Acme:Email",
            new { Value = "ops@example.com" },
            ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<UpdateResult>(ct);
        Assert.True(body!.RestartRequired);
    }

    [Fact]
    public async Task Admin_Put_EmptyValue_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAdminAsync("admin", ct);

        var resp = await ClientAs("admin").PutAsJsonAsync(
            "/api/v1/admin/config/Google:ClientId",
            new { Value = "   " },
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_Get_AfterPutSecret_ReturnsRedacted()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAdminAsync("admin", ct);

        var put = await ClientAs("admin").PutAsJsonAsync(
            "/api/v1/admin/config/Google:ClientSecret",
            new { Value = "GOCSPX-very-long-secret-value-d3xQ" },
            ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await ClientAs("admin").GetAsync("/api/v1/admin/config", ct);
        var rows = await get.Content.ReadFromJsonAsync<List<ConfigRow>>(ct);
        var secret = rows!.Single(r => r.Key == "Google:ClientSecret");

        Assert.True(secret.IsSet);
        Assert.True(secret.Secret);
        Assert.NotNull(secret.Value);
        Assert.DoesNotContain("very-long-secret-value", secret.Value!);
        Assert.Contains("****", secret.Value!);
    }

    [Fact]
    public async Task Admin_Delete_ClearsValueAndCache()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAdminAsync("admin", ct);

        var put = await ClientAs("admin").PutAsJsonAsync(
            "/api/v1/admin/config/Discord:BotToken",
            new { Value = "MTIzNDU2Nzg5MA.AbCdEf.GhIjKlMnOpQrStUvWxYz0123456789abcdefghij" },
            ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var del = await ClientAs("admin").DeleteAsync("/api/v1/admin/config/Discord:BotToken", ct);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        // DB value cleared (empty string)
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        var stored = await repo.GetConfigAsync("Discord:BotToken", ct);
        Assert.True(string.IsNullOrEmpty(stored));

        // Cache cleared too (null, not empty)
        var cache = _factory.Services.GetRequiredService<ConfigurationCache>();
        Assert.Null(cache.Get("Discord:BotToken"));
    }

    [Fact]
    public async Task Admin_Put_AcmeDomains_BadHostname_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAdminAsync("admin", ct);

        var resp = await ClientAs("admin").PutAsJsonAsync(
            "/api/v1/admin/config/Acme:Domains",
            new { Value = "good.example.com,not a hostname,also.example.com" },
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_Put_Forbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedNonAdminAsync("bob", ct);

        var resp = await ClientAs("bob").PutAsJsonAsync(
            "/api/v1/admin/config/Google:ClientId",
            new { Value = "x.apps.googleusercontent.com" },
            ct);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // Verify nothing was written.
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        var stored = await repo.GetConfigAsync("Google:ClientId", ct);
        Assert.True(string.IsNullOrEmpty(stored));
    }

    private sealed record ConfigRow(
        string Key,
        string? Value,
        bool IsSet,
        bool Secret,
        bool RestartRequired,
        string? Description);

    private sealed record UpdateResult(string Key, bool Updated, bool RestartRequired);
}
