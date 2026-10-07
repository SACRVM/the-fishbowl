using Dapper;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The desktop in the browser: SACRVM Desktop's tiles and tile menu on the
// kit's .grid, fed from fb.desktop's registry; their arrangement (size,
// hide — no colour per tile) stored per workspace on the server, hidden tiles offered
// back in the toolbar, and the Ctrl/⌘K palette. Every test that
// arranges puts the personal desktop back, so the hub smoke test sees the
// defaults.
[Collection(UiCollection.Name)]
public class DesktopTests
{
    private const string TestUser = "test-internal-id";
    private static readonly string[] Builtins =
        { "builtin:notes", "builtin:todos", "builtin:calendar", "builtin:files", "builtin:messages", "builtin:users", "builtin:settings" };

    private readonly PlaywrightFixture _fixture;

    public DesktopTests(PlaywrightFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(IBrowserContext Context, IPage Page, List<string> Errors)> OpenAsync(BrowserNewContextOptions? options = null)
    {
        var context = await _fixture.Browser!.NewContextAsync(options ?? new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        page.Console += (_, m) => { if (m.Type == "error" && !m.Text.StartsWith("Failed to load resource")) errors.Add(m.Text); };
        return (context, page, errors);
    }

    private static ILocator Cell(IPage page, string key) => page.Locator($"fb-hub-view a.tile[data-key='{key}']");

    private static ILocator ShowHiddenButton(IPage page) => page.Locator("#fb-view-toolbar button[title^='Show hidden tiles']");

    private static async Task OpenMenuAsync(IPage page, string key)
    {
        var cell = Cell(page, key);
        await cell.HoverAsync();
        await cell.Locator(".tile-menu-btn").ClickAsync();
    }

    private async Task ResetAsync(IPage page, string? space = null)
    {
        var root = space is null ? "/api/v1/desktop" : $"/api/v1/spaces/{space}/desktop";
        foreach (var key in Builtins)
        {
            var res = await page.APIRequest.PutAsync($"{_fixture.BaseUrl}{root}/tiles/{key}", new APIRequestContextOptions
            {
                DataObject = new { position = (double?)null, size = (string?)null, color = (string?)null, hidden = false },
            });
            Assert.True(res.Ok, $"reset {key}: {res.Status}");
        }
    }

    private async Task<string> CreateSpaceAsync(IPage page, string name)
    {
        var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new APIRequestContextOptions
        {
            DataObject = new { name = name + " " + Guid.NewGuid().ToString("N")[..6] },
        });
        Assert.True(res.Ok, await res.TextAsync());
        return (await res.JsonAsync())!.Value.GetProperty("slug").GetString()!;
    }

    // Saves are fire-and-forget from the page: wait until the server holds
    // the expected state instead of sleeping a fixed time before a reload.
    private async Task WaitForServerAsync(IPage page, Func<System.Text.Json.JsonElement[], bool> ok)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var res = await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/desktop");
            var tiles = (await res.JsonAsync())!.Value.GetProperty("tiles").EnumerateArray().ToArray();
            if (ok(tiles)) return;
            Assert.True(DateTime.UtcNow < deadline, "desktop layout never reached the server");
            await page.WaitForTimeoutAsync(100);
        }
    }

    private static System.Text.Json.JsonElement? Tile(System.Text.Json.JsonElement[] tiles, string key) =>
        tiles.Where(t => t.GetProperty("key").GetString() == key).Cast<System.Text.Json.JsonElement?>().FirstOrDefault();

    private static double Pos(System.Text.Json.JsonElement[] tiles, string key) =>
        Tile(tiles, key) is { } t && t.GetProperty("position").ValueKind == System.Text.Json.JsonValueKind.Number
            ? t.GetProperty("position").GetDouble() : double.MaxValue;

    private static bool Hidden(System.Text.Json.JsonElement[] tiles, string key) =>
        Tile(tiles, key) is { } t && t.GetProperty("hidden").GetBoolean();

    [Fact]
    public async Task Desktop_TilesRender_AdminTilePersonalOnly_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            foreach (var hash in new[] { "#/notes", "#/todos", "#/calendar", "#/files" })
                await Assertions.Expect(page.Locator($"fb-hub-view a.tile[href='{hash}']")).ToBeVisibleAsync(new() { Timeout = 5000 });
            // The window apps: a tile each, no address.
            foreach (var key in new[] { "messages", "trash", "apps", "spaces", "keys", "secrets", "users", "system" })
                await Assertions.Expect(page.Locator($"fb-hub-view a.tile.tile-window[data-key='builtin:{key}']")).ToBeVisibleAsync();
            await page.Locator("fb-hub-view a.tile[data-key='builtin:keys']").ClickAsync();
            await Assertions.Expect(page.Locator("#fb-win-keys fb-keys-settings-view")).ToBeVisibleAsync(new() { Timeout = 5000 });
            Assert.EndsWith("#/", page.Url);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_desktop.png") });

            // In a space: the space's own hrefs, no admin tile.
            var slug = await CreateSpaceAsync(page, "Desk");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/");
            await Assertions.Expect(page.Locator($"fb-hub-view a.tile[href='#/space/{slug}/notes']")).ToBeVisibleAsync(new() { Timeout = 15000 });
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[href*='admin']")).ToHaveCountAsync(0);
            // The owner arranges a space's desktop.
            await Assertions.Expect(page.Locator("fb-hub-view .tile-menu").First).ToBeAttachedAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Desktop_Arrange_PersistsAcrossReloadAndBrowsers_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            await ResetAsync(page);
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Cell(page, "builtin:todos")).ToBeVisibleAsync(new() { Timeout = 5000 });
            // Nothing hidden: no toolbar item for it.
            await Assertions.Expect(ShowHiddenButton(page)).ToHaveCountAsync(0);

            // Size: the menu marks the current one, wide spans two columns.
            await OpenMenuAsync(page, "builtin:todos");
            await Assertions.Expect(Cell(page, "builtin:todos").Locator(".tile-menu button[data-action='size:medium']")).ToHaveTextAsync("✓ Medium tile");
            await Cell(page, "builtin:todos").Locator(".tile-menu button[data-action='size:wide']").ClickAsync();
            await Assertions.Expect(Cell(page, "builtin:todos")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("size-wide"));
            var medium = (await Cell(page, "builtin:calendar").BoundingBoxAsync())!;
            var wide = (await Cell(page, "builtin:todos").BoundingBoxAsync())!;
            Assert.True(wide.Width > medium.Width * 2 - 2, $"wide {wide.Width} vs medium {medium.Width}");

            // Small: the housekeeping tiles start small — a quarter of a
            // medium, together in the kit's .tile-pack, icon only.
            await Assertions.Expect(page.Locator(".tile-pack > a.tile.small[data-key='builtin:messages']")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".tile-pack > a.tile.small[data-key='builtin:trash']")).ToHaveCountAsync(1);
            await OpenMenuAsync(page, "builtin:messages");
            await Assertions.Expect(Cell(page, "builtin:messages").Locator(".tile-menu button[data-action='size:small']")).ToHaveTextAsync("✓ Small tile");
            await page.Keyboard.PressAsync("Escape");
            var small = (await Cell(page, "builtin:messages").BoundingBoxAsync())!;
            Assert.True(small.Width < medium.Width / 2 && small.Width > medium.Width / 3, $"small {small.Width} vs medium {medium.Width}");
            await Assertions.Expect(Cell(page, "builtin:messages").Locator("p").First).ToBeHiddenAsync();
            // Its name under the icon (the kit's small tile hides it).
            var label = Cell(page, "builtin:messages").Locator("h2");
            await Assertions.Expect(label).ToHaveTextAsync("Messages");
            await Assertions.Expect(label).ToBeVisibleAsync();
            var labelBox = (await label.BoundingBoxAsync())!;
            var iconBox = (await Cell(page, "builtin:messages").Locator(":scope > sac-icon").BoundingBoxAsync())!;
            Assert.True(labelBox.Y >= iconBox.Y + iconBox.Height - 1, $"label at {labelBox.Y}, icon ends at {iconBox.Y + iconBox.Height}");
            // Hover: the icon grows, it doesn't turn.
            await Cell(page, "builtin:messages").HoverAsync();
            await page.WaitForTimeoutAsync(600);
            var matrix = await Cell(page, "builtin:messages").Locator(":scope > sac-icon").EvaluateAsync<string>("i => getComputedStyle(i).transform");
            Assert.StartsWith("matrix(", matrix);
            var m = matrix[7..^1].Split(',').Select(x => double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.True(m[0] > 1.05 && Math.Abs(m[1]) < 0.001 && Math.Abs(m[2]) < 0.001, $"icon transform {matrix}");

            // No colour row: a space's colour is the workspace's one colour.
            await OpenMenuAsync(page, "builtin:calendar");
            await Assertions.Expect(Cell(page, "builtin:calendar").Locator(".tile-menu sac-swatch-grid")).ToHaveCountAsync(0);
            await page.Keyboard.PressAsync("Escape");

            // Hide: gone from the desktop, offered back in the toolbar.
            await OpenMenuAsync(page, "builtin:notes");
            await Cell(page, "builtin:notes").Locator(".tile-menu button[data-action='hide']").ClickAsync();
            await Assertions.Expect(Cell(page, "builtin:notes")).ToHaveCountAsync(0);
            await Assertions.Expect(ShowHiddenButton(page)).ToHaveAttributeAsync("title", "Show hidden tiles (1)");

            await WaitForServerAsync(page, t => Hidden(t, "builtin:notes")
                && Tile(t, "builtin:todos")?.GetProperty("size").GetString() == "wide");

            // All of it on the server: a reload, and another browser.
            foreach (var p in new[] { page, await (await _fixture.Browser!.NewContextAsync(new() { IgnoreHTTPSErrors = true, BypassCSP = true })).NewPageAsync() })
            {
                await p.GotoAsync(_fixture.BaseUrl + "/#/");
                await Assertions.Expect(Cell(p, "builtin:todos")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("size-wide"), new() { Timeout = 5000 });
                await Assertions.Expect(Cell(p, "builtin:notes")).ToHaveCountAsync(0);
            }

            // Show it again from the toolbar: the item goes once nothing is hidden.
            await ShowHiddenButton(page).ClickAsync();
            var dialog = page.Locator("sac-dialog#fb-hidden-tiles");
            await dialog.Locator("button[data-key='builtin:notes']").ClickAsync();
            await Assertions.Expect(Cell(page, "builtin:notes")).ToBeVisibleAsync();
            await Assertions.Expect(ShowHiddenButton(page)).ToHaveCountAsync(0);
            await WaitForServerAsync(page, t => !Hidden(t, "builtin:notes"));
            await page.ReloadAsync();
            await Assertions.Expect(Cell(page, "builtin:notes")).ToBeVisibleAsync(new() { Timeout = 5000 });
            Assert.Empty(errors);
        }
        finally
        {
            await ResetAsync(page);
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Desktop_SpaceMember_CannotArrange_Test()
    {
        var db = new DatabaseFactory(_fixture.DataDir);
        var system = new SystemRepository(db);
        var ownerId = "desk-owner-" + Guid.NewGuid().ToString("N")[..8];
        await system.CreateUserAsync(ownerId, "Owner", null, null, Ct);
        var space = await new SpaceRepository(db).CreateAsync(ownerId, "Shared desk " + Guid.NewGuid().ToString("N")[..6], Ct);
        using (var conn = db.CreateSystemConnection())
        {
            await conn.ExecuteAsync(
                "INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@s, @u, 'member', @t)",
                new { s = space.Id, u = TestUser, t = DateTime.UtcNow.ToString("o") });
        }

        var (context, page, errors) = await OpenAsync();
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{space.Slug}/");
            await Assertions.Expect(page.Locator($"fb-hub-view a.tile[href='#/space/{space.Slug}/notes']")).ToBeVisibleAsync(new() { Timeout = 15000 });
            // Readonly: the same arrangement, no tile menus.
            await Assertions.Expect(page.Locator("fb-hub-view .tile-menu")).ToHaveCountAsync(0);
            // The server agrees: a member's PUT is refused.
            var res = await page.APIRequest.PutAsync($"{_fixture.BaseUrl}/api/v1/spaces/{space.Slug}/desktop/tiles/builtin:notes",
                new APIRequestContextOptions { DataObject = new { hidden = true } });
            Assert.Equal(403, res.Status);
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Palette_OpensApps_CreatesNote_FindsNote_SwitchesWorkspace_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var title = "Palette note " + Guid.NewGuid().ToString("N")[..6];
            var created = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/notes", new APIRequestContextOptions
            {
                DataObject = new { title, content = "Findable." },
            });
            Assert.True(created.Ok);
            var slug = await CreateSpaceAsync(page, "Palette");

            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Cell(page, "builtin:notes")).ToBeVisibleAsync(new() { Timeout = 5000 });
            var palette = page.Locator("sac-command-palette");
            var input = palette.Locator("input");

            // Ctrl+K from the page; the search button from inside a note
            // editor, whose own Ctrl+K inserts a link.
            async Task RunAsync(string query, string expectLabel, bool viaKey = true)
            {
                if (viaKey) await page.Keyboard.PressAsync("Control+k");
                else await page.Locator("#fb-search-btn").ClickAsync();
                await Assertions.Expect(palette).ToHaveAttributeAsync("open", "");
                await input.FillAsync(query);
                await Assertions.Expect(palette.Locator("[role='option']").First).ToContainTextAsync(expectLabel);
                await page.Keyboard.PressAsync("Enter");
                await Assertions.Expect(palette).Not.ToHaveAttributeAsync("open", "");
            }

            // The search button opens it too — the screenshot shows its groups.
            await page.Locator("#fb-search-btn").ClickAsync();
            await Assertions.Expect(palette).ToHaveAttributeAsync("open", "");
            await Assertions.Expect(palette).ToContainTextAsync("Apps");
            await Assertions.Expect(palette).ToContainTextAsync("Create");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_palette.png") });
            await page.Keyboard.PressAsync("Escape");

            // Open an app.
            await RunAsync("Todos", "Todos");
            await page.WaitForURLAsync(u => u.EndsWith("#/todos"));

            // Find a note by its title.
            await RunAsync(title, title);
            await page.WaitForURLAsync(u => u.EndsWith("#/notes"));
            await page.WaitForFunctionAsync("t => (document.querySelector('fb-notes-view #content')?.value || '').includes(t)", title,
                new() { Timeout = 5000 });

            // Create a note: the editor opens on a fresh, empty note.
            var before = await page.Locator("fb-notes-view .nv-item").CountAsync();
            await RunAsync("New note", "New note");
            await Assertions.Expect(page.Locator("fb-notes-view .nv-item")).ToHaveCountAsync(before + 1, new() { Timeout = 5000 });

            // Switch workspace.
            await RunAsync("Switch to Palette", "Switch to Palette", viaKey: false);
            await page.WaitForURLAsync(u => u.Contains($"#/space/{slug}"));
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    // Full text: the palette's Notes group searches note bodies of the active
    // workspace (hybrid search), shows the matching words and opens the note;
    // inside a space it finds only that space's notes and stays in the space.
    [Fact]
    public async Task Palette_FullTextNotes_PerWorkspace_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var word = "zq" + Guid.NewGuid().ToString("N")[..8];
            var personalTitle = "Personal ft " + word[..6];
            var created = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/notes", new APIRequestContextOptions
            {
                DataObject = new { title = personalTitle, content = $"# {personalTitle}\n\nThe ferry leaves at noon, bring {word} along." },
            });
            Assert.True(created.Ok);
            var slug = await CreateSpaceAsync(page, "FullText");
            var spaceTitle = "Space ft " + word[..6];
            var spaced = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/notes", new APIRequestContextOptions
            {
                DataObject = new { title = spaceTitle, content = $"# {spaceTitle}\n\nShared {word} checklist." },
            });
            Assert.True(spaced.Ok);

            var palette = page.Locator("sac-command-palette");
            var input = palette.Locator("input");
            var options = palette.Locator("[role='option']");

            // Personal: the body word finds the personal note only, with the words around it.
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Cell(page, "builtin:notes")).ToBeVisibleAsync(new() { Timeout = 5000 });
            await page.Keyboard.PressAsync("Control+k");
            await input.FillAsync(word);
            var hit = options.Filter(new() { HasText = personalTitle });
            await Assertions.Expect(hit).ToHaveCountAsync(1, new() { Timeout = 8000 });
            await Assertions.Expect(hit).ToContainTextAsync("bring " + word);
            await Assertions.Expect(options.Filter(new() { HasText = spaceTitle })).ToHaveCountAsync(0);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_palette_fulltext.png") });
            await hit.ClickAsync();
            await page.WaitForURLAsync(u => u.EndsWith("#/notes"));
            await page.WaitForFunctionAsync("t => (document.querySelector('fb-notes-view #content')?.value || '').includes(t)", word,
                new() { Timeout = 5000 });

            // In the space: only the space's note, and it opens in the space.
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/");
            await Assertions.Expect(Cell(page, "builtin:notes")).ToBeVisibleAsync(new() { Timeout = 5000 });
            await page.Keyboard.PressAsync("Control+k");
            await input.FillAsync(word);
            var spaceHit = options.Filter(new() { HasText = spaceTitle });
            await Assertions.Expect(spaceHit).ToHaveCountAsync(1, new() { Timeout = 8000 });
            await Assertions.Expect(options.Filter(new() { HasText = personalTitle })).ToHaveCountAsync(0);
            await spaceHit.ClickAsync();
            await page.WaitForURLAsync(u => u.EndsWith($"#/space/{slug}/notes"));

            // An app picked from the palette stays in the space.
            await page.Keyboard.PressAsync("Control+k");
            await input.FillAsync("Todos");
            await Assertions.Expect(options.First).ToContainTextAsync("Todos");
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForURLAsync(u => u.EndsWith($"#/space/{slug}/todos"));
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Desktop_Phone_OneColumn_Test()
    {
        var (context, page, errors) = await OpenAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = 375, Height = 740 },
            IsMobile = true,
            HasTouch = true,
        });
        try
        {
            await ResetAsync(page);
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Cell(page, "builtin:notes")).ToBeVisibleAsync(new() { Timeout = 5000 });
            // The kit's phone grid: one column of row tiles.
            var notes = (await Cell(page, "builtin:notes").BoundingBoxAsync())!;
            var todos = (await Cell(page, "builtin:todos").BoundingBoxAsync())!;
            Assert.True(notes.Width > 375 / 2, $"tile {notes.Width}px wide");
            Assert.True(todos.Y > notes.Y, "tiles stack");
            var overflow = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
            Assert.Equal(0, overflow);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_desktop_phone.png") });
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
