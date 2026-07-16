using System.Net;
using Fishbowl.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// `/s/<slug>` is a short URL alias that 302s to the SPA space workspace
// (`/#/space/<slug>/notes`). The route is anonymous and unauthenticated;
// space-existence is deliberately checked downstream by the SPA so the
// redirect stays cheap and stateless.
public class SpaceShortcutTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _testDataDir;

    public SpaceShortcutTests(WebApplicationFactory<Program> factory)
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "fishbowl_space_shortcut_tests_" + Path.GetRandomFileName());
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
    public async Task SpaceShortcut_RedirectsToSpaceHashRoute()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/s/lighthouse", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/#/space/lighthouse/notes", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task SpaceShortcut_AnonymousAccess_NoLoginRedirect()
    {
        // The shortcut must not 401 or bounce to /login — it's a public
        // alias that the SPA's auth gate handles after the hash route mounts.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/s/anything", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/#/space/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task SpaceShortcut_EscapesSlugForUrl()
    {
        // Defence-in-depth: even though the slug regex on creation rules out
        // funny chars, we don't trust the route segment server-side and
        // URL-escape on the way out.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/s/has%20space", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // %20 round-trips to "has%20space" through Uri.EscapeDataString.
        Assert.Contains("has%20space", response.Headers.Location?.OriginalString);
    }
}
