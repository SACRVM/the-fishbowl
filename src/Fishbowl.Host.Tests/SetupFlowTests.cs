using System.Net;
using System.Net.Http.Json;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

public class SetupFlowTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _testDataDir;

    public SetupFlowTests(WebApplicationFactory<Program> factory)
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "fishbowl_setup_tests_" + Path.GetRandomFileName());
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
    public async Task Setup_Returns404_WhenConfigured_Test()
    {
        // Seed config so /setup should lock out
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
            var cache = scope.ServiceProvider.GetRequiredService<Fishbowl.Host.Configuration.ConfigurationCache>();
            await repo.SetConfigAsync("Google:ClientId", "already.apps.googleusercontent.com", TestContext.Current.CancellationToken);
            await repo.SetConfigAsync("Google:ClientSecret", "already-configured-secret-yay", TestContext.Current.CancellationToken);
            cache.Set("Google:ClientId", "already.apps.googleusercontent.com");
            cache.Set("Google:ClientSecret", "already-configured-secret-yay");
        }

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var getResponse = await client.GetAsync("/setup", TestContext.Current.CancellationToken);
        var postResponse = await client.PostAsJsonAsync("/api/setup",
            new { ClientId = "x.apps.googleusercontent.com", ClientSecret = "whatever-valid-length-here-ok" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, postResponse.StatusCode);
    }

    [Fact]
    public async Task PostSetup_Rejects_InvalidClientIdFormat_Test()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup",
            new { ClientId = "not-a-google-id", ClientSecret = "some-valid-length-secret-here" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostSetup_Rejects_EmptyClientSecret_Test()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup",
            new { ClientId = "valid.apps.googleusercontent.com", ClientSecret = "" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostSetup_Rejects_DiscordTokenTooShort_Test()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup",
            new
            {
                ClientId = "valid.apps.googleusercontent.com",
                ClientSecret = "some-valid-length-secret-here",
                DiscordBotToken = "short.dot.value",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostSetup_Rejects_DiscordTokenWithoutDotSeparators_Test()
    {
        var client = _factory.CreateClient();

        // 70 chars but no dots — fails the structural check.
        var bogus = new string('a', 70);
        var response = await client.PostAsJsonAsync("/api/setup",
            new
            {
                ClientId = "valid.apps.googleusercontent.com",
                ClientSecret = "some-valid-length-secret-here",
                DiscordBotToken = bogus,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostSetup_PersistsDiscordToken_AndSignalsRestart_Test()
    {
        var client = _factory.CreateClient();

        // 70-char synthetic token with two dots — passes the structural sniff
        // tests. We don't actually log into Discord during the unit run; the
        // hosted service no-ops on bad tokens at runtime.
        var fakeToken = new string('a', 23) + "." + new string('b', 6) + "." + new string('c', 39);

        var response = await client.PostAsJsonAsync("/api/setup",
            new
            {
                ClientId = "valid.apps.googleusercontent.com",
                ClientSecret = "some-valid-length-secret-here",
                DiscordBotToken = fakeToken,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SetupResult>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.True(body!.DiscordConfigured);
        Assert.True(body.RestartRequired);

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        Assert.Equal(fakeToken,
            await repo.GetConfigAsync("Discord:BotToken", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PostSetup_WithoutDiscordToken_DoesNotMarkConfigured_Test()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup",
            new
            {
                ClientId = "valid.apps.googleusercontent.com",
                ClientSecret = "some-valid-length-secret-here",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SetupResult>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.False(body!.DiscordConfigured);

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        Assert.Null(await repo.GetConfigAsync("Discord:BotToken", TestContext.Current.CancellationToken));
    }

    private sealed record SetupResult(bool AcmeConfigured, bool DiscordConfigured, bool RestartRequired);
}
