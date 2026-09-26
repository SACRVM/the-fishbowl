using System.IO.Compression;
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

    // Export ZIPs (Files phase 3): the DB rides inside as personal.db / space.db.
    private static async Task<ZipArchive> ZipOf(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/zip", resp.Content.Headers.ContentType?.MediaType);
        var bytes = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
    }

    private async Task<List<string>> TitlesIn(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName);
        Assert.NotNull(entry);
        var restorePath = Path.Combine(_dataDir, "restored-" + Path.GetRandomFileName() + ".db");
        entry!.ExtractToFile(restorePath);
        try
        {
            var head = new byte[16];
            await using (var fs = File.OpenRead(restorePath))
                await fs.ReadExactlyAsync(head, TestContext.Current.CancellationToken);
            Assert.Equal("SQLite format 3", Encoding.ASCII.GetString(head, 0, 15));
            using var restored = new SqliteConnection($"Data Source={restorePath};Pooling=False");
            restored.Open();
            return (await restored.QueryAsync<string>("SELECT title FROM notes")).ToList();
        }
        finally
        {
            try { File.Delete(restorePath); } catch { }
        }
    }

    private async Task PutFileAsync(HttpClient client, string user, string root, string path, string content)
    {
        var req = Req(HttpMethod.Put, $"{root}/content?path={Uri.EscapeDataString(path)}&parents=1", user);
        req.Headers.Add("X-Fishbowl-Upload", "1");
        req.Headers.Add("If-None-Match", "*");
        req.Content = new StringContent(content);
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"PUT {path}: {resp.StatusCode}");
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
        var filename = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.NotNull(filename);
        Assert.StartsWith("fishbowl-", filename);
        Assert.EndsWith("-db.zip", filename);
        Assert.Equal("no-store", resp.Headers.CacheControl?.ToString());

        // The DB inside opens and the note is readable: the online backup is
        // a consistent snapshot, not a truncated in-flight file.
        using var zip = await ZipOf(resp);
        Assert.Single(zip.Entries);
        Assert.Contains("hello from export", await TitlesIn(zip, "personal.db"));
    }

    [Fact]
    public async Task Export_Files_And_All_Zips_Test()
    {
        var client = _factory.CreateClient();
        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/notes", UserA, new Note { Title = "note in all" }),
            TestContext.Current.CancellationToken);
        await PutFileAsync(client, UserA, "/api/v1/files", "Docs/readme.md", "# hello");
        await PutFileAsync(client, UserA, "/api/v1/files", "gone.txt", "bye");
        var trash = await client.SendAsync(Req(HttpMethod.Delete, "/api/v1/files?path=gone.txt", UserA),
            TestContext.Current.CancellationToken);
        Assert.True(trash.IsSuccessStatusCode);

        // Files only: the tree at the ZIP's root, no trash.
        using (var zip = await ZipOf(await client.SendAsync(Req(HttpMethod.Get, "/api/v1/export/files", UserA),
                   TestContext.Current.CancellationToken)))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("Docs/readme.md", names);
            Assert.DoesNotContain(names, n => n.Contains("gone.txt") || n.StartsWith(".trash"));
            using var r = new StreamReader(zip.GetEntry("Docs/readme.md")!.Open());
            Assert.Equal("# hello", await r.ReadToEndAsync(TestContext.Current.CancellationToken));
        }

        // Both: the DB plus files/.
        using (var zip = await ZipOf(await client.SendAsync(Req(HttpMethod.Get, "/api/v1/export/all", UserA),
                   TestContext.Current.CancellationToken)))
        {
            Assert.NotNull(zip.GetEntry("files/Docs/readme.md"));
            Assert.Contains("note in all", await TitlesIn(zip, "personal.db"));
        }

        var info = await (await client.SendAsync(Req(HttpMethod.Get, "/api/v1/export/info", UserA),
            TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(info.GetProperty("dbBytes").GetInt64() > 0);
        Assert.Equal(7, info.GetProperty("filesBytes").GetInt64());   // "# hello" — the trash doesn't count
        Assert.True(info.GetProperty("combinedAllowed").GetBoolean());
    }

    [Fact]
    public async Task Export_All_AboveCombinedMax_413_AndInfoSaysSo_Test()
    {
        var client = _factory.CreateClient();
        await PutFileAsync(client, UserB, "/api/v1/files", "big.txt", new string('x', 64));
        var config = new Fishbowl.Data.Repositories.SystemRepository(new DatabaseFactory(_dataDir));
        await config.SetConfigAsync("Export:CombinedMaxBytes", "10", TestContext.Current.CancellationToken);
        try
        {
            var resp = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/export/all", UserB),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
            var info = await (await client.SendAsync(Req(HttpMethod.Get, "/api/v1/export/info", UserB),
                TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);
            Assert.False(info.GetProperty("combinedAllowed").GetBoolean());
            // The separate downloads stay.
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req(HttpMethod.Get, "/api/v1/export/files", UserB),
                TestContext.Current.CancellationToken)).StatusCode);
        }
        finally
        {
            await config.SetConfigAsync("Export:CombinedMaxBytes", "", TestContext.Current.CancellationToken);
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
        using var zip = await ZipOf(respA);
        var titles = await TitlesIn(zip, "personal.db");
        Assert.Contains("a-only", titles);
        Assert.DoesNotContain("b-only", titles);
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
        var filename = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.NotNull(filename);
        Assert.Contains("space", filename!);
        Assert.Contains("export-team", filename);
        using var zip = await ZipOf(resp);
        Assert.Contains("space-note", await TitlesIn(zip, "space.db"));

        // The files and combined variants are owner-reachable too.
        await PutFileAsync(client, UserA, "/api/v1/spaces/export-team/files", "shared.txt", "hi");
        using var files = await ZipOf(await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/spaces/export-team/export/files", UserA), TestContext.Current.CancellationToken));
        Assert.NotNull(files.GetEntry("shared.txt"));
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

        foreach (var kind in new[] { "db", "files", "all", "info" })
        {
            var resp = await client.SendAsync(
                Req(HttpMethod.Get, $"/api/v1/spaces/member-team/export/{kind}", UserB),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
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
