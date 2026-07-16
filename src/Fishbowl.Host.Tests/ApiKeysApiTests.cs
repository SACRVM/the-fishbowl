using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Fishbowl.Api.Endpoints;
using Fishbowl.Core;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Fishbowl.Host.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

public class ApiKeysApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _dbFactory;
    private readonly ApiKeyRepository _keyRepo;
    private readonly string _dataDir;
    private const string Alice = "keys_alice";
    private const string Bob = "keys_bob";

    public ApiKeysApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_keys_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        _dbFactory = new DatabaseFactory(_dataDir);
        _keyRepo = new ApiKeyRepository(_dbFactory);

        // Pre-seed both users so api_keys.user_id FK + space membership FK hold.
        using (var db = _dbFactory.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute(
                "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[]
                {
                    new { id = Alice, n = "A", e = "a@a", now },
                    new { id = Bob,   n = "B", e = "b@b", now },
                });
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
                        // Bearer requests skip the cookie-simulation and land
                        // on the real ApiKeyAuthenticationHandler so we can
                        // verify that /api/v1/keys rejects them with 403.
                        options.ForwardDefaultSelector = ctx =>
                        {
                            var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
                            if (auth is not null &&
                                auth.StartsWith("Bearer fb_", StringComparison.Ordinal))
                            {
                                return ApiKeyAuthenticationOptions.DefaultScheme;
                            }
                            return null;
                        };
                    });
            });
        });
    }

    private HttpRequestMessage Req(HttpMethod method, string path, string userId, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    [Fact]
    public async Task CreateKey_CookieAuth_ReturnsRawTokenOnce()
    {
        var client = _factory.CreateClient();

        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/keys", Alice, new
            {
                name = "Claude Code test",
                contextType = "user",
                contextId = (string?)null,
                scopes = new[] { "read:notes", "write:notes" },
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<ApiKeysApi.CreatedKeyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.StartsWith("fb_live_", created!.RawToken);
        Assert.Equal(created.KeyPrefix, created.RawToken.Substring(0, 12));
        Assert.Equal("Claude Code test", created.Name);
        Assert.Equal("user", created.ContextType);
        Assert.Equal(Alice, created.ContextId);
        Assert.Equal(2, created.Scopes.Count);

        // List response must never echo the raw token.
        var listResp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/keys", Alice),
            TestContext.Current.CancellationToken);
        listResp.EnsureSuccessStatusCode();
        var raw = await listResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(created.RawToken, raw);
    }

    [Fact]
    public async Task CreateKey_UserContext_AlwaysBindsToCaller()
    {
        // Alice requests a 'user'-scoped key with Bob's id as contextId —
        // the server MUST ignore the attempted impersonation and bind to
        // Alice.
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/keys", Alice, new
            {
                name = "impersonation attempt",
                contextType = "user",
                contextId = Bob,
                scopes = new[] { "read:notes" },
            }),
            TestContext.Current.CancellationToken);

        var created = await resp.Content.ReadFromJsonAsync<ApiKeysApi.CreatedKeyResponse>(
            TestContext.Current.CancellationToken);
        Assert.Equal(Alice, created!.ContextId);
    }

    [Fact]
    public async Task CreateKey_SpaceContext_NonMember_403()
    {
        // Alice owns a space; Bob is not a member. Bob cannot mint a space
        // key for it.
        using (var db = _dbFactory.CreateSystemConnection())
        {
            var spaceRepo = new SpaceRepository(_dbFactory);
            await spaceRepo.CreateAsync(Alice, "Alice Space", TestContext.Current.CancellationToken);
        }

        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/keys", Bob, new
            {
                name = "steal",
                contextType = "space",
                contextId = "alice-space",
                scopes = new[] { "read:notes" },
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task CreateKey_SpaceContext_UnknownSpace_404()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/keys", Alice, new
            {
                name = "ghost",
                contextType = "space",
                contextId = "does-not-exist",
                scopes = new[] { "read:notes" },
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task CreateKey_MissingScopes_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/keys", Alice, new
            {
                name = "no-scope",
                contextType = "user",
                contextId = (string?)null,
                scopes = Array.Empty<string>(),
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateKey_UnknownScope_400_WithUnknownList()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/keys", Alice, new
            {
                name = "typo-scope",
                contextType = "user",
                contextId = (string?)null,
                scopes = new[] { "read:notes", "read:nots", "delete:everything" },
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // The response must surface the bad strings so the user can fix
        // them, but should also list the valid ones for discoverability.
        Assert.Contains("read:nots", body);
        Assert.Contains("delete:everything", body);
        Assert.Contains("read:notes", body); // present in the "valid" list
        Assert.DoesNotContain("fb_live_", body); // no token leaked into the failure path
    }

    [Fact]
    public async Task ListKeys_ReturnsOnlyCallersKeys_WithoutHash()
    {
        // Names must be distinctive enough not to collide with random chars
        // in a generated key prefix (e.g. "fb_live_9b1u" would match "b1").
        // Use hyphenated strings that can't appear inside a hex-ish prefix.
        const string aliceName = "alice-owned-key";
        const string bobName = "bob-owned-key";
        await _keyRepo.IssueAsync(Alice, ContextRef.User(Alice), aliceName,
            new[] { "read:notes" }, TestContext.Current.CancellationToken);
        await _keyRepo.IssueAsync(Bob, ContextRef.User(Bob), bobName,
            new[] { "read:notes" }, TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/keys", Alice),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(aliceName, body);
        Assert.DoesNotContain(bobName, body);
        Assert.DoesNotContain("keyHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key_hash", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteKey_Owner_204_ThenOmittedFromList()
    {
        var issued = await _keyRepo.IssueAsync(Alice, ContextRef.User(Alice), "doomed",
            new[] { "read:notes" }, TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        var del = await client.SendAsync(
            Req(HttpMethod.Delete, $"/api/v1/keys/{issued.Record.Id}", Alice),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var list = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/keys", Alice),
            TestContext.Current.CancellationToken);
        var body = await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("doomed", body);
    }

    [Fact]
    public async Task DeleteKey_NotOwner_404()
    {
        var issued = await _keyRepo.IssueAsync(Alice, ContextRef.User(Alice), "alices",
            new[] { "read:notes" }, TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Delete, $"/api/v1/keys/{issued.Record.Id}", Bob),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task BearerAuth_OnKeysEndpoints_403()
    {
        // Mint a key, then try to use it to list/create/revoke — all must
        // 403. Key management is cookie-only: a token must never mint more
        // tokens or revoke itself.
        var issued = await _keyRepo.IssueAsync(Alice, ContextRef.User(Alice), "self",
            new[] { "read:notes", "write:notes" }, TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", issued.RawToken);

        var listResp = await client.GetAsync("/api/v1/keys", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, listResp.StatusCode);

        var createResp = await client.PostAsJsonAsync("/api/v1/keys", new
        {
            name = "from-bearer",
            contextType = "user",
            contextId = (string?)null,
            scopes = new[] { "read:notes" },
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, createResp.StatusCode);

        var delResp = await client.DeleteAsync($"/api/v1/keys/{issued.Record.Id}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, delResp.StatusCode);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }
}
