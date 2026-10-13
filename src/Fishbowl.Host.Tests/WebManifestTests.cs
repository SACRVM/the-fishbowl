using System.Net;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// The web app manifest: what a browser reads to install Fishbowl as an app
// (name, icon, window). Fetched without a session, like the icon.
public class WebManifestTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _testDataDir;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public WebManifestTests(WebApplicationFactory<Program> factory)
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "fishbowl_manifest_tests_" + Path.GetRandomFileName());
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
    public async Task Manifest_IsServedWithoutASession_AsAManifest_Test()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/manifest.webmanifest", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = manifest.RootElement;
        Assert.Equal("The Fishbowl", root.GetProperty("name").GetString());
        Assert.Equal("/", root.GetProperty("start_url").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        // Every icon it names is there, as what it says it is: a browser
        // that can't load one doesn't offer the install.
        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        Assert.Contains(icons, i => i.GetProperty("sizes").GetString() == "512x512" && i.GetProperty("purpose").GetString() == "any");
        Assert.Contains(icons, i => i.GetProperty("purpose").GetString() == "maskable");
        foreach (var icon in icons)
        {
            var file = await client.GetAsync(icon.GetProperty("src").GetString(), Ct);
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal(icon.GetProperty("type").GetString(), file.Content.Headers.ContentType?.MediaType);
        }
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("login.html")]
    public async Task Pages_LinkTheManifest_Test(string page)
    {
        var resources = _factory.Services.GetRequiredService<IResourceProvider>();

        var resource = await resources.GetAsync(page, Ct);

        Assert.NotNull(resource);
        Assert.Contains("<link rel=\"manifest\" href=\"/manifest.webmanifest\">",
            System.Text.Encoding.UTF8.GetString(resource.Data));
    }
}
