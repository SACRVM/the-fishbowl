using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// Files phase 3 over HTTP: DELETE /spaces/{slug}?archive=… and the archived
// spaces surface (/api/v1/archive/spaces). Bearer 403 is covered in
// ApiKeyAuthTests (it needs the real auth pipeline).
public class SpaceArchiveApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dataDir;
    private readonly DatabaseFactory _db;
    private const string Owner = "archive_owner";
    private const string Member = "archive_member";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SpaceArchiveApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_archive_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _db = new DatabaseFactory(_dataDir);
        using (var sys = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            sys.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @id, @e, @now)",
                new[] { new { id = Owner, e = "o@o", now }, new { id = Member, e = "m@m", now } });
        }
        new SystemRepository(_db).SetConfigAsync("Files:MinFreeBytes", "0").GetAwaiter().GetResult();
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var d = services.SingleOrDefault(x => x.ServiceType == typeof(DatabaseFactory));
                if (d != null) services.Remove(d);
                services.AddSingleton(_db);
                services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    o.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    o.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.AuthenticationScheme, _ => { });
            });
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private static HttpRequestMessage Req(HttpMethod method, string path, string user, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(TestAuthHandler.UserIdHeader, user);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private async Task<JsonElement> CreateSpaceAsync(HttpClient client, string name)
    {
        var resp = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces", Owner, new { name }), Ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task PutAsync(HttpClient client, string slug, string path, string text)
    {
        var req = Req(HttpMethod.Put, $"/api/v1/spaces/{slug}/files/content?path={Uri.EscapeDataString(path)}&parents=1", Owner);
        req.Headers.Add("X-Fishbowl-Upload", "1");
        req.Headers.Add("If-None-Match", "*");
        req.Content = new StringContent(text);
        (await client.SendAsync(req, Ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Delete_WithArchive_ListDownloadRestore_RoundTrip_Test()
    {
        var client = _factory.CreateClient();
        var space = await CreateSpaceAsync(client, "Round Trip");
        var slug = space.GetProperty("slug").GetString()!;
        var id = space.GetProperty("id").GetString()!;
        await client.SendAsync(Req(HttpMethod.Post, $"/api/v1/spaces/{slug}/notes", Owner, new { title = "kept in the archive" }), Ct);
        await PutAsync(client, slug, "docs/a.txt", "archived bytes");
        using (var sys = _db.CreateSystemConnection())
            sys.Execute("INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@id, @m, 'member', @now)",
                new { id, m = Member, now = DateTime.UtcNow.ToString("o") });
        await new ApiKeyRepository(_db).IssueAsync(Owner, ContextRef.Space(slug), "space key", new[] { "read:notes" }, Ct);

        // A member can't delete (and nothing gets archived).
        var denied = await client.SendAsync(Req(HttpMethod.Delete, $"/api/v1/spaces/{slug}", Member), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(await ListAsync(client, Owner));

        var del = await client.SendAsync(Req(HttpMethod.Delete, $"/api/v1/spaces/{slug}?archive=true", Owner), Ct);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var archiveId = (await del.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        Assert.False(Directory.Exists(Path.Combine(_db.SpacesRoot, id)));
        using (var sys = _db.CreateSystemConnection())
            Assert.Equal(0, sys.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM api_keys WHERE context_id IN (@id, @slug)", new { id, slug }));

        var list = await ListAsync(client, Owner);
        var entry = Assert.Single(list);
        Assert.Equal(archiveId, entry.GetProperty("id").GetString());
        Assert.Equal("Round Trip", entry.GetProperty("name").GetString());
        Assert.Empty(await ListAsync(client, Member));

        var download = await client.SendAsync(Req(HttpMethod.Get, $"/api/v1/archive/spaces/{archiveId}/download", Owner), Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        using (var zip = new ZipArchive(new MemoryStream(await download.Content.ReadAsByteArrayAsync(Ct))))
        {
            Assert.NotNull(zip.GetEntry("manifest.json"));
            Assert.NotNull(zip.GetEntry("space.db"));
            Assert.NotNull(zip.GetEntry("files/docs/a.txt"));
        }
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Req(HttpMethod.Get, $"/api/v1/archive/spaces/{archiveId}/download", Member), Ct)).StatusCode);

        var restore = await client.SendAsync(Req(HttpMethod.Post, $"/api/v1/archive/spaces/{archiveId}/restore", Owner), Ct);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var restored = await restore.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var newSlug = restored.GetProperty("slug").GetString()!;
        Assert.Equal(slug, newSlug);   // the slug was free again
        Assert.NotEqual(id, restored.GetProperty("id").GetString());

        var notes = await client.SendAsync(Req(HttpMethod.Get, $"/api/v1/spaces/{newSlug}/notes", Owner), Ct);
        Assert.Contains("kept in the archive", await notes.Content.ReadAsStringAsync(Ct));
        var file = await client.SendAsync(Req(HttpMethod.Get, $"/api/v1/spaces/{newSlug}/files/content?path=docs/a.txt", Owner), Ct);
        Assert.Equal("archived bytes", await file.Content.ReadAsStringAsync(Ct));
        // No members came back, no keys either.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Req(HttpMethod.Get, $"/api/v1/spaces/{newSlug}/notes", Member), Ct)).StatusCode);
        using (var sys = _db.CreateSystemConnection())
            Assert.Equal(0, sys.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM api_keys WHERE context_id = @newSlug", new { newSlug }));

        // Restoring again while the slug is taken yields <slug>-2.
        var again = await client.SendAsync(Req(HttpMethod.Post, $"/api/v1/archive/spaces/{archiveId}/restore", Owner), Ct);
        Assert.Equal(slug + "-2", (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("slug").GetString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Req(HttpMethod.Delete, $"/api/v1/archive/spaces/{archiveId}", Member), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(Req(HttpMethod.Delete, $"/api/v1/archive/spaces/{archiveId}", Owner), Ct)).StatusCode);
        Assert.Empty(await ListAsync(client, Owner));
    }

    [Fact]
    public async Task Delete_WithoutArchive_RemovesFolder_KeepsNothing_Test()
    {
        var client = _factory.CreateClient();
        var space = await CreateSpaceAsync(client, "Gone For Good");
        var slug = space.GetProperty("slug").GetString()!;
        var id = space.GetProperty("id").GetString()!;
        await PutAsync(client, slug, "x.txt", "x");
        Assert.True(Directory.Exists(Path.Combine(_db.SpacesRoot, id)));

        var del = await client.SendAsync(Req(HttpMethod.Delete, $"/api/v1/spaces/{slug}?archive=false", Owner), Ct);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_db.SpacesRoot, id)));
        Assert.DoesNotContain(await ListAsync(client, Owner), a => a.GetProperty("spaceId").GetString() == id);
    }

    [Fact]
    public async Task Archive_UnknownId_404_Test()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Req(HttpMethod.Post, "/api/v1/archive/spaces/nope/restore", Owner), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Req(HttpMethod.Get, "/api/v1/archive/spaces/..%2Fsystem/download", Owner), Ct)).StatusCode);
    }

    private async Task<List<JsonElement>> ListAsync(HttpClient client, string user)
    {
        var resp = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/archive/spaces", user), Ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<List<JsonElement>>(Ct))!;
    }
}
