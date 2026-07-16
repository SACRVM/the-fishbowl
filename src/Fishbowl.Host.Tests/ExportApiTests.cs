using System.Net;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

public class ExportApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dataDir;
    private const string UserA = "export_user_a";
    private const string UserB = "export_user_b";

    public ExportApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_export_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        var testFactory = new DatabaseFactory(_dataDir);

        // Pre-seed users so space-creation FKs are satisfied (TestAuthHandler
        // only synthesises claims, not user rows).
        using (var db = testFactory.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute(
                "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[]
                {
                    new { id = UserA, n = "A", e = "a@a", now },
                    new { id = UserB, n = "B", e = "b@b", now },
                });
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

    private HttpRequestMessage Req(HttpMethod method, string path, string? user, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (user is not null) req.Headers.Add(TestAuthHandler.UserIdHeader, user);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    [Fact]
    public async Task Export_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/export/db", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Export_CookieUser_ReturnsValidSqliteDb()
    {
        var client = _factory.CreateClient();

        // Create a note so the DB has observable content we can assert on
        // after round-tripping through the download.
        var create = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/notes", UserA, new Note { Title = "hello from export" }),
            TestContext.Current.CancellationToken);
        create.EnsureSuccessStatusCode();

        var resp = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/export/db", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/vnd.sqlite3", resp.Content.Headers.ContentType?.MediaType);

        var filename = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.NotNull(filename);
        Assert.StartsWith("fishbowl-", filename);
        Assert.EndsWith(".db", filename);

        var bytes = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        // SQLite file format magic header — 16 bytes "SQLite format 3\0".
        Assert.True(bytes.Length >= 16, "Response too short to be a SQLite DB");
        var header = Encoding.ASCII.GetString(bytes, 0, 15);
        Assert.Equal("SQLite format 3", header);
        Assert.Equal(0, bytes[15]);

        // And the DB actually opens + the note we created is readable. This
        // round-trips the BackupDatabase call: proves the download is a
        // consistent snapshot, not a truncated in-flight file.
        var restorePath = Path.Combine(_dataDir, "restored-" + Path.GetRandomFileName() + ".db");
        await File.WriteAllBytesAsync(restorePath, bytes, TestContext.Current.CancellationToken);
        try
        {
            using var restored = new SqliteConnection($"Data Source={restorePath}");
            restored.Open();
            var titles = (await restored.QueryAsync<string>("SELECT title FROM notes")).ToList();
            Assert.Contains("hello from export", titles);
        }
        finally
        {
            try { File.Delete(restorePath); } catch { }
        }
    }

    [Fact]
    public async Task Export_IsolatedByUser()
    {
        var client = _factory.CreateClient();

        await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/notes", UserA, new Note { Title = "a-only" }),
            TestContext.Current.CancellationToken);
        await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/notes", UserB, new Note { Title = "b-only" }),
            TestContext.Current.CancellationToken);

        var respA = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/export/db", UserA),
            TestContext.Current.CancellationToken);
        respA.EnsureSuccessStatusCode();
        var bytesA = await respA.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        var restorePath = Path.Combine(_dataDir, "iso-" + Path.GetRandomFileName() + ".db");
        await File.WriteAllBytesAsync(restorePath, bytesA, TestContext.Current.CancellationToken);
        try
        {
            using var restored = new SqliteConnection($"Data Source={restorePath}");
            restored.Open();
            var titles = (await restored.QueryAsync<string>("SELECT title FROM notes")).ToList();
            Assert.Contains("a-only", titles);
            Assert.DoesNotContain("b-only", titles);
        }
        finally
        {
            try { File.Delete(restorePath); } catch { }
        }
    }

    [Fact]
    public async Task SpaceExport_Owner_ReturnsValidSqliteDb()
    {
        var client = _factory.CreateClient();

        // Create space and a note so there's content
        var createSpace = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Export Team" }),
            TestContext.Current.CancellationToken);
        createSpace.EnsureSuccessStatusCode();
        await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/spaces/export-team/notes", UserA,
                new Note { Title = "space-note" }),
            TestContext.Current.CancellationToken);

        var resp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/spaces/export-team/export/db", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var header = Encoding.ASCII.GetString(bytes, 0, 15);
        Assert.Equal("SQLite format 3", header);

        var filename = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.NotNull(filename);
        Assert.Contains("space", filename!);
        Assert.Contains("export-team", filename);
    }

    [Fact]
    public async Task SpaceExport_NonOwnerMember_403()
    {
        var client = _factory.CreateClient();

        var createSpace = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Member Team" }),
            TestContext.Current.CancellationToken);
        createSpace.EnsureSuccessStatusCode();

        // Promote UserB to plain member (still not owner).
        using (var sys = new DatabaseFactory(_dataDir).CreateSystemConnection())
        {
            var spaceId = sys.ExecuteScalar<string>(
                "SELECT id FROM spaces WHERE slug = 'member-team'");
            var now = DateTime.UtcNow.ToString("o");
            sys.Execute(
                "INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@t, @u, 'member', @j)",
                new { t = spaceId, u = UserB, j = now });
        }

        var resp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/spaces/member-team/export/db", UserB),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task SpaceExport_NonMember_403()
    {
        var client = _factory.CreateClient();

        await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Private" }),
            TestContext.Current.CancellationToken);

        var resp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/spaces/private/export/db", UserB),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task SpaceExport_UnknownSlug_404()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/spaces/ghost/export/db", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // Bearer-rejection for the export endpoint is covered in
    // ApiKeyAuthTests.Export_BearerToken_Returns403 — it needs the real auth
    // pipeline, and this fixture overrides the default auth scheme to
    // TestAuthHandler, which short-circuits Bearer into a 401 long before
    // the endpoint's cookie-only guard runs.

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }
}
