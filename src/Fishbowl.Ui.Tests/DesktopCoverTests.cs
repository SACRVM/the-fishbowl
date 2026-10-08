using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The desktop's cover tile (fb-hub-view's .fb-cover): the grid's first,
// two-column tile with the workspace's name and the version as a catalogue
// number, the About window behind a click / Enter / Space, a still scene under
// prefers-reduced-motion, a card on a phone. Also the logo file the page
// icons and the login page share (/img/fishbowl.svg).
[Collection(UiCollection.Name)]
public class DesktopCoverTests
{
    private readonly PlaywrightFixture _fixture;

    public DesktopCoverTests(PlaywrightFixture fixture) => _fixture = fixture;

    private static BrowserNewContextOptions Desktop(ReducedMotion? motion = null) => new()
    {
        IgnoreHTTPSErrors = true,
        BypassCSP = true,
        ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        ReducedMotion = motion,
    };

    private static ILocator Cover(IPage page) => page.Locator("fb-hub-view #fb-tiles > a.fb-cover");

    private async Task<string> VersionAsync(IPage page)
    {
        var res = await page.APIRequest.GetAsync(_fixture.BaseUrl + "/api/v1/version");
        return (await res.JsonAsync())!.Value.GetProperty("version").GetString()!;
    }

    [Fact]
    public async Task Cover_IsTheFirstTile_AndSpansTwoColumns_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(Desktop());
        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[data-key='builtin:notes']")).ToBeVisibleAsync(new() { Timeout = 15000 });

            // The first child of the grid, a kit tile — and no page heading any more.
            Assert.True(await page.Locator("fb-hub-view #fb-tiles").EvaluateAsync<bool>(
                "g => g.firstElementChild.classList.contains('fb-cover') && g.firstElementChild.classList.contains('tile')"));
            Assert.Equal(0, await page.Locator("fb-hub-view header, fb-hub-view h1").CountAsync());
            // Not an app: no context menu.
            await Cover(page).ClickAsync(new() { Button = MouseButton.Right });
            await page.WaitForTimeoutAsync(300);
            Assert.Equal(0, await page.Locator("sac-menu.sac-context-menu[open]").CountAsync());
            await page.Mouse.MoveAsync(0, 0);           // off the cover: no hover lift in what's measured next
            await page.WaitForTimeoutAsync(400);

            // Two columns × one row: twice a medium tile plus the gap, as tall as one column is wide.
            Assert.Equal("span 2", await Cover(page).EvaluateAsync<string>("c => getComputedStyle(c).gridColumnStart"));
            var cover = (await Cover(page).BoundingBoxAsync())!;
            var notes = (await page.Locator("fb-hub-view a.tile[data-key='builtin:notes']").BoundingBoxAsync())!;
            var gap = await page.Locator("fb-hub-view #fb-tiles").EvaluateAsync<double>("g => parseFloat(getComputedStyle(g).columnGap)");
            Assert.InRange(cover.Width, 2 * notes.Width + gap - 2, 2 * notes.Width + gap + 2);
            Assert.InRange(cover.Height, notes.Width - 2, notes.Width + 2);

            // It sits left of the first app tile, in the grid's first row.
            Assert.InRange(cover.Y, notes.Y - 2, notes.Y + 2);
            Assert.True(notes.X > cover.X + cover.Width);

            // A button for assistive tech, named like the kit's About title.
            await Assertions.Expect(Cover(page)).ToHaveAttributeAsync("role", "button");
            await Assertions.Expect(Cover(page)).ToHaveAttributeAsync("aria-label", "About The Fishbowl");

