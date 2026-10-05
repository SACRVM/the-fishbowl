using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// OAuth for MCP clients, the anonymous edges: registration's cap counts only
// clients in use (unused ones expire after a day), and a code becomes a key
// only for an account that may still sign in.
public class OAuthHardeningTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Callback = "https://claude.ai/api/mcp/auth_callback";
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dir;
    private readonly DatabaseFactory _db;
    private readonly OAuthRepository _oauth;
    private readonly SystemRepository _system;

    public OAuthHardeningTests(WebApplicationFactory<Program> factory)
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_oauth_hardening_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _db = new DatabaseFactory(_dir);
        _oauth = new OAuthRepository(_db);
        _system = new SystemRepository(_db);
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

    private void InsertClients(int count, DateTime createdAt)
    {
        using var sys = _db.CreateSystemConnection();
        sys.Execute("INSERT INTO oauth_clients(client_id, name, redirect_uris, created_at) VALUES (@id, 'Spam', '[]', @at)",
            Enumerable.Range(0, count).Select(i => new { id = $"fbc_spam_{createdAt.Ticks}_{i}", at = createdAt.ToString("o") }));
    }

    private long Rows(string sql)
    {
        using var sys = _db.CreateSystemConnection();
        return sys.ExecuteScalar<long>(sql);
    }

    private Task<HttpResponseMessage> Register() =>
        _factory.CreateClient().PostAsJsonAsync("/oauth/register",
            new { client_name = "Claude", redirect_uris = new[] { Callback } }, Ct);

    // ── Registration ─────────────────────────────────────────────────────

    [Fact]
    public async Task Register_UnusedClientsOlderThanADay_DontCount_AndGo()
    {
        InsertClients(1000, DateTime.UtcNow.AddDays(-2));

        var resp = await Register();

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal(1, Rows("SELECT COUNT(*) FROM oauth_clients"));
    }

    [Fact]
    public async Task Register_ClientsInUse_StillFillTheCap()
    {
        InsertClients(Fishbowl.Api.Endpoints.OAuthApi.MaxClients, DateTime.UtcNow.AddMinutes(-5));

        var resp = await Register();

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_client_metadata", (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AClientThatCompletedAnExchange_StaysInUse_ByItsNewestUsedCode()
    {
        InsertClients(1, DateTime.UtcNow.AddDays(-3));
        string clientId;
        using (var sys = _db.CreateSystemConnection())
        {
            clientId = sys.ExecuteScalar<string>("SELECT client_id FROM oauth_clients")!;
            var old = DateTime.UtcNow.AddDays(-3);
            sys.Execute(@"INSERT INTO oauth_codes(code_hash, client_id, user_id, context_type, context_id, scopes, redirect_uri, code_challenge, expires_at, used)
                          VALUES (@h, @clientId, 'u', 'user', 'u', '[]', @cb, 'x', @at, 1)",
                new[]
                {
                    new { h = "older", clientId, cb = Callback, at = old.ToString("o") },
                    new { h = "newer", clientId, cb = Callback, at = old.AddHours(1).ToString("o") },
                });
        }

        // Saving any code tidies the expired ones: only the newest used one stays.
        await _oauth.SaveCodeAsync(new OAuthCode("fresh", "fbc_other", "u", "user", "u", new[] { "read:notes" }, Callback,
            new string('c', 43), DateTime.UtcNow.AddMinutes(5)), Ct);
        Assert.Equal(0, Rows("SELECT COUNT(*) FROM oauth_codes WHERE code_hash = 'older'"));
        Assert.Equal(1, Rows("SELECT COUNT(*) FROM oauth_codes WHERE code_hash = 'newer'"));

        Assert.Equal(HttpStatusCode.Created, (await Register()).StatusCode);
        Assert.Equal(2, await _oauth.CountClientsAsync(Ct));   // the old client is still in use
    }

    // ── Token ────────────────────────────────────────────────────────────

    private async Task<(string Code, string Verifier, string ClientId)> CodeForAsync(string userId)
    {
        var client = await _oauth.RegisterClientAsync("Claude", new[] { Callback }, Ct);
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await _oauth.SaveCodeAsync(new OAuthCode(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))), client.ClientId, userId, "user", userId,
            new[] { "read:notes" }, Callback, challenge, DateTime.UtcNow.AddMinutes(5)), Ct);
        return (code, verifier, client.ClientId);
    }

    private async Task<HttpResponseMessage> Token((string Code, string Verifier, string ClientId) grant) =>
        await _factory.CreateClient().PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = grant.Code,
            ["redirect_uri"] = Callback,
            ["client_id"] = grant.ClientId,
            ["code_verifier"] = grant.Verifier,
        }), Ct);

    [Fact]
    public async Task Token_ForAnActiveAccount_IsAKey()
    {
        await _system.CreateUserAsync("ok-user", "Ok", null, null, Ct);
        var resp = await Token(await CodeForAsync("ok-user"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Token_ACodeFromBeforeADisable_GivesNoKey()
    {
        await _system.CreateUserAsync("later-disabled", "L", null, null, Ct);
        var grant = await CodeForAsync("later-disabled");
        using (var sys = _db.CreateSystemConnection())   // as if disabled between the code and the exchange
            sys.Execute("UPDATE users SET state = 'disabled' WHERE id = 'later-disabled'");

        var resp = await Token(grant);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_grant", (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Equal(0, Rows("SELECT COUNT(*) FROM api_keys WHERE user_id = 'later-disabled'"));
    }

    [Fact]
    public async Task Token_ForADeletedAccount_IsAnOAuthError_NotA500()
    {
        var resp = await Token(await CodeForAsync("never-was"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_grant", (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task DisablingOrBlocking_DropsTheAccountsOpenCodes()
    {
        await _system.CreateUserAsync("to-block", "B", null, null, Ct);
        await CodeForAsync("to-block");
        Assert.Equal(1, Rows("SELECT COUNT(*) FROM oauth_codes WHERE user_id = 'to-block'"));

        Assert.True(await new UserAdminRepository(_db).SetStateAsync("to-block", UserStates.Blocked, Ct));

        Assert.Equal(0, Rows("SELECT COUNT(*) FROM oauth_codes WHERE user_id = 'to-block'"));
    }
}
