using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// API-level pin for the secret vault v2 write gate
// (NoteRepository.EnforceLimits, docs/superpowers/specs/
// 2026-09-25-secret-vault-design.md): a non-v2 content_secret envelope is
// refused everywhere, and a space note can't carry a secret at all — there's
// no shared vault yet, so a hand-typed :::secret block is refused on save.
public class SecretGateApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dataDir;
    private const string UserA = "secretgate_user_a";

    public SecretGateApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_secretgate_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        var testFactory = new DatabaseFactory(_dataDir);
        using (var db = testFactory.CreateSystemConnection())
        {
            db.Execute(
                "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'A', 'a@a', @now)",
                new { id = UserA, now = DateTime.UtcNow.ToString("o") });
        }

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (dbDescriptor != null) services.Remove(dbDescriptor);
                services.AddSingleton<DatabaseFactory>(testFactory);

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

    private HttpRequestMessage Req(HttpMethod method, string path, string user, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(TestAuthHandler.UserIdHeader, user);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    [Fact]
    public async Task PostNote_V1ContentSecret_Returns400()
    {
        var client = _factory.CreateClient();
        // v1 envelope shape (no "v":2) — the pre-vault-v2 form, refused
        // outright regardless of context.
        var v1Blob = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            v = 1,
            blocks = new[] { Convert.ToBase64String(new byte[29]) },
        }));

        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/notes", UserA, new
            {
                title = "has a v1 secret",
                contentSecret = v1Blob,
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("contentSecret", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostSpaceNote_InlineSecretBlock_Returns400()
    {
        var client = _factory.CreateClient();

        var createSpace = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Secret Gate Space" }),
            TestContext.Current.CancellationToken);
        createSpace.EnsureSuccessStatusCode();

        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/spaces/secret-gate-space/notes", UserA, new
            {
                title = "has an inline secret",
                content = "before\n:::secret\nhunter2\n:::end\nafter",
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("hunter2", body);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }
}