            // Its type never leaves the tile, and the fish's lane keeps clear of it.
            var overflow = await Cover(page).EvaluateAsync<bool>(
                "c => [...c.querySelectorAll('.fc-brand,.fc-cat,.fc-name')].some(e => { const r = e.getBoundingClientRect(), t = c.getBoundingClientRect(); return r.left < t.left || r.right > t.right || r.top < t.top || r.bottom > t.bottom; })");
            Assert.False(overflow);
        }
        finally { await context.CloseAsync(); }
    }

    [Fact]
    public async Task Cover_ShowsThePersonalWorkspaceName_AndASpacesName_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(Desktop());
        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Cover(page).Locator(".fc-name")).ToHaveTextAsync("Personal", new() { Timeout = 15000 });
            // The brand line stays; there is no "workspace" label and no tagline.
            await Assertions.Expect(Cover(page).Locator(".fc-brand")).ToHaveTextAsync("THE FISHBOWL");
            Assert.DoesNotContain("Your memory lives here", await Cover(page).InnerTextAsync());
            Assert.DoesNotContain("orkspace", await Cover(page).InnerTextAsync());

            var name = "Cover space " + Guid.NewGuid().ToString("N")[..6];
            var created = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/spaces", new APIRequestContextOptions { DataObject = new { name } });
            Assert.True(created.Ok, await created.TextAsync());
            var slug = (await created.JsonAsync())!.Value.GetProperty("slug").GetString()!;

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/");
            await Assertions.Expect(Cover(page).Locator(".fc-name")).ToHaveTextAsync(name, new() { Timeout = 15000 });

            // Switching back through the switcher: the cover follows without a reload.
            await page.Locator("#fb-context button[slot='trigger']").ClickAsync();
            await page.Locator("#fb-context [data-action='ctx:user']").ClickAsync();
            await Assertions.Expect(Cover(page).Locator(".fc-name")).ToHaveTextAsync("Personal", new() { Timeout = 5000 });
        }
        finally { await context.CloseAsync(); }
    }

    [Fact]
    public async Task Cover_CatalogueNumber_ShowsTheVersion_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(Desktop());
        try
        {
            var page = await context.NewPageAsync();
            var version = await VersionAsync(page);
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Cover(page).Locator(".fc-cat")).ToHaveTextAsync($"FB · {version}", new() { Timeout = 15000 });
        }
        finally { await context.CloseAsync(); }
    }

    [Fact]
    public async Task Cover_ClickEnterAndSpace_OpenTheAbout_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(Desktop());
        try
        {
            var page = await context.NewPageAsync();
            var version = await VersionAsync(page);
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Cover(page).Locator(".fc-cat")).ToHaveTextAsync($"FB · {version}", new() { Timeout = 15000 });

            var about = page.Locator("sac-window[data-about] .sac-about");
            async Task ExpectAboutAndCloseAsync()
            {
                await Assertions.Expect(about).ToBeVisibleAsync(new() { Timeout = 5000 });
                await Assertions.Expect(about).ToContainTextAsync("The Fishbowl");
                await Assertions.Expect(about).ToContainTextAsync($"v{version}");
                await Assertions.Expect(about).ToContainTextAsync("Your memory lives here. You don't.");
                await Assertions.Expect(about).ToContainTextAsync("Fishbowl contributors");
                await Assertions.Expect(about).ToContainTextAsync("AGPL-3.0");
                // The bundled parts, each with its licence.
                await Assertions.Expect(about).ToContainTextAsync("SACRVM APPKIT");
                await Assertions.Expect(about).ToContainTextAsync("Jint");
                await Assertions.Expect(about).ToContainTextAsync("BSD-2-Clause");
                // The hash didn't move: the cover is not a link anywhere.
                Assert.EndsWith("#/", page.Url);
                await page.EvaluateAsync("() => document.querySelectorAll('sac-window[data-about]').forEach((w) => w.remove())");
                await Assertions.Expect(about).ToHaveCountAsync(0);
            }

            await Cover(page).ClickAsync();
            await ExpectAboutAndCloseAsync();

            await Cover(page).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await ExpectAboutAndCloseAsync();

            await Cover(page).FocusAsync();
            var scrollBefore = await page.EvaluateAsync<double>("() => window.scrollY");
            await page.Keyboard.PressAsync("Space");
            await ExpectAboutAndCloseAsync();
            Assert.Equal(scrollBefore, await page.EvaluateAsync<double>("() => window.scrollY"));
        }
        finally { await context.CloseAsync(); }
    }

    [Fact]
    public async Task Cover_ReducedMotion_FishHasNoRunningAnimation_Test()
    {
        const string running = "() => [...document.querySelectorAll('.fb-cover .fc-fish')]"
            + ".flatMap(f => f.getAnimations({ subtree: true })).filter(a => a.playState === 'running').length";
        const string runningAnywhere = "() => document.querySelector('.fb-cover').getAnimations({ subtree: true }).filter(a => a.playState === 'running').length";

        // The control: with motion allowed the fish (across, bob, turn, tail) is swimming.
        var moving = await _fixture.Browser!.NewContextAsync(Desktop());
        try
        {
            var page = await moving.NewPageAsync();
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Cover(page)).ToBeVisibleAsync(new() { Timeout = 15000 });
            Assert.True(await page.EvaluateAsync<int>(running) >= 3, "the fish should be animated by default");
        }
        finally { await moving.CloseAsync(); }

        var still = await _fixture.Browser!.NewContextAsync(Desktop(ReducedMotion.Reduce));
        try
        {
            var page = await still.NewPageAsync();
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Cover(page)).ToBeVisibleAsync(new() { Timeout = 15000 });
            await Assertions.Expect(Cover(page).Locator(".fc-name")).ToHaveTextAsync("Personal", new() { Timeout = 15000 });
            Assert.Equal(0, await page.EvaluateAsync<int>(running));
            Assert.Equal(0, await page.EvaluateAsync<int>(runningAnywhere));
            // The fish is still there, resting inside the tile.
            var fish = (await Cover(page).Locator(".fc-fish").BoundingBoxAsync())!;
            var cover = (await Cover(page).BoundingBoxAsync())!;
            Assert.True(fish.X > cover.X && fish.X + fish.Width < cover.X + cover.Width);
        }
        finally { await still.CloseAsync(); }
    }

    [Fact]
    public async Task Cover_OnAPhone_IsACard_WithoutHorizontalScroll_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = 375, Height = 740 },
            IsMobile = true,
            HasTouch = true,
        });
        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Cover(page).Locator(".fc-name")).ToHaveTextAsync("Personal", new() { Timeout = 15000 });
            await Assertions.Expect(Cover(page).Locator(".fc-cat")).ToContainTextAsync("FB · ", new() { Timeout = 15000 });
            await page.WaitForTimeoutAsync(300);

            var box = (await Cover(page).BoundingBoxAsync())!;
            Assert.InRange(box.Height, 178, 260);      // a card, not a screen-wide square
            Assert.InRange(box.Width, 330, 375);
            var overflow = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
            Assert.Equal(0, overflow);

            // Nothing of its type runs out of the card, and no two lines of it meet.
            var clash = await Cover(page).EvaluateAsync<bool>(
                "c => { const t = c.getBoundingClientRect(); const r = ['.fc-brand', '.fc-cat', '.fc-name'].map(s => c.querySelector(s).getBoundingClientRect());"
                + " const [b, v, n] = r; return r.some(x => x.left < t.left || x.right > t.right || x.bottom > t.bottom) || b.right > v.left || b.bottom > n.top; }");
            Assert.False(clash);
        }
        finally { await context.CloseAsync(); }
    }

    [Fact]
    public async Task Logo_IsServedAsSvg_TheLoginPageShowsIt_AndThePagesCarryTheIcon_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(Desktop());
        try
        {
            var page = await context.NewPageAsync();

            var svg = await page.APIRequest.GetAsync(_fixture.BaseUrl + "/img/fishbowl.svg");
            Assert.Equal(200, svg.Status);
            Assert.StartsWith("image/svg+xml", svg.Headers["content-type"]);
            var body = await svg.TextAsync();
            Assert.Contains("viewBox=\"0 0 120 120\"", body);
            Assert.DoesNotContain("<script", body);

            // The sign-in page: the logo as an image, loaded (not the old inline drawing).
            await page.GotoAsync(_fixture.BaseUrl + "/login");
            var logo = page.Locator("img.fb-fishbowl");
            await Assertions.Expect(logo).ToHaveAttributeAsync("src", "/img/fishbowl.svg");
            await Assertions.Expect(page.Locator("svg.fb-fishbowl")).ToHaveCountAsync(0);
            await page.WaitForFunctionAsync("() => { const i = document.querySelector('img.fb-fishbowl'); return i && i.complete && i.naturalWidth > 0; }");
            var logoBox = (await logo.BoundingBoxAsync())!;
            Assert.InRange(logoBox.Width, 80, 96);
            await Assertions.Expect(page.Locator("link[rel='icon']")).ToHaveAttributeAsync("href", "/img/fishbowl.svg");
            await Assertions.Expect(page.Locator("link[rel='icon']")).ToHaveAttributeAsync("type", "image/svg+xml");

            // The app shell and the other server-rendered pages carry the same icon.
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(page.Locator("link[rel='icon']")).ToHaveAttributeAsync("href", "/img/fishbowl.svg");
            foreach (var path in new[] { "/pending", "/oauth/authorize" })
            {
                var res = await page.APIRequest.GetAsync(_fixture.BaseUrl + path);
                if (res.Status == 200 && (res.Headers.GetValueOrDefault("content-type") ?? "").Contains("html"))
                    Assert.Contains("rel=\"icon\" type=\"image/svg+xml\" href=\"/img/fishbowl.svg\"", await res.TextAsync());
            }
        }
        finally { await context.CloseAsync(); }
    }
}
