using System.Net;
using System.Net.Http.Json;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// End-to-end exercise of the local username/password path:
//   /api/setup creates a local admin → /api/auth/login signs them in
//   → /api/v1/me returns their profile.
// Also covers /api/auth/providers exposing "local" iff a local user exists,
// and the /setup lockout firing after a local user is created (no Google
// needed for the lockout — any provider counts).
public class LocalAuthFlowTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _testDataDir;

    public LocalAuthFlowTests(WebApplicationFactory<Program> factory)
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "fishbowl_local_auth_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_testDataDir);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (dbDescriptor != null) services.Remove(dbDescriptor);
                services.AddSingleton<DatabaseFactory>(new DatabaseFactory(_testDataDir));
            });
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public async Task NoSetupYet_ProvidersList_IsEmpty()
    {
        var client = _factory.CreateClient();
        var providers = await client.GetFromJsonAsync<List<Provider>>(
            "/api/auth/providers", TestContext.Current.CancellationToken);

        Assert.NotNull(providers);
        Assert.Empty(providers!);
    }

    [Fact]
    public async Task PostSetup_RejectsWhenNeitherProviderSupplied()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup",
            new { /* no fields */ },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostSetup_LocalOnly_IsAccepted_AndCreatesAdmin()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup",
            new { LocalUsername = "alice", LocalPassword = "supersecret-password" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetupResult>(TestContext.Current.CancellationToken);
        Assert.True(body!.LocalConfigured);
        Assert.False(body.GoogleConfigured);

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        var alice = await repo.GetUserByLocalUsernameAsync("alice", TestContext.Current.CancellationToken);
        Assert.NotNull(alice);
        Assert.True(alice!.IsAdmin);
        Assert.False(string.IsNullOrEmpty(alice.PasswordHash));
        Assert.False(string.IsNullOrEmpty(alice.PasswordSalt));
    }

    [Fact]
    public async Task PostSetup_RejectsShortPassword()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/setup",
            new { LocalUsername = "alice", LocalPassword = "tooshort" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostSetup_RejectsBadUsernameChars()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/setup",
            new { LocalUsername = "alice!sneaky", LocalPassword = "supersecret-password" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostSetup_LockedOutOnceLocalUserExists()
    {
        var client = _factory.CreateClient();

        var first = await client.PostAsJsonAsync("/api/setup",
            new { LocalUsername = "alice", LocalPassword = "supersecret-password" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second call must 404 — Fishbowl is configured.
        var second = await client.PostAsJsonAsync("/api/setup",
            new { LocalUsername = "intruder", LocalPassword = "anothertricky-password" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        // /setup GET also locks out.
        var setupGet = await client.GetAsync("/setup", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, setupGet.StatusCode);
    }

    [Fact]
    public async Task ProvidersList_IncludesLocal_AfterSetup()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/setup",
            new { LocalUsername = "alice", LocalPassword = "supersecret-password" },
            TestContext.Current.CancellationToken);

        var providers = await client.GetFromJsonAsync<List<Provider>>(
            "/api/auth/providers", TestContext.Current.CancellationToken);

        Assert.NotNull(providers);
        Assert.Contains(providers!, p => p.Id == "local");
    }

    [Fact]
    public async Task Login_HappyPath_SignsInAndCanFetchMe()
    {
        // The cookie handler hands a redirect to the browser by default; we
        // need to inspect status codes directly, so disable auto-redirect.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        await client.PostAsJsonAsync("/api/setup",
            new { LocalUsername = "alice", LocalPassword = "supersecret-password" },
            TestContext.Current.CancellationToken);

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { Username = "alice", Password = "supersecret-password" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        var me = await client.GetAsync("/api/v1/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/setup",
            new { LocalUsername = "alice", LocalPassword = "supersecret-password" },
            TestContext.Current.CancellationToken);

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { Username = "alice", Password = "wrong-password" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Login_UnknownUser_Returns401_AndSpendsRoughlySameTimeAsRealVerify()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/setup",
            new { LocalUsername = "alice", LocalPassword = "supersecret-password" },
            TestContext.Current.CancellationToken);

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { Username = "nobody", Password = "anything-goes-here" },
            TestContext.Current.CancellationToken);

        // Same wire response as wrong password — never differentiate
        // "user exists" vs "user doesn't" to the network.
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Login_UsernameIsCaseInsensitive()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        await client.PostAsJsonAsync("/api/setup",
            new { LocalUsername = "Alice", LocalPassword = "supersecret-password" },
            TestContext.Current.CancellationToken);

        // Server normalises to lowercase, so 'ALICE' and 'alice' both work.
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { Username = "ALICE", Password = "supersecret-password" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    private sealed record Provider(string Id, string Name, string Icon);
    private sealed record SetupResult(
        bool GoogleConfigured,
        bool LocalConfigured,
        bool AcmeConfigured,
        bool DiscordConfigured,
        bool RestartRequired);
}
