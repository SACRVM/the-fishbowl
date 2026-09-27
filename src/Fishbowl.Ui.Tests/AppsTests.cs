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
// install control, and trusted refused in a space. Every test leaves no
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
            // ── Install: the dashed tile, the URL, the review. ──
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await page.Locator("#fb-install-tile").ClickAsync();
            var url = page.Locator("sac-dialog[title='Install an app']");
            await url.Locator("#fb-app-url").FillAsync(_app.AppUrl);
            await page.WaitForTimeoutAsync(400);   // the dialog's fade-in, for the picture
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "desk-2-install-url.png") });
            await url.Locator("button[data-action='read']").ClickAsync();

            var review = page.Locator("#fb-app-review");
            await Assertions.Expect(review).ToHaveAttributeAsync("title", $"Install {FixtureAppServer.AppName}?", new() { Timeout = 10000 });
            // Identity starts at "Nothing", whatever the manifest asks; files is on offer.
            await Assertions.Expect(review.Locator("#fb-app-identity")).ToHaveValueAsync("none");
            await Assertions.Expect(review.Locator("#fb-app-files")).ToBeCheckedAsync();
            await Assertions.Expect(review.Locator("input[value='trusted']")).ToBeEnabledAsync();   // the test user is an admin
            await page.WaitForTimeoutAsync(400);   // the dialog's fade-in, for the picture
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "desk-3-review.png") });
            await review.Locator("button[data-action='ok']").ClickAsync();

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
            await Assertions.Expect(frame.Locator("#granted")).ToHaveTextAsync("identity=false isolated=true");
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
            await page.WaitForFunctionAsync("async () => { const r = await fetch('/api/v1/desktop'); const d = await r.json(); return d.apps.some(a => a.id === 'fixture-kanban' && a.granted.length === 0); }");

            // ── Settings → Apps lists it. ──
            await page.GotoAsync(_fixture.BaseUrl + "/#/apps");
            var row = page.Locator($"fb-apps-settings-view .fb-row[data-app='{FixtureAppServer.AppId}']");
            await Assertions.Expect(row).ToContainTextAsync("v2.0.0");
            await Assertions.Expect(row.Locator("sac-chip")).ToHaveAttributeAsync("label", "sandboxed");
            await Assertions.Expect(page.Locator("#fb-view-toolbar button[title='Install an app']")).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "desk-7-settings-apps.png") });

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
            await Assertions.Expect(frame.Locator("#granted")).ToHaveTextAsync("identity=pseudonymous isolated=true", new() { Timeout = 15000 });
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
    public async Task Apps_PolicyOff_NoInstallControls_TrustedNotInASpace_Test()
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
            await page.GotoAsync(_fixture.BaseUrl + "/#/apps");
            await Assertions.Expect(page.Locator("#fb-apps-policy")).ToContainTextAsync("turned it off");
            await Assertions.Expect(page.Locator("#fb-view-toolbar button[title='Install an app']")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("fb-apps-settings-view .empty-state button")).ToHaveCountAsync(0);
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/admin/config/Apps:Install");

            // A space: its owner installs, sandboxed only.
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Apps " + Guid.NewGuid().ToString("N")[..6] } });
            slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString();
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/");
            await page.Locator("#fb-install-tile").ClickAsync();
            var url = page.Locator("sac-dialog[title='Install an app']");
            await url.Locator("#fb-app-url").FillAsync(_app.AppUrl);
            await url.Locator("button[data-action='read']").ClickAsync();
            var review = page.Locator("#fb-app-review");
            await Assertions.Expect(review.Locator("input[value='trusted']")).ToBeDisabledAsync(new() { Timeout = 10000 });
            await Assertions.Expect(review).ToContainTextAsync("every member sees it");
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
    public async Task Apps_Phone_Screenshots_Test()
    {
        var (context, page, _) = await OpenAsync(390, 800, phone: true);
        await CleanAsync(page);
        try
        {
            await InstallByApiAsync(page, new[] { "files" });
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Tile(page)).ToBeVisibleAsync();
            await page.Locator("#fb-install-tile").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "phone-1-desktop.png") });
            await page.Locator("#fb-install-tile").ClickAsync();
            var url = page.Locator("sac-dialog[title='Install an app']");
            await url.Locator("#fb-app-url").FillAsync(_app.AppUrl);
            await page.WaitForTimeoutAsync(400);   // the dialog's fade-in, for the picture
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "phone-2-install-url.png") });
            await url.Locator("button[data-action='cancel']").ClickAsync();
            await page.GotoAsync(_fixture.BaseUrl + "/#/apps");
            await Assertions.Expect(page.Locator($"fb-apps-settings-view .fb-row[data-app='{FixtureAppServer.AppId}']")).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, "phone-7-settings-apps.png") });
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
