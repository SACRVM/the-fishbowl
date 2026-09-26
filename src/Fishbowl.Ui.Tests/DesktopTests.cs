using Dapper;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The desktop in the browser: square tiles on the kit's <sac-launcher>, fed
// from fb.desktop's registry; their arrangement (size, colour, hide, order)
// stored per workspace on the server (persist="none" — the launcher never
// keeps its own copy), the badges, and the Ctrl/⌘K palette. Every test that
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

    private static ILocator Cell(IPage page, string key) => page.Locator($"fb-hub-view .sac-launcher-cell[data-id='{key}']");

    private static Task<string[]> OrderAsync(IPage page) =>
        page.Locator("fb-hub-view .sac-launcher-cell")
            .EvaluateAllAsync<string[]>("els => els.map(e => e.dataset.id)");

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

    private static async Task ChooseAsync(IPage page, string key, string label)
    {
        var cell = Cell(page, key);
        await cell.HoverAsync();
        await cell.Locator(".sac-launcher-menu-btn").ClickAsync();
        await cell.Locator(".sac-launcher-menu button[data-action]").Filter(new() { HasText = label }).ClickAsync();
    }

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
            await Assertions.Expect(page.Locator("fb-hub-view .sac-launcher-menu").First).ToBeAttachedAsync();
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

            // Squares: a medium tile is as high as it is wide.
            var sq = (await Cell(page, "builtin:files").BoundingBoxAsync())!;
            Assert.InRange(sq.Height, sq.Width - 2, sq.Width + 2);

            // Size: wide is two squares side by side, one square high.
            await ChooseAsync(page, "builtin:todos", "Wide");
            await Assertions.Expect(Cell(page, "builtin:todos")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("size-wide"));
            var wide = (await Cell(page, "builtin:todos").BoundingBoxAsync())!;
            Assert.InRange(wide.Height, sq.Height - 2, sq.Height + 2);
            Assert.True(wide.Width > sq.Width * 2 - 2, $"wide {wide.Width} vs square {sq.Width}");

            // Colour, through the picker dialog.
            await ChooseAsync(page, "builtin:calendar", "Colour");
            var dialog = page.Locator("sac-dialog[title^='Colour']");
            await dialog.Locator("sac-swatch").Nth(3).ClickAsync();
            await Assertions.Expect(Cell(page, "builtin:calendar").Locator("a.tile")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("--accent: var\\(--palette-"));

            // Hide: gone from the desktop, kept for the Edit mode.
            await ChooseAsync(page, "builtin:notes", "Hide");
            await Assertions.Expect(Cell(page, "builtin:notes")).ToBeHiddenAsync();

            // Order: drag Files in front of Todos.
            var files = Cell(page, "builtin:files").Locator("a.tile");
            var todos = Cell(page, "builtin:todos").Locator("a.tile");
            var from = (await files.BoundingBoxAsync())!;
            var to = (await todos.BoundingBoxAsync())!;
            await page.Mouse.MoveAsync(from.X + 40, from.Y + 40);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(from.X + 50, from.Y + 50, new() { Steps = 3 });
            // Just left of the Todos tile's centre: the grid axis inserts
            // before the nearest tile when the pointer is on its left half.
            await page.Mouse.MoveAsync(to.X + to.Width * 0.4f, to.Y + to.Height / 2, new() { Steps = 12 });
            await page.Mouse.UpAsync();
            await WaitForServerAsync(page, t => Pos(t, "builtin:files") < Pos(t, "builtin:todos") && Hidden(t, "builtin:notes")
                && Tile(t, "builtin:todos")?.GetProperty("size").GetString() == "wide"
                && Tile(t, "builtin:calendar")?.GetProperty("color").ValueKind == System.Text.Json.JsonValueKind.String);
            var order = await OrderAsync(page);
            Assert.True(Array.IndexOf(order, "builtin:files") < Array.IndexOf(order, "builtin:todos"), string.Join(",", order));

            // All of it on the server: a reload, and another browser.
            foreach (var p in new[] { page, await (await _fixture.Browser!.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync() })
            {
                await p.GotoAsync(_fixture.BaseUrl + "/#/");
                await Assertions.Expect(Cell(p, "builtin:todos")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("size-wide"), new() { Timeout = 5000 });
                await Assertions.Expect(Cell(p, "builtin:calendar").Locator("a.tile")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("--palette-"));
                await Assertions.Expect(Cell(p, "builtin:notes")).ToBeHiddenAsync();
                var again = await OrderAsync(p);
                Assert.True(Array.IndexOf(again, "builtin:files") < Array.IndexOf(again, "builtin:todos"), string.Join(",", again));
            }

            // Show the hidden tile again: Edit shows it grayed, with a Show control.
            await page.Locator("fb-hub-view .sac-launcher-edit").ClickAsync();
            // no-add: Edit mode offers no same-realm "Add app" tile.
            Assert.Equal(0, await page.Locator("fb-hub-view .sac-launcher-add-cell").CountAsync());
            await Cell(page, "builtin:notes").Locator(".sac-launcher-ctrl.vis").ClickAsync();
            await page.Locator("fb-hub-view .sac-launcher-edit").ClickAsync();
            await Assertions.Expect(Cell(page, "builtin:notes")).ToBeVisibleAsync();
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
            // Readonly: the same arrangement, no menus, no Edit, no drag.
            await Assertions.Expect(page.Locator("fb-hub-view sac-launcher[readonly]")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("fb-hub-view .sac-launcher-menu")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("fb-hub-view .sac-launcher-edit")).ToBeHiddenAsync();
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
    public async Task Desktop_Phone_TwoColumnSquares_Test()
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
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Cell(page, "builtin:notes")).ToBeVisibleAsync(new() { Timeout = 5000 });
            // Arranging is reachable on touch: the "…" is shown, not hover-only.
            Assert.Equal("1", await Cell(page, "builtin:notes").Locator(".sac-launcher-menu-btn")
                .EvaluateAsync<string>("b => getComputedStyle(b).opacity"));
            // Two columns of squares.
            var box = (await Cell(page, "builtin:notes").BoundingBoxAsync())!;
            Assert.InRange(box.Height, box.Width - 2, box.Width + 2);
            Assert.True(box.Width < 375 / 2, $"tile {box.Width}px wide");
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
