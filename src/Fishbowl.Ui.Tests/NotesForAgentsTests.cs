using System.Text.Json;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// What a space run by agents needs in the apps (spec 2026-11-06-notes-for-agents):
// saved filters with a tag expression (kept per workspace, every member's), the
// board that groups notes by a tag namespace and moves them by swapping the tag
// (by drag or by Alt+arrow, review:pending stays), what agents changed (the
// history window's diff), links between notes and back, the lock, todos' tags
// with their strip, and a tag's description in the tag manager.
[Collection(UiCollection.Name)]
public class NotesForAgentsTests
{
    private readonly PlaywrightFixture _fixture;

    public NotesForAgentsTests(PlaywrightFixture fixture) => _fixture = fixture;

    private async Task<(IBrowserContext Context, IPage Page, List<string> Errors)> OpenAsync()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        return (context, page, errors);
    }

    private async Task<string> SpaceAsync(IPage page, string name)
    {
        var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = name + " " + Guid.NewGuid().ToString("N")[..6] } });
        return (await res.JsonAsync())!.Value.GetProperty("slug").GetString()!;
    }

    private static async Task<string> PostAsync(IPage page, string url, object body)
    {
        var res = await page.APIRequest.PostAsync(url, new() { DataObject = body });
        Assert.True(res.Ok, $"{res.Status}: {await res.TextAsync()}");
        return (await res.JsonAsync())!.Value.GetProperty("id").GetString()!;
    }

    private static async Task<List<string>> TagsAsync(IPage page, string url)
    {
        var note = (await (await page.APIRequest.GetAsync(url)).JsonAsync())!.Value;
        return note.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToList();
    }

    [Fact]
    public async Task SavedFilter_NarrowsTheList_AndTheBoardMovesNotesByTheirTag_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var slug = await SpaceAsync(page, "Board");
            var api = $"{_fixture.BaseUrl}/api/v1/spaces/{slug}";
            var a = await PostAsync(page, $"{api}/notes", new { title = "Decision A", content = "a", tags = new[] { "kind:decision", "status:approved" } });
            await PostAsync(page, $"{api}/notes", new { title = "Decision B", content = "b", tags = new[] { "kind:decision", "status:superseded" } });
            await PostAsync(page, $"{api}/notes", new { title = "Plan C", content = "c", tags = new[] { "kind:plan", "status:draft" } });

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/notes");
            var rows = page.Locator("fb-notes-view .nv-item");
            await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 15000 });

            // A saved filter: name and expression, with the live count.
            await page.Locator("fb-notes-view #filters-btn").ClickAsync();
            await page.Locator("fb-notes-view #filters-menu button[data-action='new']").ClickAsync();
            var dialog = page.Locator("sac-dialog[open]");
            await dialog.Locator("input[name=name]").FillAsync("Open decisions");
            await dialog.Locator("input[name=expr]").FillAsync("kind:decision AND NOT status:superseded");
            await Assertions.Expect(dialog.Locator(".fb-filter-count")).ToContainTextAsync("1 note");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(1);
            await Assertions.Expect(rows.First).ToContainTextAsync("Decision A");
            await Assertions.Expect(page.Locator("fb-notes-view #list-title")).ToHaveTextAsync("Open decisions");

            // Kept: after a reload the filter is still in use.
            await page.ReloadAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(1, new() { Timeout = 15000 });

            // The board by status: every status tag is a column, also the empty ones.
            await page.Locator("#nv-view-menu [slot='trigger']").ClickAsync();
            await page.Locator("#nv-view-menu button[data-action='board:status']").ClickAsync();
            var board = page.Locator("fb-notes-view #board");
            await Assertions.Expect(board.Locator(".nb-col[data-value='approved'] .nb-card")).ToHaveCountAsync(1);
            await Assertions.Expect(board.Locator(".nb-col[data-value='draft']")).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_notes_board.png") });

            // A drag swaps the tag (and keeps every other).
            await board.Locator(".nb-card", new() { HasText = "Decision A" })
                .DragToAsync(board.Locator(".nb-col[data-value='draft'] .nb-cards"));
            await Assertions.Expect(board.Locator(".nb-col[data-value='draft'] .nb-card")).ToHaveCountAsync(1);
            var tags = await TagsAsync(page, $"{api}/notes/{a}");
            Assert.Contains("status:draft", tags);
            Assert.DoesNotContain("status:approved", tags);
            Assert.Contains("kind:decision", tags);

            // Alt+→ moves the focused card one column on — into superseded,
            // which the filter leaves out.
            await board.Locator(".nb-card", new() { HasText = "Decision A" }).FocusAsync();
            await page.Keyboard.PressAsync("Alt+ArrowRight");
            await Assertions.Expect(board.Locator(".nb-card")).ToHaveCountAsync(0);
            Assert.Contains("status:superseded", await TagsAsync(page, $"{api}/notes/{a}"));

            // A card opens its note; the footer leads back to the board.
            await page.Locator("fb-notes-view #filters-btn").ClickAsync();
            await page.Locator("fb-notes-view #filters-menu button[data-action='filter:']").ClickAsync();
            await board.Locator(".nb-card", new() { HasText = "Plan C" }).ClickAsync();
            await Assertions.Expect(page.Locator("fb-notes-view #editor")).ToBeVisibleAsync();
            await page.Locator("fb-notes-view #back-to-board-btn").ClickAsync();
            await Assertions.Expect(board).ToBeVisibleAsync();

            // And back to the list.
            await page.Locator("#nv-view-menu [slot='trigger']").ClickAsync();
            await page.Locator("#nv-view-menu button[data-action='list']").ClickAsync();
            await Assertions.Expect(board).ToBeHiddenAsync();
            Assert.Empty(errors);
        }
        finally { await context.CloseAsync(); }
    }

    [Fact]
    public async Task AgentChanges_Links_AndTheLock_InTheEditor_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var tag = Guid.NewGuid().ToString("N")[..6];
            var notes = $"{_fixture.BaseUrl}/api/v1/notes";
            var target = await PostAsync(page, notes, new { title = $"Target {tag}", content = $"Target {tag}\n\nThe body." });
            var source = await PostAsync(page, notes, new
            {
                title = $"Source {tag}",
                content = $"Source {tag}\n\nSee [[Target {tag}]] and [the same](note:{target}), and [[Nowhere {tag}]].",
            });
            // An agent appends (MCP through the signed-in session: personal workspace).
            var rpc = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/mcp", new()
            {
                DataObject = new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "update_memory", arguments = new { id = source, append = "An agent added this line." } } },
            });
            Assert.True(rpc.Ok);

            await page.GotoAsync($"{_fixture.BaseUrl}/#/notes");
            var row = page.Locator("fb-notes-view .nv-item", new() { HasText = $"Source {tag}" });
            await Assertions.Expect(row).ToBeVisibleAsync(new() { Timeout = 15000 });
            await row.ClickAsync();

            // What the agent changed.
            var history = page.Locator("fb-notes-view #history-btn");
            await Assertions.Expect(history).ToContainTextAsync("1 agent change");
            await history.ClickAsync();
            var win = page.Locator("#fb-note-history");
            await Assertions.Expect(win.Locator(".fb-diff-line.add", new() { HasText = "An agent added this line." })).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_note_history.png") });
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(win).Not.ToHaveAttributeAsync("open", "");

            // Its links: the target once (two forms name it), the missing one struck through.
            var outLinks = page.Locator("fb-notes-view #links-out");
            await Assertions.Expect(outLinks.Locator("sac-chip")).ToHaveCountAsync(1);
            await Assertions.Expect(outLinks.Locator(".nv-link-missing")).ToHaveTextAsync($"Nowhere {tag}");
            await outLinks.Locator($"sac-chip[label='Target {tag}']").ClickAsync();
            await Assertions.Expect(page.Locator("fb-notes-view .nv-item.selected")).ToContainTextAsync($"Target {tag}");
            await Assertions.Expect(page.Locator($"fb-notes-view #links-in sac-chip[label='Source {tag}']")).ToBeVisibleAsync();

            // The lock: asked, then the text holds; the owner lifts it.
            await page.Locator("fb-notes-view #lock-btn").ClickAsync();
            await page.Locator("sac-dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Lock" }).ClickAsync();
            await Assertions.Expect(page.Locator("fb-notes-view #note-locked-pill")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("fb-notes-view #content")).ToHaveAttributeAsync("readonly", "");
            var stored = (await (await page.APIRequest.GetAsync($"{notes}/{target}")).JsonAsync())!.Value;
            Assert.True(stored.GetProperty("locked").GetBoolean());
            await page.Locator("fb-notes-view #lock-btn").ClickAsync();
            await Assertions.Expect(page.Locator("fb-notes-view #note-locked-pill")).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("fb-notes-view #content")).Not.ToHaveAttributeAsync("readonly", "");
            Assert.Empty(errors);
        }
        finally { await context.CloseAsync(); }
    }

    [Fact]
    public async Task TodoTags_ShowAndFilter_AndATagSaysWhatItMeans_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var slug = await SpaceAsync(page, "Todo tags");
            var api = $"{_fixture.BaseUrl}/api/v1/spaces/{slug}";
            await PostAsync(page, $"{api}/todos", new { title = "Bake lightmaps", tags = new[] { "area:lighting" } });
            await PostAsync(page, $"{api}/todos", new { title = "Write the README" });

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/todos");
            var rows = page.Locator("fb-todos-view .tv-item");
            await Assertions.Expect(rows).ToHaveCountAsync(2, new() { Timeout = 15000 });
            await Assertions.Expect(rows.Filter(new() { HasText = "Bake lightmaps" }).Locator(".tv-item-tagline")).ToContainTextAsync("area:lighting");
            await page.Locator("fb-todos-view #tag-filter sac-chip[label='area:lighting']").ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(1);
            await rows.First.ClickAsync();
            await Assertions.Expect(page.Locator("fb-todos-view #tag-input sac-chip[label='area:lighting']")).ToBeAttachedAsync();

            // The tag manager (from Notes): the todo is counted, a description sticks.
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/notes");
            await page.Locator("#fb-view-toolbar button[title='Manage tags']").ClickAsync();
            var tagRow = page.Locator("#fb-tag-manager .fb-tags-row[data-tag='area:lighting']");
            await Assertions.Expect(tagRow.Locator(".fb-tags-count")).ToContainTextAsync("1");
            await tagRow.Locator(".fb-tags-desc").FillAsync("Lights, shadows and baking");
            await tagRow.Locator(".fb-tags-desc").PressAsync("Enter");
            string? description = null;
            for (var i = 0; i < 50 && description != "Lights, shadows and baking"; i++)
            {
                await page.WaitForTimeoutAsync(100);
                var tags = (await (await page.APIRequest.GetAsync($"{api}/tags")).JsonAsync())!.Value;
                var t = tags.EnumerateArray().First(x => x.GetProperty("name").GetString() == "area:lighting");
                description = t.TryGetProperty("description", out var d) ? d.GetString() : null;
            }
            Assert.Equal("Lights, shadows and baking", description);
            Assert.Empty(errors);
        }
        finally { await context.CloseAsync(); }
    }
}
