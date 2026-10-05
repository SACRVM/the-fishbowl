using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Core.Files;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// A space's app code (files/.apps) stays the Designers' whichever way a
// write comes: a transfer is judged by where its items land, trash ids by
// what they name in any spelling, trashed app code is theirs to throw away,
// and a deploy either replaces the app whole or changes nothing.
public class AppsFolderGuardTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string Alice = "guard_alice";   // owner: Designer and more
    private const string Bob = "guard_bob";       // member
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AppsFolderGuardTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_apps_guard_" + Path.GetRandomFileName());
        _db = new DatabaseFactory(_dataDir);
        using (var sys = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            sys.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[] { new { id = Alice, n = "A", e = "a@a", now }, new { id = Bob, n = "B", e = "b@b", now } });
        }
        new SystemRepository(_db).SetConfigAsync(FileLimits.MinFreeBytesKey, "0").GetAwaiter().GetResult();

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton(_db);
                services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    o.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    o.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.AuthenticationScheme, o =>
                {
                    o.ForwardDefaultSelector = ctx =>
                        ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal) ? "ApiKey" : null;
                });
            });
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private HttpClient As(string user)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, user);
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
    private static async Task<string?> Code(HttpResponseMessage r) => (await Json(r)).GetProperty("error").GetString();

    // A space owned by Alice with Bob as a member.
    private async Task<(string Slug, string Id, string Prefix)> SpaceAsync()
    {
        var space = await new SpaceRepository(_db).CreateAsync(Alice, "Guarded " + Guid.NewGuid().ToString("N")[..6], Ct);
        using (var sys = _db.CreateSystemConnection())
            sys.Execute("INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@s, @u, 'member', @now)",
                new { s = space.Id, u = Bob, now = DateTime.UtcNow.ToString("o") });
        return (space.Slug, space.Id, $"/api/v1/spaces/{space.Slug}/files");
    }

    private static async Task<HttpResponseMessage> Put(HttpClient c, string prefix, string path, string text = "{}")
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"{prefix}/content?parents=1&path={Uri.EscapeDataString(path)}")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Headers.Add("X-Fishbowl-Upload", "1");
        req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        return await c.SendAsync(req, Ct);
    }

    private static Task<HttpResponseMessage> Transfer(HttpClient c, string slug, string op, string path, string folder, string onConflict) =>
        c.PostAsJsonAsync("/api/v1/files/transfer", new
        {
            op,
            from = new { workspace = "space:" + slug, paths = new[] { path } },
            to = new { workspace = "space:" + slug, folder },
            onConflict,
        }, Ct);

    private string FilesRoot(string spaceId) => Path.Combine(_dataDir, "spaces", spaceId, "files");

    // ───── transfer ─────

    [Fact]
    public async Task Transfer_IsJudgedByWhereItsItemsLand_AndNeverReplacesApps_Test()
    {
        var (slug, id, prefix) = await SpaceAsync();
        Assert.Equal(HttpStatusCode.Created, (await Put(As(Alice), prefix, ".apps/booking/app.json")).StatusCode);
        // A member may keep a folder called .apps anywhere below the root…
        Assert.Equal(HttpStatusCode.Created, (await Put(As(Bob), prefix, "x/.apps/evil/triggers/t.js", "throw 1")).StatusCode);

        // …but can't send it to the root, whatever the conflict mode (a
        // replace would trash .apps itself — refused as that first).
        foreach (var (op, mode, code) in new[]
                 {
                     ("copy", "rename", "apps_design_only"), ("move", "fail", "apps_design_only"),
                     ("copy", "replace", "apps_folder_fixed"), ("move", "replace", "apps_folder_fixed"),
                 })
            Assert.Equal(code, await Code(await Transfer(As(Bob), slug, op, "x/.apps", "", mode)));
        var planted = await Transfer(As(Bob), slug, "copy", "x/.apps/evil", ".apps", "fail");
        Assert.Equal(HttpStatusCode.Forbidden, planted.StatusCode);
        // A Designer may move app code around, but a transfer never trashes .apps itself.
        var replace = await Transfer(As(Alice), slug, "copy", "x/.apps", "", "replace");
        Assert.Equal(HttpStatusCode.Conflict, replace.StatusCode);
        Assert.Equal("apps_folder_fixed", await Code(replace));

        Assert.True(File.Exists(Path.Combine(FilesRoot(id), ".apps", "booking", "app.json")));
        Assert.False(Directory.Exists(Path.Combine(FilesRoot(id), ".apps", "evil")));
        Assert.Empty((await Json(await As(Alice).GetAsync($"{prefix}/trash", Ct))).EnumerateArray());
    }

    [Fact]
    public async Task Transfer_DesignerMovesIntoApps_Test()
    {
        var (slug, id, prefix) = await SpaceAsync();
        Assert.Equal(HttpStatusCode.Created, (await Put(As(Alice), prefix, ".apps/booking/app.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Put(As(Alice), prefix, "helper.js", "1")).StatusCode);

        var moved = await Transfer(As(Alice), slug, "move", "helper.js", ".apps/booking", "fail");
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var result = (await Json(moved)).GetProperty("results")[0];
        Assert.Equal(JsonValueKind.Null, result.GetProperty("error").ValueKind);
        Assert.True(File.Exists(Path.Combine(FilesRoot(id), ".apps", "booking", "helper.js")));

        // Into .apps itself too.
        Assert.Equal(HttpStatusCode.Created, (await Put(As(Alice), prefix, "readme.md", "#")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Transfer(As(Alice), slug, "move", "readme.md", ".apps", "fail")).StatusCode);
        Assert.True(File.Exists(Path.Combine(FilesRoot(id), ".apps", "readme.md")));
    }

    // ───── trash ─────

    [Fact]
    public async Task Trash_AppCode_StaysTheDesigners_InAnyIdSpelling_Test()
    {
        var (_, _, prefix) = await SpaceAsync();
        Assert.Equal(HttpStatusCode.Created, (await Put(As(Alice), prefix, ".apps/booking/app.js", "v1")).StatusCode);
        var trashed = await Json(await As(Alice).DeleteAsync($"{prefix}?path=.apps/booking/app.js", Ct));
        var id = trashed.GetProperty("id").GetString()!;

        // A member: no restore and no purge, however the id is spelled.
        foreach (var spelling in new[] { id, id.ToLowerInvariant() })
        {
            Assert.Equal("apps_design_only", await Code(await As(Bob).PostAsync($"{prefix}/trash/{spelling}/restore", null, Ct)));
            Assert.Equal("apps_design_only", await Code(await As(Bob).DeleteAsync($"{prefix}/trash/{spelling}", Ct)));
        }

        // Emptying the trash leaves app code for a Designer.
        Assert.Equal(HttpStatusCode.Created, (await Put(As(Bob), prefix, "n.txt", "n")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(Bob).DeleteAsync($"{prefix}?path=n.txt", Ct)).StatusCode);
        var emptied = await As(Bob).DeleteAsync($"{prefix}/trash", Ct);
        Assert.Equal(1, (await Json(emptied)).GetProperty("purged").GetInt32());
        var left = (await Json(await As(Bob).GetAsync($"{prefix}/trash", Ct))).EnumerateArray().ToList();
        Assert.Equal(".apps/booking/app.js", Assert.Single(left).GetProperty("originalPath").GetString());

        // The Designer restores it with the id in any spelling — to where it was.
        Assert.Equal(HttpStatusCode.OK, (await As(Alice).PostAsync($"{prefix}/trash/{id.ToLowerInvariant()}/restore", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(Bob).GetAsync($"{prefix}/stat?path=.apps/booking/app.js", Ct)).StatusCode);
    }

    // ───── deploy ─────

    private static ByteArrayContent Zip(params (string Path, byte[] Bytes)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, bytes) in files)
            {
                using var s = zip.CreateEntry(path, CompressionLevel.Fastest).Open();
                s.Write(bytes);
            }
        return new ByteArrayContent(ms.ToArray());
    }

    private static (string, byte[]) F(string path, string text) => (path, Encoding.UTF8.GetBytes(text));

    private const string Manifest = "{\"name\":\"Board\",\"tag\":\"board-app\"}";

    [Fact]
    public async Task Deploy_ThatFailsHalfway_ChangesNothing_Test()
    {
        var (slug, id, _) = await SpaceAsync();
        var url = $"/api/v1/spaces/{slug}/apps/board/deploy";
        Assert.Equal(HttpStatusCode.OK, (await As(Alice).PostAsync(url, Zip(F("app.json", Manifest), F("app.js", "v1")), Ct)).StatusCode);

        // The third file is over the server's file limit: two are staged by then.
        await new SystemRepository(_db).SetConfigAsync(FileLimits.MaxFileBytesKey, "64", Ct);
        var refused = await As(Alice).PostAsync(url, Zip(F("app.json", Manifest), F("app.js", "v2"), F("big.js", new string('x', 100))), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.Equal("too_large", await Code(refused));

        var apps = Path.Combine(FilesRoot(id), ".apps");
        Assert.Equal("v1", File.ReadAllText(Path.Combine(apps, "board", "app.js")));
        Assert.False(File.Exists(Path.Combine(apps, "board", "big.js")));
        Assert.Equal(new[] { "board" }, Directory.EnumerateDirectories(apps).Select(Path.GetFileName));
        Assert.Empty((await Json(await As(Alice).GetAsync($"/api/v1/spaces/{slug}/files/trash", Ct))).EnumerateArray());
    }

    [Fact]
    public async Task Deploy_RefusesWhatCantLandWhole_BeforeWritingAnything_Test()
    {
        var (slug, id, _) = await SpaceAsync();
        var url = $"/api/v1/spaces/{slug}/apps/board/deploy";
        async Task<HttpResponseMessage> Deploy(params (string, byte[])[] files) => await As(Alice).PostAsync(url, Zip(files), Ct);

        var twice = await Deploy(F("app.json", Manifest), F("app.js", "1"), F("app.js", "2"));
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
        Assert.Equal("invalid_value", await Code(twice));
        Assert.Equal(HttpStatusCode.BadRequest, (await Deploy(F("app.json", Manifest), F("lib", "1"), F("lib/x.js", "2"))).StatusCode);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Deploy(F("app.json", Manifest), F("App.js", "1"), F("app.js", "2"))).StatusCode);
            var device = await Json(await Deploy(F("app.json", Manifest), F("con.js", "1")));
            Assert.Equal("invalid_name", device.GetProperty("error").GetString());
            Assert.Equal("reserved_device", device.GetProperty("rule").GetString());
        }

        // A small ZIP that unpacks past the limit.
        var bomb = await Deploy(F("app.json", Manifest), ("zeros.bin", new byte[(int)(Fishbowl.Api.Endpoints.SpaceAppsApi.MaxDeployUnpackedBytes + 1)]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bomb.StatusCode);
        Assert.Equal("unpacked", (await Json(bomb)).GetProperty("field").GetString());

        Assert.False(Directory.Exists(Path.Combine(FilesRoot(id), ".apps")));
    }
}
