using System.Text.Json;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// Desktop apps end to end (docs/superpowers/specs/2026-09-26-desktop-apps-design.md),
// against a real app served from a second loopback origin (FixtureAppServer):
// install from a URL through the review dialog (identity defaults to
// nothing), the tile, the sandboxed frame (storage in Files → Apps/<name>,
// the network outside `connect` blocked by the frame's CSP), the update
// found on the next visit and re-pinned through review, permissions,
// remove keeping or deleting the data, the admin's policy hiding every
// install control, and the space owner installing. Every test leaves no
// app behind (other classes count desktop tiles).
[Collection(UiCollection.Name)]
public class AppsTests : IAsyncLifetime
{
    private readonly PlaywrightFixture _fixture;
    private FixtureAppServer _app = null!;
    private static readonly string Shots = Path.Combine(Path.GetTempPath(), "apps-final");

    public AppsTests(PlaywrightFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync()
    {
        _app = new FixtureAppServer();
        Directory.CreateDirectory(Shots);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private string Folder => "Apps/" + FixtureAppServer.AppName;

    private async Task<(IBrowserContext Context, IPage Page, List<string> Errors)> OpenAsync(int width = 1400, int height = 900, bool phone = false)
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = width, Height = height },
            IsMobile = phone,
            HasTouch = phone,
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        return (context, page, errors);
    }

    private static ILocator Tile(IPage page) => page.Locator($"fb-hub-view a.tile[data-key='app:{FixtureAppServer.AppId}']");

    private static async Task MenuAsync(IPage page, string label)
    {
        var tile = Tile(page);
        await tile.HoverAsync();
        await tile.Locator(".tile-menu-btn").ClickAsync();
        await tile.Locator(".tile-menu button[data-action]", new() { HasText = label }).ClickAsync();
    }

