using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Desktop;
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

// /api/v1/desktop and the sandboxed app frame
// (docs/superpowers/specs/2026-09-26-desktop-apps-design.md § Server, § Tests):
// install/update/remove in both workspaces, the policy matrix, the origin
// allow-list, owner vs member vs readonly, Bearer 403, the frame's 404s and
// its CSP, and purgeData.
public class DesktopApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string Admin = "desk_admin";
    private const string Alice = "desk_alice";
    private const string Bob = "desk_bob";
    private const string Carol = "desk_carol";
    private const string Pin = "sha256-47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=";
    private const string Pin2 = "sha256-LCa0a2j/xo/5m0U8HTBBNBNCLXBkg7+g+YpeiGJm564=";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DesktopApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_desktop_api_" + Path.GetRandomFileName());
        _db = new DatabaseFactory(_dataDir);
        using (var sys = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            sys.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[]
                {
                    new { id = Admin, n = "Ad", e = "ad@a", now },
                    new { id = Alice, n = "A", e = "a@a", now },
                    new { id = Bob, n = "B", e = "b@b", now },
                    new { id = Carol, n = "C", e = "c@c", now },
                });
        }
        var system = new SystemRepository(_db);
        system.SetAdminAsync(Admin, true).GetAwaiter().GetResult();
        system.SetConfigAsync(FileLimits.MinFreeBytesKey, "0").GetAwaiter().GetResult();

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
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, user);
        return c;
    }

    private Task Config(string key, string value) => new SystemRepository(_db).SetConfigAsync(key, value, Ct);

    private static object Install(string id = "color-bucket", string? integrity = Pin, string mode = "sandboxed",
        string[]? granted = null, string origin = "https://owner.github.io", string extra = "",
        string name = "Color Bucket") => new
        {
            manifestUrl = origin + "/" + id + "/app.json",
            manifest = JsonDocument.Parse(
                $"{{\"id\":\"{id}\",\"name\":\"{name}\",\"kind\":\"view\",\"tag\":\"app-{id}\",\"entry\":\"app.js\",\"version\":\"1.0.0\",\"permissions\":[\"files\",\"identity\"]{extra}}}").RootElement,
            integrity,
            mode,
            granted = granted ?? new[] { "files" },
        };

    private static JsonElement ManifestOf() =>
        JsonDocument.Parse("{\"id\":\"color-bucket\",\"name\":\"Color Bucket\",\"kind\":\"view\",\"tag\":\"app-color-bucket\",\"entry\":\"app.js\",\"version\":\"1.1.0\",\"permissions\":[\"files\",\"identity\"]}").RootElement;

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<string?> ErrorOf(HttpResponseMessage r) =>
        (await Json(r)).TryGetProperty("error", out var e) ? e.GetString() : null;

    [Fact]
    public async Task Personal_InstallListUpdateRemove()
    {
        var c = As(Alice);
        var created = await c.PostAsJsonAsync("/api/v1/desktop/apps", Install(), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var app = await Json(created);
        Assert.Equal("sandboxed", app.GetProperty("mode").GetString());
        Assert.Equal(Pin, app.GetProperty("entryIntegrity").GetString());
        Assert.Equal("https://owner.github.io/color-bucket/app.js", app.GetProperty("entryUrl").GetString());
        Assert.Equal("Apps/Color Bucket", app.GetProperty("dataFolder").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/v1/desktop/apps", Install(), Ct)).StatusCode);

        var desk = await Json(await c.GetAsync("/api/v1/desktop", Ct));
        Assert.Single(desk.GetProperty("apps").EnumerateArray());
        Assert.True(desk.GetProperty("canInstall").GetBoolean());
        Assert.True(desk.GetProperty("canTrust").GetBoolean());

        // Another user never sees it.
        Assert.Empty((await Json(await As(Bob).GetAsync("/api/v1/desktop", Ct))).GetProperty("apps").EnumerateArray());

        // Re-pin: new code needs a new hash; the origin can't move.
        var noPin = await c.PatchAsJsonAsync("/api/v1/desktop/apps/color-bucket",
            new { manifest = ManifestOf() }, Ct);
        Assert.Equal("integrity_required", await ErrorOf(noPin));
        var repinned = await c.PatchAsJsonAsync("/api/v1/desktop/apps/color-bucket",
            new { manifest = ManifestOf(), integrity = Pin2 }, Ct);
        Assert.Equal(HttpStatusCode.OK, repinned.StatusCode);
        Assert.Equal(Pin2, (await Json(repinned)).GetProperty("entryIntegrity").GetString());
        var moved = await c.PatchAsJsonAsync("/api/v1/desktop/apps/color-bucket",
            new { manifestUrl = "https://elsewhere.github.io/app.json", manifest = ManifestOf(), integrity = Pin }, Ct);
        Assert.Equal("origin_changed", await ErrorOf(moved));

        // Grants change without new code.
        var grants = await c.PatchAsJsonAsync("/api/v1/desktop/apps/color-bucket", new { granted = new[] { "files", "identity" } }, Ct);
        Assert.Equal(2, (await Json(grants)).GetProperty("granted").GetArrayLength());

        // Sandboxed → trusted drops pin and grants; back needs a pin again.
        var trusted = await Json(await c.PatchAsJsonAsync("/api/v1/desktop/apps/color-bucket", new { mode = "trusted" }, Ct));
        Assert.Equal("trusted", trusted.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, trusted.GetProperty("entryIntegrity").ValueKind);
        Assert.Equal("integrity_required", await ErrorOf(await c.PatchAsJsonAsync("/api/v1/desktop/apps/color-bucket", new { mode = "sandboxed" }, Ct)));
        Assert.Equal(HttpStatusCode.OK, (await c.PatchAsJsonAsync("/api/v1/desktop/apps/color-bucket", new { mode = "sandboxed", integrity = Pin }, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync("/api/v1/desktop/apps/color-bucket", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync("/api/v1/desktop/apps/color-bucket", Ct)).StatusCode);
    }

    [Fact]
    public async Task Install_Refusals()
    {
        var c = As(Alice);
        Assert.Equal("integrity_required", await ErrorOf(await c.PostAsJsonAsync("/api/v1/desktop/apps", Install(integrity: null), Ct)));
        Assert.Equal("integrity_required", await ErrorOf(await c.PostAsJsonAsync("/api/v1/desktop/apps", Install(integrity: "sha256-nope"), Ct)));
        Assert.Equal("invalid_grant", await ErrorOf(await c.PostAsJsonAsync("/api/v1/desktop/apps", Install(granted: new[] { "notes" }), Ct)));
        Assert.Equal("invalid_manifest_url", await ErrorOf(await c.PostAsJsonAsync("/api/v1/desktop/apps", Install(origin: "http://owner.github.io"), Ct)));
        Assert.Equal("invalid_mode", await ErrorOf(await c.PostAsJsonAsync("/api/v1/desktop/apps", Install(mode: "root"), Ct)));
        var badConnect = await c.PostAsJsonAsync("/api/v1/desktop/apps", Install(extra: ",\"connect\":[\"https://x.example; script-src *\"]"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badConnect.StatusCode);
    }

    [Theory]
    // install level, trusted level, caller is admin, expected install / trusted
    [InlineData("everyone", "everyone", false, true, true)]
    [InlineData("everyone", "admins", false, true, false)]
    [InlineData("everyone", "admins", true, true, true)]
    [InlineData("everyone", "off", true, true, false)]
    [InlineData("admins", "everyone", false, false, false)]
    [InlineData("admins", "everyone", true, true, true)]
    [InlineData("off", "everyone", true, false, false)]
    public async Task Policy_Personal(string install, string trusted, bool admin, bool canInstall, bool canTrust)
    {
        await Config(DesktopPolicy.InstallKey, install);
        await Config(DesktopPolicy.TrustedKey, trusted);
        var c = As(admin ? Admin : Alice);

        var desk = await Json(await c.GetAsync("/api/v1/desktop", Ct));
        Assert.Equal(canInstall, desk.GetProperty("canInstall").GetBoolean());
        Assert.Equal(canTrust, desk.GetProperty("canTrust").GetBoolean());

        var sandboxed = await c.PostAsJsonAsync("/api/v1/desktop/apps", Install("one"), Ct);
        Assert.Equal(canInstall ? HttpStatusCode.Created : HttpStatusCode.Forbidden, sandboxed.StatusCode);
        var trust = await c.PostAsJsonAsync("/api/v1/desktop/apps", Install("two", integrity: null, mode: "trusted"), Ct);
        Assert.Equal(canTrust ? HttpStatusCode.Created : HttpStatusCode.Forbidden, trust.StatusCode);
    }

    [Theory]
    [InlineData("everyone", true)]
    [InlineData("admins", true)]   // a space is its owner's call
    [InlineData("off", false)]
    public async Task Policy_Space_OwnerInstallsUnlessOff(string install, bool allowed)
    {
        await Config(DesktopPolicy.InstallKey, install);
        var space = await new SpaceRepository(_db).CreateAsync(Alice, "Desk Space", Ct);
        var c = As(Alice);
        var r = await c.PostAsJsonAsync($"/api/v1/spaces/{space.Slug}/desktop/apps", Install(), Ct);
        Assert.Equal(allowed ? HttpStatusCode.Created : HttpStatusCode.Forbidden, r.StatusCode);

        // Trusted never goes into a space, whatever the policy says.
        var trusted = await c.PostAsJsonAsync($"/api/v1/spaces/{space.Slug}/desktop/apps", Install("t", integrity: null, mode: "trusted"), Ct);
        Assert.Equal(allowed ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden, trusted.StatusCode);
        if (allowed) Assert.Equal("trusted_personal_only", await ErrorOf(trusted));
    }

    [Fact]
    public async Task Policy_AllowList()
    {
        await Config(DesktopPolicy.AllowedOriginsKey, "https://owner.github.io");
        var c = As(Alice);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/v1/desktop/apps", Install(), Ct)).StatusCode);
        var other = await c.PostAsJsonAsync("/api/v1/desktop/apps", Install("other", origin: "https://stranger.github.io"), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal("origin_not_allowed", await ErrorOf(other));
    }

    [Fact]
    public async Task Space_Roles_AndBearer()
    {
        var space = await new SpaceRepository(_db).CreateAsync(Alice, "Shared Desk", Ct);
        using (var sys = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            sys.Execute("INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@s, @u, @r, @now)",
                new[] { new { s = space.Id, u = Bob, r = "member", now }, new { s = space.Id, u = Carol, r = "readonly", now } });
        }
        var prefix = $"/api/v1/spaces/{space.Slug}/desktop";
        Assert.Equal(HttpStatusCode.Created, (await As(Alice).PostAsJsonAsync(prefix + "/apps", Install(), Ct)).StatusCode);

        foreach (var member in new[] { Bob, Carol })
        {
            var c = As(member);
            var desk = await Json(await c.GetAsync(prefix, Ct));
            Assert.Single(desk.GetProperty("apps").EnumerateArray());   // sharing a space shares its apps
            Assert.False(desk.GetProperty("canInstall").GetBoolean());
            Assert.False(desk.GetProperty("canArrange").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync(prefix + "/apps", Install("mine"), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync(prefix + "/tiles/builtin:notes", new { size = "wide" }, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.DeleteAsync(prefix + "/apps/color-bucket", Ct)).StatusCode);
        }
        Assert.NotEqual(HttpStatusCode.OK, (await As(Admin).GetAsync(prefix, Ct)).StatusCode);   // not a member

        // Cookie-only: a Bearer key never reaches the desktop, not even its own workspace.
        var token = (await new ApiKeyRepository(_db).IssueAsync(Alice, ContextRef.User(Alice), "k",
            new[] { "read:notes", "read:files" }, Ct)).RawToken;
        var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.GetAsync("/api/v1/desktop", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.PostAsJsonAsync("/api/v1/desktop/apps", Install("b"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Tiles_Upsert_AndValidate()
    {
        var c = As(Alice);
        var ok = await c.PutAsJsonAsync("/api/v1/desktop/tiles/builtin:notes", new { position = 2.5, size = "wide", color = "teal", hidden = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        await c.PutAsJsonAsync("/api/v1/desktop/tiles/builtin:todos", new { position = 1.0 }, Ct);
        var tiles = (await Json(await c.GetAsync("/api/v1/desktop", Ct))).GetProperty("tiles").EnumerateArray().ToList();
        Assert.Equal(new[] { "builtin:todos", "builtin:notes" }, tiles.Select(t => t.GetProperty("key").GetString()));
        Assert.Equal("wide", tiles[1].GetProperty("size").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/v1/desktop/tiles/builtin:notes", new { size = "huge" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/v1/desktop/tiles/builtin:notes", new { color = "#ff0000" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/v1/desktop/tiles/Bad%20Key", new { size = "wide" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Frame_ServesSandboxedAppsOnly_WithTheirCsp()
    {
        var c = As(Alice);
        await c.PostAsJsonAsync("/api/v1/desktop/apps", Install(extra: ",\"connect\":[\"https://api.example.com\"]"), Ct);
        await c.PostAsJsonAsync("/api/v1/desktop/apps", Install("mine", integrity: null, mode: "trusted"), Ct);

        var frame = await c.GetAsync($"/apps/frame/user/{Alice}/color-bucket", Ct);
        Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
        var csp = string.Join(" ", frame.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal(FrameCsp.Build("https://owner.github.io", new[] { "https://api.example.com" }), csp);
        Assert.Equal("nosniff", frame.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", frame.Headers.GetValues("Referrer-Policy").Single());
        var html = await frame.Content.ReadAsStringAsync(Ct);
        Assert.Contains($"src=\"https://owner.github.io/color-bucket/app.js\" integrity=\"{Pin}\" crossorigin=\"anonymous\"", html);
        Assert.Contains("data-app-tag=\"app-color-bucket\"", html);
        Assert.Contains(Fishbowl.Api.Endpoints.DesktopApi.GuestScript, html);

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/apps/frame/user/{Alice}/nope", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/apps/frame/user/{Alice}/mine", Ct)).StatusCode);   // trusted: no frame
        Assert.Equal(HttpStatusCode.NotFound, (await As(Bob).GetAsync($"/apps/frame/user/{Alice}/color-bucket", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/apps/frame/team/{Alice}/color-bucket", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false })
            .GetAsync($"/apps/frame/user/{Alice}/color-bucket", Ct)).StatusCode);

        // Space frames: members only.
        var space = await new SpaceRepository(_db).CreateAsync(Alice, "Frame Space", Ct);
        await c.PostAsJsonAsync($"/api/v1/spaces/{space.Slug}/desktop/apps", Install(name: "<script>alert(1)</script>"), Ct);
        var spaceFrame = await c.GetAsync($"/apps/frame/space/{space.Slug}/color-bucket", Ct);
        Assert.Equal(HttpStatusCode.OK, spaceFrame.StatusCode);
        var spaceHtml = await spaceFrame.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("<script>alert(1)</script>", spaceHtml);
        Assert.Contains("&lt;script&gt;", spaceHtml);
        Assert.Contains("connect-src 'none'", string.Join(" ", spaceFrame.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(HttpStatusCode.NotFound, (await As(Bob).GetAsync($"/apps/frame/space/{space.Slug}/color-bucket", Ct)).StatusCode);
    }

    [Fact]
    public async Task Remove_KeepsDataByDefault_PurgesOnRequest()
    {
        var c = As(Alice);
        await c.PostAsJsonAsync("/api/v1/desktop/apps", Install(), Ct);
        await c.PostAsJsonAsync("/api/v1/desktop/apps", Install("second", name: "Second App"), Ct);
        foreach (var path in new[] { "Apps/Color Bucket/settings.json", "Apps/Second App/state.json" })
        {
            var req = new HttpRequestMessage(HttpMethod.Put, "/api/v1/files/content?parents=1&path=" + Uri.EscapeDataString(path))
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")),
            };
            req.Headers.Add("X-Fishbowl-Upload", "1");
            req.Headers.TryAddWithoutValidation("If-None-Match", "*");
            Assert.Equal(HttpStatusCode.Created, (await c.SendAsync(req, Ct)).StatusCode);
        }

        var kept = await Json(await c.DeleteAsync("/api/v1/desktop/apps/color-bucket", Ct));
        Assert.False(kept.GetProperty("purged").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/files/stat?path=" + Uri.EscapeDataString("Apps/Color Bucket"), Ct)).StatusCode);

        var purged = await Json(await c.DeleteAsync("/api/v1/desktop/apps/second?purgeData=true", Ct));
        Assert.True(purged.GetProperty("purged").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/files/stat?path=" + Uri.EscapeDataString("Apps/Second App"), Ct)).StatusCode);

        // Nothing stored: purging is a no-op, not an error.
        await c.PostAsJsonAsync("/api/v1/desktop/apps", Install("third", name: "Third App"), Ct);
        var none = await Json(await c.DeleteAsync("/api/v1/desktop/apps/third?purgeData=true", Ct));
        Assert.False(none.GetProperty("purged").GetBoolean());
    }
}
