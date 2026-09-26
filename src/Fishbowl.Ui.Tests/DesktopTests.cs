using Dapper;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The desktop in the browser: SACRVM Desktop's tiles and tile menu on the
// kit's .grid, fed from fb.desktop's registry; their arrangement (size,
// colour, hide) stored per workspace on the server, hidden tiles offered
// back in the toolbar, the badges, and the Ctrl/⌘K palette. Every test that
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
            foreach (var hash in new[] { "#/notes", "#/todos", "#/calendar", "#/files", "#/messages", "#/admin/users" })
                await Assertions.Expect(page.Locator($"fb-hub-view a.tile[href='{hash}']")).ToBeVisibleAsync(new() { Timeout = 5000 });
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_desktop.png") });

            // In a space: the space's own hrefs, no admin tile.
            var slug = await CreateSpaceAsync(page, "Desk");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/");
            await Assertions.Expect(page.Locator($"fb-hub-view a.tile[href='#/space/{slug}/notes']")).ToBeVisibleAsync(new() { Timeout = 5000 });
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
            var medium = (await Cell(page, "builtin:files").BoundingBoxAsync())!;
            var wide = (await Cell(page, "builtin:todos").BoundingBoxAsync())!;
            Assert.True(wide.Width > medium.Width * 2 - 2, $"wide {wide.Width} vs medium {medium.Width}");

            // Colour, from the menu's colour row.
            await OpenMenuAsync(page, "builtin:calendar");
            await Cell(page, "builtin:calendar").Locator(".tile-tint sac-swatch").Nth(3).ClickAsync();
            await Assertions.Expect(Cell(page, "builtin:calendar")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("--accent: var\\(--palette-"));

            // Hide: gone from the desktop, offered back in the toolbar.
            await OpenMenuAsync(page, "builtin:notes");
            await Cell(page, "builtin:notes").Locator(".tile-menu button[data-action='hide']").ClickAsync();
            await Assertions.Expect(Cell(page, "builtin:notes")).ToHaveCountAsync(0);
            await Assertions.Expect(ShowHiddenButton(page)).ToHaveAttributeAsync("title", "Show hidden tiles (1)");

            await WaitForServerAsync(page, t => Hidden(t, "builtin:notes")
                && Tile(t, "builtin:todos")?.GetProperty("size").GetString() == "wide"
                && Tile(t, "builtin:calendar")?.GetProperty("color").ValueKind == System.Text.Json.JsonValueKind.String);

            // All of it on the server: a reload, and another browser.
            foreach (var p in new[] { page, await (await _fixture.Browser!.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync() })
            {
                await p.GotoAsync(_fixture.BaseUrl + "/#/");
                await Assertions.Expect(Cell(p, "builtin:todos")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("size-wide"), new() { Timeout = 5000 });
                await Assertions.Expect(Cell(p, "builtin:calendar")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("--palette-"));
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
            await Assertions.Expect(page.Locator($"fb-hub-view a.tile[href='#/space/{space.Slug}/notes']")).ToBeVisibleAsync(new() { Timeout = 5000 });
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
    public async Task Desktop_Badges_ShowCounts_Test()
    {
        var (context, page, errors) = await OpenAsync();
        var messages = new MessageRepository(new DatabaseFactory(_fixture.DataDir));
        IReadOnlyList<string> messageIds = Array.Empty<string>();
        string? todoId = null, eventId = null;
        try
        {
            await ResetAsync(page);
            var title = "Badge todo " + Guid.NewGuid().ToString("N")[..6];
            var todo = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/todos", new APIRequestContextOptions
            {
                DataObject = new { title, dueAt = DateTime.UtcNow.AddMinutes(5).ToString("o") },
            });
            Assert.True(todo.Ok, await todo.TextAsync());
            todoId = (await todo.JsonAsync())!.Value.GetProperty("id").GetString();
            var eventTitle = "Badge event " + Guid.NewGuid().ToString("N")[..6];
            var start = DateTime.Now.AddMinutes(1);
            var evt = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/events", new APIRequestContextOptions
            {
                DataObject = new { title = eventTitle, startAt = start.ToUniversalTime().ToString("o"), endAt = start.AddHours(1).ToUniversalTime().ToString("o") },
            });
            Assert.True(evt.Ok, await evt.TextAsync());
            eventId = (await evt.JsonAsync())!.Value.GetProperty("id").GetString();
            messageIds = await messages.CreateAsync(new[] { TestUser }, "user.approved", "user", TestUser, null, Ct);

            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Cell(page, "builtin:todos").Locator(".tile-badge")).ToContainTextAsync("today", new() { Timeout = 5000 });
            await Assertions.Expect(Cell(page, "builtin:calendar").Locator(".tile-badge")).ToBeVisibleAsync();
            await Assertions.Expect(Cell(page, "builtin:messages").Locator(".tile-badge")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^\\d+$"));
            Assert.Empty(errors);
        }
        finally
        {
            // Leave the shared test user as it was: no extra todo, event or
            // unread message for the tests that follow.
            foreach (var id in messageIds) await messages.MarkReadAsync(id, TestUser, Ct);
            if (todoId is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/todos/{todoId}");
            if (eventId is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/events/{eventId}");
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

    [Fact]
    public async Task Desktop_Phone_OneColumn_Test()
    {
        var (context, page, errors) = await OpenAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
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
