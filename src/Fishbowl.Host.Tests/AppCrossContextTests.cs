using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Fishbowl.Api.Endpoints;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Fishbowl.Host.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// Cross-context isolation tests for the Apps platform.
// Verifies: app-key A cannot reach app B, personal-app key cannot reach
// team-app, deleted-app key returns 404, and app key cannot reach notes.
public class AppCrossContextTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _dbFactory;
    private readonly ApiKeyRepository _keys;
    private readonly string _dataDir;
    private const string Alice = "xctx_alice";

    public AppCrossContextTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_xctx_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        _dbFactory = new DatabaseFactory(_dataDir);
        _keys = new ApiKeyRepository(_dbFactory);
        using (var db = _dbFactory.CreateSystemConnection())
        {
            db.Execute(
                "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'Alice', 'a@a', @now)",
                new { id = Alice, now = DateTime.UtcNow.ToString("o") });
        }

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton<DatabaseFactory>(_dbFactory);

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.AuthenticationScheme, options =>
                    {
                        options.ForwardDefaultSelector = ctx =>
                        {
                            var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
                            if (auth is not null && auth.StartsWith("Bearer fb_", StringComparison.Ordinal))
                                return ApiKeyAuthenticationOptions.DefaultScheme;
                            return null;
                        };
                    });
            });
        });
    }

    private HttpRequestMessage CookieReq(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(TestAuthHandler.UserIdHeader, Alice);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private async Task<(AppsApi.AppSummary App, string Bearer)> CreateAppAndMintKeyAsync(
        string ownerType, string ownerId, string slug,
        IEnumerable<string>? scopes = null)
    {
        var client = _factory.CreateClient();

        var createResp = await client.SendAsync(
            CookieReq(HttpMethod.Post, "/api/v1/apps", new
            {
                slug,
                name = slug,
                owner = new { type = ownerType, id = ownerId },
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var app = await createResp.Content.ReadFromJsonAsync<AppsApi.AppSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(app);

        var keyResp = await client.SendAsync(
            CookieReq(HttpMethod.Post, $"/api/v1/apps/{ownerType}/{ownerId}/{slug}/keys", new
            {
                name = $"key-{slug}",
                scopes = scopes?.ToArray()
                    ?? new[] { ScopeCatalog.AppRead, ScopeCatalog.AppWrite, ScopeCatalog.AppAdmin },
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, keyResp.StatusCode);
        var key = await keyResp.Content.ReadFromJsonAsync<AppsApi.CreateAppKeyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(key);

        return (app!, key!.RawToken);
    }

    private HttpClient BearerClient(string token)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    // Key for App-A cannot reach App-B's data plane — ResolveAppAsync sees the
    // AppRef mismatch (token's appId ≠ URL's app row id) and returns 403.
    [Fact]
    public async Task BearerKeyForAppA_CannotReachAppB_Returns403()
    {
        var (_, aBearer) = await CreateAppAndMintKeyAsync("user", Alice, "xctx-app-a");
        await CreateAppAndMintKeyAsync("user", Alice, "xctx-app-b");

        var resp = await BearerClient(aBearer).SendAsync(
            new HttpRequestMessage(HttpMethod.Get,
                $"/api/v1/apps/user/{Alice}/xctx-app-b/tables"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // Personal-app Bearer key has ownerType=user; hitting the team-app route
    // causes a claim mismatch on the team owner_id — 403.
    [Fact]
    public async Task BearerKeyForPersonalApp_CannotReachTeamApp_Returns403()
    {
        var ct = TestContext.Current.CancellationToken;

        var teams = new TeamRepository(_dbFactory);
        var team = await teams.CreateAsync(Alice, "xctx-team", ct);

        var (_, personalBearer) = await CreateAppAndMintKeyAsync("user", Alice, "xctx-personal");
        await CreateAppAndMintKeyAsync("team", team.Slug, "xctx-team-app");

        var resp = await BearerClient(personalBearer).SendAsync(
            new HttpRequestMessage(HttpMethod.Get,
                $"/api/v1/apps/team/{team.Slug}/xctx-team-app/tables"),
            ct);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // After the app is deleted (cookie DELETE), the orphan Bearer key still
    // authenticates (row in api_keys still exists) but ResolveAppAsync can
    // no longer find the app row — returns 404.
    [Fact]
    public async Task BearerKeyOnDeletedApp_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, bearer) = await CreateAppAndMintKeyAsync("user", Alice, "xctx-doomed");

        var cookieClient = _factory.CreateClient();
        var delResp = await cookieClient.SendAsync(
            CookieReq(HttpMethod.Delete,
                $"/api/v1/apps/user/{Alice}/xctx-doomed?confirm=xctx-doomed"),
            ct);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        SqliteConnection.ClearAllPools();

        var resp = await BearerClient(bearer).SendAsync(
            new HttpRequestMessage(HttpMethod.Get,
                $"/api/v1/apps/user/{Alice}/xctx-doomed/tables"),
            ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // App-scoped Bearer key carries only app:* scopes; it has no read:notes
    // claim and the notes endpoint must reject it with 403 (scope gate fires
    // before any data access).
    [Fact]
    public async Task BearerKeyWithAppScope_CannotReachNotesEndpoints_Returns403()
    {
        var (_, appBearer) = await CreateAppAndMintKeyAsync("user", Alice, "xctx-notes-check");

        var resp = await BearerClient(appBearer).GetAsync(
            "/api/v1/notes",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    public void Dispose()
    {
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }
}
