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
        Assert.False(desk.TryGetProperty("canTrust", out _));   // the trusted mode is gone

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

        // There is no other mode to switch to.
        Assert.Equal("invalid_mode", await ErrorOf(await c.PatchAsJsonAsync("/api/v1/desktop/apps/color-bucket", new { mode = "trusted" }, Ct)));

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

    [Fact]
    public async Task Trusted_IsGone_EvenForAdmins()
    {
        var r = await As(Admin).PostAsJsonAsync("/api/v1/desktop/apps", Install("t", integrity: null, mode: "trusted"), Ct);
        Assert.Equal("invalid_mode", await ErrorOf(r));
    }

    [Fact]
    public async Task OldTrustedInstalls_ComeBackSandboxedWithoutAPin()
    {
        // A desktop DB from before v19 with a trusted install.
        var dir = Path.Combine(_dataDir, "users", "legacy_trusted");
        Directory.CreateDirectory(dir);
        using (var c = _db.CreateContextConnection(ContextRef.User("legacy_trusted")))
        {
            c.Execute(@"INSERT INTO desktop_apps(id, manifest_url, manifest, origin, entry_url, entry_integrity, version, mode, granted, installed_at, updated_at)
                        VALUES ('old', 'https://o.github.io/old/app.json', '{}', 'https://o.github.io', 'https://o.github.io/old/app.js', NULL, '1', 'trusted', '[]', '2026-01-01', '2026-01-01')");
            c.Execute("PRAGMA user_version = 18");
        }
        SqliteConnection.ClearAllPools();
        using var db = new DatabaseFactory(_dataDir).CreateContextConnection(ContextRef.User("legacy_trusted"));
        Assert.Equal("sandboxed", db.ExecuteScalar<string>("SELECT mode FROM desktop_apps WHERE id = 'old'"));
        Assert.Null(db.ExecuteScalar<string?>("SELECT entry_integrity FROM desktop_apps WHERE id = 'old'"));
    }

    [Fact]
    public async Task Install_AcceptsTheKitsManifestShape_AndIdentityLevels()
    {
        var c = As(Alice);
        var kitShape = new
        {
            manifestUrl = "https://owner.github.io/kanban/app.json",
            manifest = JsonDocument.Parse(
                "{\"id\":\"kanban\",\"name\":\"Kanban\",\"kind\":\"window\",\"tag\":\"app-kanban\",\"entry\":\"app.js\",\"permissions\":{\"files\":true,\"identity\":true},\"src\":\"https://owner.github.io/kanban/app.js\",\"entryIntegrity\":\"" + Pin + "\"}").RootElement,
            integrity = Pin,
            mode = "sandboxed",
            granted = new[] { "identity:pseudonymous" },
        };
        var created = await c.PostAsJsonAsync("/api/v1/desktop/apps", kitShape, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("identity:pseudonymous", (await Json(created)).GetProperty("granted")[0].GetString());
        var both = await c.PatchAsJsonAsync("/api/v1/desktop/apps/kanban", new { granted = new[] { "identity", "identity:pseudonymous" } }, Ct);
        Assert.Equal("invalid_grant", await ErrorOf(both));
    }

    [Theory]
    // install level, caller is admin, expected install
    [InlineData("everyone", false, true)]
    [InlineData("admins", false, false)]
    [InlineData("admins", true, true)]
    [InlineData("off", true, false)]
    public async Task Policy_Personal(string install, bool admin, bool canInstall)
    {
        await Config(DesktopPolicy.InstallKey, install);
        var c = As(admin ? Admin : Alice);

        var desk = await Json(await c.GetAsync("/api/v1/desktop", Ct));
        Assert.Equal(canInstall, desk.GetProperty("canInstall").GetBoolean());

        var sandboxed = await c.PostAsJsonAsync("/api/v1/desktop/apps", Install("one"), Ct);
        Assert.Equal(canInstall ? HttpStatusCode.Created : HttpStatusCode.Forbidden, sandboxed.StatusCode);
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
                new[] { new { s = space.Id, u = Bob, r = "member", now }, new { s = space.Id, u = Carol, r = "reader", now } });
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

        var frame = await c.GetAsync($"/apps/frame/user/{Alice}/color-bucket", Ct);
        Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
        var csp = string.Join(" ", frame.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal(FrameCsp.Build("https://owner.github.io", new[] { "https://api.example.com" },
            "http://localhost", "https://owner.github.io/color-bucket/app.js"), csp);
        Assert.Equal("nosniff", frame.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", frame.Headers.GetValues("Referrer-Policy").Single());
        var html = await frame.Content.ReadAsStringAsync(Ct);
        // The kit's harness: the kit and its guest runtime, never the entry
        // (the bridge hands the pinned entry over at boot).
        Assert.Contains("data-sac-guest", html);
        Assert.Contains("data-app-tag=\"app-color-bucket\"", html);
        Assert.Contains("/kit/js/all.js", html);
        Assert.Contains(Fishbowl.Api.Endpoints.DesktopApi.GuestScript, html);
        Assert.DoesNotContain("app.js\"", html);

        // Fishbowl's own pages carry PageCsp: only its own scripts, for everyone.
        static string ScriptSrc(HttpResponseMessage r) => string.Join(" ", r.Headers.GetValues("Content-Security-Policy"))
            .Split("; ").Single(d => d.StartsWith("script-src "));
        Assert.Equal("script-src 'self'", ScriptSrc(await c.GetAsync("/csp-probe", Ct)));
        Assert.Equal("script-src 'self'", ScriptSrc(await As(Bob).GetAsync("/csp-probe", Ct)));

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/apps/frame/user/{Alice}/nope", Ct)).StatusCode);
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
        Assert.Contains("connect-src blob: data:", string.Join(" ", spaceFrame.Headers.GetValues("Content-Security-Policy")));
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

    [Fact]
    public async Task SpaceApps_FromDotApps_CodeUnderASignedUrl_FrameForMembers()
    {
        var space = await new SpaceRepository(_db).CreateAsync(Alice, "Code Space", Ct);
        using (var sys = _db.CreateSystemConnection())
            sys.Execute("INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@s, @u, 'reader', @now)",
                new { s = space.Id, u = Bob, now = DateTime.UtcNow.ToString("o") });
        // The disk is the truth: app code is files in .apps/<folder>/.
        var apps = Path.Combine(_dataDir, "spaces", space.Id, "files", ".apps");
        Directory.CreateDirectory(Path.Combine(apps, "hello"));
        Directory.CreateDirectory(Path.Combine(apps, "broken"));
        File.WriteAllText(Path.Combine(apps, "hello", "app.json"), "{\"name\":\"Hello\",\"tag\":\"hello-app\",\"version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(apps, "hello", "app.js"), "customElements.define('hello-app', class extends HTMLElement {});");
        File.WriteAllText(Path.Combine(apps, "hello", "page.html"), "<script>alert(1)</script>");
        File.WriteAllText(Path.Combine(apps, "broken", "app.json"), "{\"name\":\"No tag\"}");

        // Every member's desktop lists it, with the installed apps' shape.
        var desk = await Json(await As(Bob).GetAsync($"/api/v1/spaces/{space.Slug}/desktop", Ct));
        var app = Assert.Single(desk.GetProperty("apps").EnumerateArray());
        Assert.Equal("space.hello", app.GetProperty("id").GetString());
        Assert.Equal("space", app.GetProperty("mode").GetString());
        Assert.Equal("Hello", app.GetProperty("manifest").GetProperty("name").GetString());
        var entry = app.GetProperty("entryUrl").GetString()!;
        Assert.StartsWith($"http://localhost/apps/code/{space.Id}/hello/", entry);
        Assert.EndsWith("/app.js", entry);
        Assert.Empty((await Json(await As(Alice).GetAsync("/api/v1/desktop", Ct))).GetProperty("apps").EnumerateArray());

        // The code: no cookie (the frame is opaque) — the signature is the key.
        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var code = await anon.GetAsync(entry, Ct);
        Assert.Equal(HttpStatusCode.OK, code.StatusCode);
        Assert.Equal("text/javascript", code.Content.Headers.ContentType!.MediaType);
        Assert.Equal("*", code.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("nosniff", code.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", string.Join(" ", code.Headers.GetValues("Content-Security-Policy")));
        Assert.Contains("hello-app", await code.Content.ReadAsStringAsync(Ct));

        var bas = entry[..^"app.js".Length];
        var sig = bas.TrimEnd('/').Split('/')[^1];
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(bas + "page.html", Ct)).StatusCode);       // never HTML
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(bas + "nope.js", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(bas.Replace(sig, "x" + sig[1..]) + "app.js", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(bas.Replace("/hello/", "/broken/") + "app.json", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(bas + "..%2Fbroken%2Fapp.json", Ct)).StatusCode);

        // The frame document: members only, its scripts limited to the code folder.
        var frame = await As(Bob).GetAsync($"/apps/frame/space/{space.Slug}/space.hello", Ct);
        Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
        var csp = string.Join(" ", frame.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains($"script-src http://localhost/kit/js/ {bas};", csp);
        Assert.Contains("data-app-tag=\"hello-app\"", await frame.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await As(Carol).GetAsync($"/apps/frame/space/{space.Slug}/space.hello", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As(Bob).GetAsync($"/apps/frame/space/{space.Slug}/space.broken", Ct)).StatusCode);
    }

    [Fact]
    public async Task SpaceApps_ErrorStore_ManifestProblems_ReportsAndClear()
    {
        var space = await new SpaceRepository(_db).CreateAsync(Alice, "Error Space", Ct);
        using (var sys = _db.CreateSystemConnection())
            sys.Execute("INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@s, @u, 'reader', @now)",
                new { s = space.Id, u = Bob, now = DateTime.UtcNow.ToString("o") });
        var apps = Path.Combine(_dataDir, "spaces", space.Id, "files", ".apps");
        Directory.CreateDirectory(Path.Combine(apps, "notag"));
        Directory.CreateDirectory(Path.Combine(apps, "badjson"));
        Directory.CreateDirectory(Path.Combine(apps, "plain"));       // no app.json: no app, no error
        File.WriteAllText(Path.Combine(apps, "notag", "app.json"), "{\"name\":\"No tag\"}");
        File.WriteAllText(Path.Combine(apps, "badjson", "app.json"), "{ nope");
        var errors = $"/api/v1/spaces/{space.Slug}/apps/errors";

        // The server records what it can't read — once, however often the desktop loads.
        await As(Bob).GetAsync($"/api/v1/spaces/{space.Slug}/desktop", Ct);
        await As(Alice).GetAsync($"/api/v1/spaces/{space.Slug}/desktop", Ct);
        var list = (await Json(await As(Alice).GetAsync(errors, Ct))).GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.All(list, e => Assert.Equal("manifest", e.GetProperty("kind").GetString()));
        Assert.Contains(list, e => e.GetProperty("app").GetString() == "notag" && e.GetProperty("message").GetString()!.Contains("\"tag\""));
        Assert.Contains(list, e => e.GetProperty("app").GetString() == "badjson" && e.GetProperty("message").GetString()!.Contains("valid JSON"));

        // Designers read and clear; any member's browser reports.
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Bob).GetAsync(errors, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await As(Bob).PostAsJsonAsync(errors, new { app = "notag", kind = "reported", message = "boom", detail = "at line 3" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As(Bob).PostAsJsonAsync(errors, new { app = "notag", kind = "manifest", message = "fake" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As(Bob).PostAsJsonAsync(errors, new { app = "../x", kind = "load", message = "x" }, Ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.NoContent, (await As(Carol).PostAsJsonAsync(errors, new { app = "notag", kind = "load", message = "x" }, Ct)).StatusCode);   // not a member
        var newest = (await Json(await As(Alice).GetAsync($"{errors}?app=notag", Ct))).GetProperty("errors")[0];
        Assert.Equal("boom", newest.GetProperty("message").GetString());
        Assert.Equal("at line 3", newest.GetProperty("detail").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await As(Bob).DeleteAsync(errors, Ct)).StatusCode);
        Assert.Equal(3, (await Json(await As(Alice).DeleteAsync(errors, Ct))).GetProperty("removed").GetInt32());
        Assert.Empty((await Json(await As(Alice).GetAsync(errors, Ct))).GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task SpaceGuide_WrittenFromTheLiveSpace_RestAndMcp()
    {
        var space = await new SpaceRepository(_db).CreateAsync(Alice, "Guide Space", Ct);
        using (var sys = _db.CreateSystemConnection())
            sys.Execute("INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@s, @u, 'member', @now)",
                new { s = space.Id, u = Bob, now = DateTime.UtcNow.ToString("o") });
        var created = await As(Alice).PostAsJsonAsync($"/api/v1/spaces/{space.Slug}/tables",
            new { name = "rooms", description = "Rooms of the club", columns = new[] { new { name = "seats", type = "integer", description = "How many fit" } } }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var apps = Path.Combine(_dataDir, "spaces", space.Id, "files", ".apps");
        Directory.CreateDirectory(Path.Combine(apps, "booking"));
        File.WriteAllText(Path.Combine(apps, "booking", "app.json"), "{\"name\":\"Booking\",\"tag\":\"booking-app\",\"version\":\"2.0.0\"}");

        var guide = await As(Alice).GetAsync($"/api/v1/spaces/{space.Slug}/guide", Ct);
        Assert.Equal(HttpStatusCode.OK, guide.StatusCode);
        Assert.Equal("text/markdown", guide.Content.Headers.ContentType!.MediaType);
        var md = await guide.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Your role here: **owner**", md);
        Assert.Contains("context.space", md);
        Assert.Contains("rooms", md);
        Assert.Contains("seats", md);
        Assert.Contains("`.apps/booking/` — **Booking** v2.0.0, `<booking-app>`", md);
        Assert.Contains($"PUT /api/v1/spaces/{space.Slug}/files/content", md);

        // A member is told what they may not do; a stranger gets nothing.
        Assert.Contains("needs the Designer role", await (await As(Bob).GetAsync($"/api/v1/spaces/{space.Slug}/guide", Ct)).Content.ReadAsStringAsync(Ct));
        Assert.NotEqual(HttpStatusCode.OK, (await As(Carol).GetAsync($"/api/v1/spaces/{space.Slug}/guide", Ct)).StatusCode);

        // The same over MCP, for a space key.
        var token = (await new ApiKeyRepository(_db).IssueAsync(Alice, ContextRef.Space(space.Slug), "agent", new[] { "read:tables" }, Ct)).RawToken;
        var mcp = _factory.CreateClient();
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        mcp.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        mcp.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        var rpc = await mcp.PostAsync("/mcp", new StringContent(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "space_guide", arguments = new { } },
        }), Encoding.UTF8, "application/json"), Ct);
        var body = await Json(rpc);
        var text = body.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("booking-app", JsonDocument.Parse(text).RootElement.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task SpaceApps_Notify_MembersOnly_Muting_RateLimit()
    {
        var space = await new SpaceRepository(_db).CreateAsync(Alice, "Notify Space", Ct);
        using (var sys = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            sys.Execute("INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@s, @u, @r, @now)",
                new[] { new { s = space.Id, u = Bob, r = "member", now }, new { s = space.Id, u = Carol, r = "reader", now } });
        }
        var apps = Path.Combine(_dataDir, "spaces", space.Id, "files", ".apps", "board");
        Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(apps, "app.json"), "{\"name\":\"Board\",\"tag\":\"board-app\"}");
        var notify = $"/api/v1/spaces/{space.Slug}/apps/board/notify";
        async Task<int> Send(HttpClient c, object to, string text = "Lunch is here")
        {
            var r = await c.PostAsJsonAsync(notify, new { to, text }, Ct);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return (await Json(r)).GetProperty("sent").GetInt32();
        }
        async Task<string?> Code(HttpResponseMessage r) => (await Json(r)).GetProperty("error").GetString();

        // "all" is every member but the sender; the server sets space and app.
        Assert.Equal(2, await Send(As(Bob), "all"));
        var inbox = (await Json(await As(Alice).GetAsync("/api/v1/messages", Ct))).GetProperty("items").EnumerateArray()
            .Single(m => m.GetProperty("kind").GetString() == "app.message");
        Assert.Equal("Notify Space", inbox.GetProperty("subject").GetProperty("name").GetString());
        Assert.Equal("Board", inbox.GetProperty("data").GetProperty("appName").GetString());
        Assert.Equal("Lunch is here", inbox.GetProperty("data").GetProperty("text").GetString());

        // Members only; a real app; text; a writer; a person.
        Assert.Equal("not_members", await Code(await As(Bob).PostAsJsonAsync(notify, new { to = new[] { Admin }, text = "hi" }, Ct)));
        Assert.Equal("app_missing", await Code(await As(Bob).PostAsJsonAsync($"/api/v1/spaces/{space.Slug}/apps/nope/notify", new { to = "all", text = "hi" }, Ct)));
        Assert.Equal("invalid_app_message", await Code(await As(Bob).PostAsJsonAsync(notify, new { to = "all", text = " " }, Ct)));
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Carol).PostAsJsonAsync(notify, new { to = "all", text = "hi" }, Ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await As(Admin).PostAsJsonAsync(notify, new { to = "all", text = "hi" }, Ct)).StatusCode);

        // Muting the app, or the whole space, silences it for that person only.
        Assert.Equal(HttpStatusCode.NoContent, (await As(Alice).PutAsJsonAsync("/api/v1/messages/muted", new { space = space.Id, app = "board", muted = true }, Ct)).StatusCode);
        Assert.Equal(1, await Send(As(Bob), "all"));                       // Carol only
        var muted = (await Json(await As(Alice).GetAsync("/api/v1/messages/muted", Ct))).GetProperty("muted")[0];
        Assert.Equal("board", muted.GetProperty("app").GetString());
        Assert.Equal("Notify Space", muted.GetProperty("spaceName").GetString());
        await As(Alice).PutAsJsonAsync("/api/v1/messages/muted", new { space = space.Id, app = "board", muted = false }, Ct);
        await As(Carol).PutAsJsonAsync("/api/v1/messages/muted", new { space = space.Id, muted = true }, Ct);
        Assert.Equal(1, await Send(As(Bob), "all"));                       // Alice only

        // 30 an hour per app.
        for (var n = 3; n < Fishbowl.Api.Endpoints.AppMessagesApi.PerHour; n++) await Send(As(Bob), new[] { Alice });
        var limited = await As(Bob).PostAsJsonAsync(notify, new { to = "all", text = "one more" }, Ct);
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.Equal("rate_limited", await Code(limited));
    }

    [Fact]
    public async Task OAuth_RegisterAuthorizeToken_TheTokenIsAKeyForTheChosenWorkspace()
    {
        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Discovery, and the 401 that points at it.
        var meta = await Json(await anon.GetAsync("/.well-known/oauth-authorization-server", Ct));
        Assert.Equal("http://localhost/oauth/token", meta.GetProperty("token_endpoint").GetString());
        Assert.Contains("S256", meta.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));
        var resource = await Json(await anon.GetAsync("/.well-known/oauth-protected-resource", Ct));
        Assert.Equal("http://localhost/mcp", resource.GetProperty("resource").GetString());
        var unauth = await anon.PostAsync("/mcp", new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
        Assert.Contains("resource_metadata=\"http://localhost/.well-known/oauth-protected-resource\"", unauth.Headers.WwwAuthenticate.ToString());

        // Dynamic registration: https callbacks only (loopback http for dev).
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/oauth/register", new { client_name = "Evil", redirect_uris = new[] { "http://evil.example/cb" } }, Ct)).StatusCode);
        var reg = await anon.PostAsJsonAsync("/oauth/register", new { client_name = "Claude", redirect_uris = new[] { "https://claude.ai/api/mcp/auth_callback" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
        var clientId = (await Json(reg)).GetProperty("client_id").GetString()!;
        const string Callback = "https://claude.ai/api/mcp/auth_callback";

        // The consent page asks a signed-in person; the decision is a one-time code.
        Assert.Equal(HttpStatusCode.Redirect, (await anon.GetAsync($"/oauth/authorize?client_id={clientId}", Ct)).StatusCode);
        var space = await new SpaceRepository(_db).CreateAsync(Alice, "OAuth Space", Ct);
        var request = await Json(await As(Alice).GetAsync($"/api/v1/oauth/request?client_id={clientId}&redirect_uri={Uri.EscapeDataString(Callback)}", Ct));
        Assert.Equal("Claude", request.GetProperty("client").GetString());
        Assert.Contains(request.GetProperty("workspaces").EnumerateArray(), w => w.GetProperty("id").GetString() == "space:" + space.Slug);
        Assert.Equal(HttpStatusCode.BadRequest, (await As(Alice).GetAsync($"/api/v1/oauth/request?client_id={clientId}&redirect_uri=https://evil.example/", Ct)).StatusCode);

        var verifier = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var decision = await As(Alice).PostAsJsonAsync("/api/v1/oauth/authorize", new
        {
            clientId,
            redirectUri = Callback,
            codeChallenge = challenge,
            state = "xyz",
            workspace = "space:" + space.Slug,
            access = "build",
        }, Ct);
        var redirect = new Uri((await Json(decision)).GetProperty("redirect").GetString()!);
        Assert.StartsWith(Callback, redirect.GetLeftPart(UriPartial.Path));
        var query = System.Web.HttpUtility.ParseQueryString(redirect.Query);
        Assert.Equal("xyz", query["state"]);

        async Task<HttpResponseMessage> Token(string code, string v) => await anon.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = Callback,
            ["client_id"] = clientId,
            ["code_verifier"] = v,
        }), Ct);
        var token = await Token(query["code"]!, verifier);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        var body = await Json(token);
        var access = body.GetProperty("access_token").GetString()!;
        Assert.StartsWith("fb_live_", access);
        Assert.Contains("design:apps", body.GetProperty("scope").GetString());
        Assert.Equal("invalid_grant", (await Json(await Token(query["code"]!, verifier))).GetProperty("error").GetString());   // one time

        // The token is an API key of that space: MCP answers it.
        var mcp = _factory.CreateClient();
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        mcp.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        mcp.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        var guide = await mcp.PostAsync("/mcp", new StringContent(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "space_guide", arguments = new { } },
        }), Encoding.UTF8, "application/json"), Ct);
        Assert.Contains("OAuth Space", (await Json(guide)).GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task SpaceApps_Deploy_AZipReplacesTheFolder_DesignersOnly()
    {
        var space = await new SpaceRepository(_db).CreateAsync(Alice, "Deploy Space", Ct);
        using (var sys = _db.CreateSystemConnection())
            sys.Execute("INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@s, @u, 'member', @now)",
                new { s = space.Id, u = Bob, now = DateTime.UtcNow.ToString("o") });
        static ByteArrayContent Zip(params (string Path, string Text)[] files)
        {
            using var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (path, text) in files)
                {
                    using var w = new StreamWriter(zip.CreateEntry(path).Open());
                    w.Write(text);
                }
            return new ByteArrayContent(ms.ToArray());
        }
        var url = $"/api/v1/spaces/{space.Slug}/apps/board/deploy";
        var manifest = "{\"name\":\"Board\",\"tag\":\"board-app\"}";

        Assert.Equal(HttpStatusCode.Forbidden, (await As(Bob).PostAsync(url, Zip(("app.json", manifest), ("app.js", "1")), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As(Alice).PostAsync(url, Zip(("app.js", "1")), Ct)).StatusCode);       // no app.json
        Assert.Equal(HttpStatusCode.BadRequest, (await As(Alice).PostAsync(url, Zip(("app.json", manifest), ("../x.js", "1")), Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await As(Alice).PostAsync(url, Zip(("app.json", manifest), ("app.js", "v1"), ("old.css", "x")), Ct)).StatusCode);
        var deployed = await As(Alice).PostAsync(url, Zip(("app.json", manifest), ("app.js", "v2")), Ct);
        Assert.Equal(2, (await Json(deployed)).GetProperty("files").GetInt32());
        var dir = Path.Combine(_dataDir, "spaces", space.Id, "files", ".apps", "board");
        Assert.Equal("v2", File.ReadAllText(Path.Combine(dir, "app.js")));
        Assert.False(File.Exists(Path.Combine(dir, "old.css")));     // the old version is in the trash
        var desk = await Json(await As(Bob).GetAsync($"/api/v1/spaces/{space.Slug}/desktop", Ct));
        Assert.Contains(desk.GetProperty("apps").EnumerateArray(), a => a.GetProperty("id").GetString() == "space.board");
    }
}