    private async Task CleanAsync(IPage page, string? space = null)
    {
        var root = space is null ? "/api/v1/desktop" : $"/api/v1/spaces/{space}/desktop";
        await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}{root}/apps/{FixtureAppServer.AppId}?purgeData=true");
    }

    private async Task<JsonElement?> InstalledAsync(IPage page)
    {
        var desk = (await (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/desktop")).JsonAsync())!.Value;
        foreach (var a in desk.GetProperty("apps").EnumerateArray())
            if (a.GetProperty("id").GetString() == FixtureAppServer.AppId) return a;
        return null;
    }

    private async Task<int> StatAsync(IPage page, string path) =>
        (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/files/stat?path={Uri.EscapeDataString(path)}")).Status;

    private async Task InstallByApiAsync(IPage page, string[] granted)
    {
        var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/desktop/apps", new APIRequestContextOptions
        {
            DataObject = new
            {
                manifestUrl = _app.AppUrl + "app.json",
                manifest = JsonDocument.Parse(_app.Manifest()).RootElement,
                integrity = _app.EntryIntegrity(),
                mode = "sandboxed",
                granted,
            },
        });
        Assert.True(res.Ok, await res.TextAsync());
    }

    [Fact]
    public async Task Apps_InstallOpenUpdateRemove_Test()
    {
        var (context, page, errors) = await OpenAsync();
        await CleanAsync(page);
        try
        {
            // ── Install: the Apps window, the URL, the review. ──
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "apps");
            await page.Locator("#fb-win-apps .wa-bar button[title='Install an app']").ClickAsync();
            var url = page.Locator("sac-dialog[title='Install an app']");
            await url.Locator("#fb-app-url").FillAsync(_app.AppUrl);
            await page.WaitForTimeoutAsync(400);   // the dialog's fade-in, for the picture
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "desk-2-install-url.png") });
            await url.Locator("button[data-action='read']").ClickAsync();

            var review = page.Locator("#fb-app-review");
            await Assertions.Expect(review).ToHaveAttributeAsync("title", $"Install {FixtureAppServer.AppName}?", new() { Timeout = 10000 });
            // Identity starts at "Nothing", whatever the manifest asks; files is on offer.
            await Assertions.Expect(review.Locator("#fb-app-identity")).ToHaveValueAsync("none");
            // No "anonymous id" on offer while the kit's would hand the name over.
            await Assertions.Expect(review.Locator("#fb-app-identity option[value='pseudonymous']")).ToHaveCountAsync(0);
            await Assertions.Expect(review.Locator("#fb-app-files")).ToBeCheckedAsync();
            // The data folder the app will use is shown.
            await Assertions.Expect(review.Locator("code", new() { HasText = "Apps/" })).ToHaveCountAsync(1);
            await Assertions.Expect(review.Locator("input[name='fb-app-mode']")).ToHaveCountAsync(0);   // one mode: sandboxed
            await page.WaitForTimeoutAsync(400);   // the dialog's fade-in, for the picture
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "desk-3-review.png") });
            await review.Locator("button[data-action='ok']").ClickAsync();
            await WindowApp.CloseAllAsync(page);   // the Apps window, back to the desktop

            // ── The tile, SACRVM Desktop's origin line. ──
            await Assertions.Expect(Tile(page)).ToBeVisibleAsync(new() { Timeout = 10000 });
            await Assertions.Expect(Tile(page).Locator(".tile-meta")).ToContainTextAsync("v1.0.0 · sandboxed");
            var installed = (await InstalledAsync(page))!.Value;
            Assert.Equal(_app.EntryIntegrity(), installed.GetProperty("entryIntegrity").GetString());
            Assert.Equal("files", installed.GetProperty("granted")[0].GetString());
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "desk-1-desktop.png") });

            // ── Open: the sandboxed frame. ──
            await Tile(page).ClickAsync();
            var frame = page.FrameLocator("sac-window iframe");
            await Assertions.Expect(frame.Locator("#hello")).ToHaveTextAsync("hello from fixture 1.0.0", new() { Timeout = 15000 });
            await Assertions.Expect(frame.Locator("#granted")).ToHaveTextAsync("identity=false files=scoped isolated=true");
            await Assertions.Expect(frame.Locator("#stored")).ToHaveTextAsync("stored {\"n\":1}", new() { Timeout = 10000 });
            await Assertions.Expect(frame.Locator("#net")).ToHaveTextAsync("net blocked", new() { Timeout = 10000 });
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "desk-8-app-open.png") });
            // Its storage is a real folder in Files.
            Assert.Equal(200, await StatAsync(page, Folder + "/.fishbowl-app.json"));

            // ── Update: new code on the next visit, re-pinned through review. ──
            _app.Version = "2.0.0";
            await page.ReloadAsync();
            await Assertions.Expect(Tile(page)).ToBeVisibleAsync();
            var tile = Tile(page);
            await tile.HoverAsync();
            await tile.Locator(".tile-menu-btn").ClickAsync();
            var updateItem = tile.Locator(".tile-menu button[data-action='app:update']");
            await Assertions.Expect(updateItem).ToHaveTextAsync("Update to v2.0.0…", new() { Timeout = 10000 });
            await page.WaitForTimeoutAsync(400);   // the dialog's fade-in, for the picture
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "desk-4-app-menu.png") });
            await updateItem.ClickAsync();
            await Assertions.Expect(review).ToHaveAttributeAsync("title", $"Update {FixtureAppServer.AppName} to v2.0.0?");
            await page.WaitForTimeoutAsync(400);   // the dialog's fade-in, for the picture
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "desk-5-update.png") });
            await review.Locator("button[data-action='ok']").ClickAsync();
            await Assertions.Expect(Tile(page).Locator(".tile-meta")).ToContainTextAsync("v2.0.0", new() { Timeout = 10000 });
            Assert.Equal(_app.EntryIntegrity(), (await InstalledAsync(page))!.Value.GetProperty("entryIntegrity").GetString());

            // ── Permissions: take storage away. ──
            await MenuAsync(page, "Permissions…");
            await review.Locator("#fb-app-files").UncheckAsync();
            await review.Locator("button[data-action='ok']").ClickAsync();
            // Polled from here: WaitForFunctionAsync evaluates a string, which
            // the page's CSP refuses (and this file keeps the CSP on — the
            // frame's policy is what it tests).
            for (var i = 0; !await page.EvaluateAsync<bool>("async () => { const r = await fetch('/api/v1/desktop'); const d = await r.json(); return d.apps.some(a => a.id === 'fixture-kanban' && a.granted.length === 0); }"); i++)
            {
                Assert.True(i < 50, "the grant change never reached the server");
                await page.WaitForTimeoutAsync(100);
            }

            // ── Settings → Apps lists it. ──
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "apps");
            var row = page.Locator($"fb-apps-settings-view .fb-row[data-app='{FixtureAppServer.AppId}']");
            await Assertions.Expect(row).ToContainTextAsync("v2.0.0");
            await Assertions.Expect(row.Locator("sac-chip")).ToHaveAttributeAsync("label", "sandboxed");
            await Assertions.Expect(page.Locator("#fb-win-apps .wa-bar button[title='Install an app']")).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "desk-7-settings-apps.png") });
            await WindowApp.CloseAllAsync(page);

            // ── Remove, keeping the data. ──
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await MenuAsync(page, "Remove from this desktop");
            var confirm = page.Locator($"sac-dialog[title='Remove {FixtureAppServer.AppName}?']");
            await Assertions.Expect(confirm).ToContainTextAsync("Apps/" + FixtureAppServer.AppName);
            await page.WaitForTimeoutAsync(400);   // the dialog's fade-in, for the picture
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "desk-6-remove.png") });
            await confirm.Locator("button[data-action='keep']").ClickAsync();
            await Assertions.Expect(Tile(page)).ToHaveCountAsync(0, new() { Timeout = 10000 });
            Assert.Null(await InstalledAsync(page));
            Assert.Equal(200, await StatAsync(page, Folder + "/.fishbowl-app.json"));

            // ── Back again, then removed with its data (to the trash). ──
            await InstallByApiAsync(page, new[] { "files" });
            await page.ReloadAsync();
            await MenuAsync(page, "Remove from this desktop");
            await confirm.Locator("button[data-action='purge']").ClickAsync();
            await Assertions.Expect(Tile(page)).ToHaveCountAsync(0, new() { Timeout = 10000 });
            Assert.Equal(404, await StatAsync(page, Folder + "/.fishbowl-app.json"));
            Assert.Empty(errors);
        }
        finally
        {
            await CleanAsync(page);
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Apps_WithoutFilesGrant_RunWithoutStorage_Test()
    {
        var (context, page, _) = await OpenAsync();
        await CleanAsync(page);
        try
        {
            await InstallByApiAsync(page, new[] { "identity:pseudonymous" });
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Tile(page).ClickAsync();
            var frame = page.FrameLocator("sac-window iframe");
            // A pseudonymous grant hands nothing while the kit's pseudonymous
            // mode still passes the name and picture (desktop-apps.js ANON).
            await Assertions.Expect(frame.Locator("#granted")).ToHaveTextAsync("identity=false files=false isolated=true", new() { Timeout = 15000 });
            await Assertions.Expect(frame.Locator("#stored")).ToContainTextAsync("no storage", new() { Timeout = 10000 });
            Assert.Equal(404, await StatAsync(page, Folder + "/.fishbowl-app.json"));
        }
        finally
        {
            await CleanAsync(page);
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Apps_PolicyOff_NoInstallControls_SpaceOwnerInstalls_Test()
    {
        var (context, page, _) = await OpenAsync();
        string? slug = null;
        try
        {
            var off = await page.APIRequest.PutAsync($"{_fixture.BaseUrl}/api/v1/admin/config/Apps:Install", new() { DataObject = new { value = "off" } });
            Assert.True(off.Ok, await off.TextAsync());
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[data-key='builtin:notes']")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#fb-install-tile")).ToHaveCountAsync(0);
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "apps");
            await Assertions.Expect(page.Locator("#fb-apps-policy")).ToContainTextAsync("turned off on this Fishbowl");
            await Assertions.Expect(page.Locator("#fb-win-apps .wa-bar button[title='Install an app']")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("fb-apps-settings-view .empty-state button")).ToHaveCountAsync(0);
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/admin/config/Apps:Install");
            await WindowApp.CloseAllAsync(page);

            // A space: its owner installs, sandboxed only.
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Apps " + Guid.NewGuid().ToString("N")[..6] } });
            slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString();
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "apps", $"#/space/{slug}/");
            await page.Locator("#fb-win-apps .wa-bar button[title='Install an app']").ClickAsync();
            var url = page.Locator("sac-dialog[title='Install an app']");
            await url.Locator("#fb-app-url").FillAsync(_app.AppUrl);
            await url.Locator("button[data-action='read']").ClickAsync();
            var review = page.Locator("#fb-app-review");
            await Assertions.Expect(review).ToContainTextAsync("every member sees it", new() { Timeout = 10000 });
            await page.WaitForTimeoutAsync(400);   // the dialog's fade-in, for the picture
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "desk-3b-review-space.png") });
            await review.Locator("button[data-action='cancel']").ClickAsync();
        }
        finally
        {
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/admin/config/Apps:Install");
            if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Apps_SpaceAppTiles_TileJsAndContextTileSet_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Tiles " + Guid.NewGuid().ToString("N")[..6] } });
            var slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString();
            async Task Put(string path, string content)
            {
                var put = await page.APIRequest.PutAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/files/content?path={Uri.EscapeDataString(path)}&parents=1",
                    new APIRequestContextOptions
                    {
                        Headers = new Dictionary<string, string> { ["X-Fishbowl-Upload"] = "1", ["If-None-Match"] = "*" },
                        DataByte = System.Text.Encoding.UTF8.GetBytes(content),
                    });
                Assert.True(put.Ok, $"PUT {path}: {put.Status} {await put.TextAsync()}");
            }
            await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/tables", new() { DataObject = new { name = "cards", columns = Array.Empty<object>() } });
            foreach (var title in new[] { "Plan", "Ship" })
                await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/tables/cards/rows", new() { DataObject = new { title } });

            // A tile.js: the server runs it, the tile starts medium and shows it.
            await Put(".apps/kanban/app.json", "{\"name\":\"Kanban\",\"tag\":\"kanban-tile-app\",\"icon\":\"grid\"}");
            await Put(".apps/kanban/app.js", "customElements.define('kanban-tile-app', class extends HTMLElement { mount() { this.textContent = 'kanban'; } });");
            await Put(".apps/kanban/tile.js", """
                function tile(ctx) {
                  return { main: ctx.count('cards') + ' cards', items: ctx.query('cards', {}).map(c => ({ text: c.title })) };
                }
                """);
            // No tile.js: the app hands its tile over while it runs.
            await Put(".apps/pusher/app.json", "{\"name\":\"Pusher\",\"tag\":\"pusher-tile-app\"}");
            await Put(".apps/pusher/app.js", """
                customElements.define('pusher-tile-app', class extends HTMLElement {
                  async mount(context) {
                    await context.tile.set({ main: 'pushed main', items: [{ text: 'pushed row', tail: 'now' }] });
                    this.innerHTML = '<p id=done>set</p>';
                  }
                });
                """);
            await page.APIRequest.PutAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/desktop/tiles/app:space.pusher",
                new() { DataObject = new { position = (double?)null, size = "medium", color = (string?)null, hidden = false } });

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/");
            var kanban = page.Locator("fb-hub-view a.tile[data-key='app:space.kanban']");
            await Assertions.Expect(kanban.Locator(".tl-main")).ToHaveTextAsync("2 cards", new() { Timeout = 15000 });
            await Assertions.Expect(kanban.Locator(".tl-status > h2")).ToHaveTextAsync("Kanban");
            await Assertions.Expect(kanban.Locator(".tl-items li")).ToHaveCountAsync(2);
            // Until arranged, the space's apps come right after the full-screen apps.
            var order = await page.Locator("fb-hub-view a.tile[data-key]").EvaluateAllAsync<string[]>("els => els.map(e => e.dataset.key)");
            Assert.True(Array.IndexOf(order, "app:space.kanban") < Array.IndexOf(order, "builtin:messages"), string.Join(", ", order));
            Assert.True(Array.IndexOf(order, "app:space.kanban") > Array.IndexOf(order, "builtin:tables"), string.Join(", ", order));

            // Plain until the app ran; then what it set, also after a reload.
            var pusher = page.Locator("fb-hub-view a.tile[data-key='app:space.pusher']");
            await Assertions.Expect(pusher.Locator(".tl-main")).ToHaveCountAsync(0);
            await pusher.ClickAsync();
            await Assertions.Expect(page.FrameLocator("sac-window iframe").Locator("#done")).ToHaveTextAsync("set", new() { Timeout = 15000 });
            await WindowApp.CloseAllAsync(page);
            await Assertions.Expect(pusher.Locator(".tl-main")).ToHaveTextAsync("pushed main", new() { Timeout = 10000 });
            await Assertions.Expect(pusher.Locator(".tl-text")).ToHaveTextAsync("pushed row");
            await page.ReloadAsync();
            await Assertions.Expect(pusher.Locator(".tl-main")).ToHaveTextAsync("pushed main", new() { Timeout = 15000 });
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Apps_SpaceApp_FromDotApps_OpensSandboxed_Test()
    {
        var (context, page, errors) = await OpenAsync();
        string? slug = null;
        try
        {
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Code " + Guid.NewGuid().ToString("N")[..6] } });
            slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString();
            async Task Put(string path, string content)
            {
                var put = await page.APIRequest.PutAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/files/content?path={Uri.EscapeDataString(path)}&parents=1",
                    new APIRequestContextOptions
                    {
                        Headers = new Dictionary<string, string> { ["X-Fishbowl-Upload"] = "1", ["If-None-Match"] = "*" },
                        DataByte = System.Text.Encoding.UTF8.GetBytes(content),
                    });
                Assert.True(put.Ok, $"PUT {path}: {put.Status} {await put.TextAsync()}");
            }
            await Put(".apps/hello/app.json", "{\"name\":\"Hello\",\"tag\":\"hello-space-app\",\"version\":\"0.1.0\",\"icon\":\"star\"}");
            // The app reads and writes the space's data over the bridge (context.space).
            var table = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/tables",
                new() { DataObject = new { name = "tasks", columns = Array.Empty<object>() } });
            Assert.True(table.Ok, await table.TextAsync());
            await Put(".apps/hello/app.js", """
                customElements.define('hello-space-app', class extends HTMLElement {
                  async mount(context) {
                    this.innerHTML = '<p id=hi>hello from .apps</p><p id=data></p>';
                    await context.space.insert('tasks', { title: 'from the app' });
                    const r = await context.space.query('tasks', {});
                    const rows = Array.isArray(r) ? r : (r.rows || r.items || []);
                    let refused = '';
                    try { await context.space.describe('nope'); } catch (e) { refused = e.code; }
                    await context.space.error('boom from the app', 'detail');
                    const members = await context.space.members();
                    await context.space.notify([members.items.find((m) => m.you).userId], 'hello members');
                    this.querySelector('#data').textContent = `${rows.length} ${rows[0]?.title} ${refused}`;
                    setTimeout(() => { throw new Error('uncaught in the frame'); });
                  }
                });
                """);

            // A tile on the space's desktop, opened in the sandboxed frame.
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/");
            var tile = page.Locator("fb-hub-view a.tile[data-key='app:space.hello']");
            await Assertions.Expect(tile).ToBeVisibleAsync(new() { Timeout = 15000 });
            await tile.ClickAsync();
            var frame = page.FrameLocator("sac-window iframe");
            await Assertions.Expect(frame.Locator("#hi")).ToHaveTextAsync("hello from .apps", new() { Timeout = 15000 });
            await Assertions.Expect(frame.Locator("#data")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^1 from the app [a-z-]+$"), new() { Timeout = 15000 });
            Assert.Equal(1, (await (await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/tables/tasks/count",
                new() { DataObject = new { } })).JsonAsync())!.Value.GetProperty("count").GetInt32());
            Assert.Equal("allow-scripts allow-forms allow-popups allow-downloads",
                await page.Locator("sac-window iframe").GetAttributeAsync("sandbox"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "desk-5-space-app.png") });

            // The Apps window lists it, with nothing to update or remove.
            await WindowApp.CloseAllAsync(page);
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "apps", $"#/space/{slug}/");
            var row = page.Locator("fb-apps-settings-view .fb-row[data-app='space.hello']");
            await Assertions.Expect(row).ToContainTextAsync(".apps/hello · v0.1.0");
            await Assertions.Expect(row.Locator("button[data-action]")).ToHaveCountAsync(0);
            // Its Designer sees what broke: the refused call, its own report and
            // the uncaught error the kit forwards from the frame (sac:app-error).
            var errorsCard = page.Locator("#fb-apps-errors-card");
            await Assertions.Expect(errorsCard).ToContainTextAsync("boom from the app");
            await Assertions.Expect(errorsCard).ToContainTextAsync("describe:");
            await Assertions.Expect(errorsCard).ToContainTextAsync("uncaught in the frame");
            await errorsCard.Locator("button[data-action='clear-errors']").ClickAsync();
            await Assertions.Expect(errorsCard).ToContainTextAsync("No errors.");

            // Its message is in the inbox as "Space · App"; muting it is one menu away.
            await WindowApp.CloseAllAsync(page);
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "messages", $"#/space/{slug}/");
            var msg = page.Locator("fb-messages-view .msg-row[data-kind='app.message']", new() { HasText = "hello members" });
            await Assertions.Expect(msg).ToContainTextAsync("Hello");
            await msg.Locator(".msg-mute .icon-btn").ClickAsync();
            await msg.Locator(".msg-mute button[data-action='mute-app']").ClickAsync();
            var mutedBtn = page.Locator("#fb-messages-muted");
            await Assertions.Expect(mutedBtn).ToBeVisibleAsync();
            await mutedBtn.ClickAsync();
            var dialog = page.Locator("#fb-muted-dialog");
            await Assertions.Expect(dialog).ToContainTextAsync("hello");
            await dialog.Locator("button[data-action='unmute']").ClickAsync();
            await Assertions.Expect(mutedBtn).ToHaveCountAsync(0);
            // The one page error is the app's own, thrown on purpose above.
            Assert.DoesNotContain(errors, (e) => !e.Contains("uncaught in the frame"));
        }
        finally
        {
            if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Apps_Phone_Screenshots_Test()
    {
        var (context, page, _) = await OpenAsync(390, 800, phone: true);
        await CleanAsync(page);
        try
        {
            await InstallByApiAsync(page, new[] { "files" });
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Tile(page)).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "phone-1-desktop.png") });
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "apps");
            await page.Locator("#fb-win-apps .wa-bar button[title='Install an app']").ClickAsync();
            var url = page.Locator("sac-dialog[title='Install an app']");
            await url.Locator("#fb-app-url").FillAsync(_app.AppUrl);
            await page.WaitForTimeoutAsync(400);   // the dialog's fade-in, for the picture
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "phone-2-install-url.png") });
            await url.Locator("button[data-action='cancel']").ClickAsync();
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "apps");
            await Assertions.Expect(page.Locator($"fb-apps-settings-view .fb-row[data-app='{FixtureAppServer.AppId}']")).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "phone-7-settings-apps.png") });
            await WindowApp.CloseAllAsync(page);
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Tile(page).ClickAsync();
            await Assertions.Expect(page.FrameLocator("sac-window iframe").Locator("#net")).ToHaveTextAsync("net blocked", new() { Timeout = 15000 });
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "phone-8-app-open.png") });
        }
        finally
        {
            await CleanAsync(page);
            await context.CloseAsync();
        }
    }
}
