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

            // In the text the editor shows the wikilink as a link; Ctrl/⌘+click
            // on it opens its note (the editor's sac:link).
            var wiki = page.Locator($"fb-notes-view #content a.inline-wikilink[title='Target {tag}']");
            await Assertions.Expect(wiki).ToBeVisibleAsync();
            await wiki.ClickAsync(new() { Modifiers = new[] { KeyboardModifier.ControlOrMeta } });
            await Assertions.Expect(page.Locator("fb-notes-view .nv-item.selected")).ToContainTextAsync($"Target {tag}");
            await row.ClickAsync();
            // A link by id (a host scheme of the editor's) the same way.
            await page.Locator($"fb-notes-view #content a[href='note:{target}']").ClickAsync(new() { Modifiers = new[] { KeyboardModifier.ControlOrMeta } });
            await Assertions.Expect(page.Locator("fb-notes-view .nv-item.selected")).ToContainTextAsync($"Target {tag}");
            await row.ClickAsync();
            await Assertions.Expect(outLinks.Locator("sac-chip")).ToHaveCountAsync(1);

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

    // A note written elsewhere keeps its title apart from its text. Opening it
    // in the Notes app shows the title on top; clicking in and out and leaving
    // writes nothing (a save compares with what the editor reads back — its
    // 11524c6 turned the "~" of "about" into "~~"), an edit keeps the title out
    // of the text, and a lone "~" is text: never struck through, kept as typed.
    [Fact]
    public async Task AnAgentsNote_OpenedOrEdited_KeepsItsTextAndItsTitleApart_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var tag = Guid.NewGuid().ToString("N")[..6];
            var notes = $"{_fixture.BaseUrl}/api/v1/notes";
            const string costs = "A dedicated box (~€39/month) or a cloud VPS (~€32) will do.\n\nThe rest.";
            var a = await PostAsync(page, notes, new { title = $"Costs {tag}", content = costs });
            var b = await PostAsync(page, notes, new { title = $"Plan {tag}", content = "First line.\n\nSecond line." });
            async Task<JsonElement> NoteAsync(string id) => (await (await page.APIRequest.GetAsync($"{notes}/{id}")).JsonAsync())!.Value;
            var before = await NoteAsync(a);

            await page.GotoAsync($"{_fixture.BaseUrl}/#/notes");
            var rowA = page.Locator("fb-notes-view .nv-item", new() { HasText = $"Costs {tag}" });
            var rowB = page.Locator("fb-notes-view .nv-item", new() { HasText = $"Plan {tag}" });
            await Assertions.Expect(rowA).ToBeVisibleAsync(new() { Timeout = 15000 });
            await rowA.ClickAsync();
            var line = page.Locator("fb-notes-view #content .line", new() { HasText = "cloud VPS" });
            await Assertions.Expect(line).ToBeVisibleAsync();
            Assert.Equal($"# Costs {tag}", await page.Locator("fb-notes-view #content .line").First.TextContentAsync());
            Assert.Equal($"# Costs {tag}\n\n{costs}", await page.Locator("fb-notes-view #content").EvaluateAsync<string>("e => e.value"));
            await Assertions.Expect(line.Locator("del")).ToHaveCountAsync(0);

            // In and out of the text, then another note: nothing is written.
            await line.ClickAsync();
            await page.Locator("fb-notes-view #search-input").ClickAsync();
            await rowB.ClickAsync();
            await page.WaitForTimeoutAsync(1200);
            var after = await NoteAsync(a);
            Assert.Equal(costs, after.GetProperty("content").GetString());
            Assert.Equal($"Costs {tag}", after.GetProperty("title").GetString());
            Assert.Equal(before.GetProperty("updatedAt").GetString(), after.GetProperty("updatedAt").GetString());

            // An edit: the text changes, the title stays the title, not a line of it.
            var second = page.Locator("fb-notes-view #content .line", new() { HasText = "Second line." });
            await second.ClickAsync();
            await page.Keyboard.PressAsync("End");
            await page.Keyboard.TypeAsync(" Edited.");
            await rowA.ClickAsync();
            JsonElement edited = default;
            for (var i = 0; i < 30; i++)
            {
                edited = await NoteAsync(b);
                if (edited.GetProperty("content").GetString()!.Contains("Edited.")) break;
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
            Assert.Equal("First line.\n\nSecond line. Edited.", edited.GetProperty("content").GetString());
            Assert.Equal($"Plan {tag}", edited.GetProperty("title").GetString());

            // Its heading renamed in the editor: a new title, still apart.
            await rowB.ClickAsync();
            var heading = page.Locator("fb-notes-view #content .line").First;
            await Assertions.Expect(heading).ToHaveTextAsync($"# Plan {tag}");
            await heading.ClickAsync();
            await page.Keyboard.PressAsync("End");
            await page.Keyboard.TypeAsync(" v2");
            await rowA.ClickAsync();
            for (var i = 0; i < 30; i++)
            {
                edited = await NoteAsync(b);
                if (edited.GetProperty("title").GetString()!.EndsWith("v2")) break;
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
            Assert.Equal($"Plan {tag} v2", edited.GetProperty("title").GetString());
            Assert.Equal("First line.\n\nSecond line. Edited.", edited.GetProperty("content").GetString());

            // An edit beside the tildes keeps them as they were.
            var rest = page.Locator("fb-notes-view #content .line", new() { HasText = "The rest." });
            await rest.ClickAsync();
            await page.Keyboard.PressAsync("End");
            await page.Keyboard.TypeAsync(" Edited.");
            await rowB.ClickAsync();
            for (var i = 0; i < 30; i++)
            {
                edited = await NoteAsync(a);
                if (edited.GetProperty("content").GetString()!.Contains("Edited.")) break;
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
            Assert.Equal(costs.Replace("The rest.", "The rest. Edited."), edited.GetProperty("content").GetString());
            Assert.Equal($"Costs {tag}", edited.GetProperty("title").GetString());
            Assert.Empty(errors);
        }
        finally { await context.CloseAsync(); }
    }

    // Reviewing many at once: the marked notes (one of them locked) lose
    // review:pending in one go, and so does every note a filter shows, after
    // a question — their text and their other tags as they were.
    [Fact]
    public async Task ApproveSelected_AndEveryNoteAFilterShows_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var slug = await SpaceAsync(page, "Review");
            var api = $"{_fixture.BaseUrl}/api/v1/spaces/{slug}";
            var key = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/keys", new()
            {
                DataObject = new { name = "review " + slug, contextType = "space", contextId = slug, scopes = new[] { "read:notes", "write:notes" } },
            });
            Assert.True(key.Ok, await key.TextAsync());
            var agent = await _fixture.Playwright!.APIRequest.NewContextAsync(new()
            {
                BaseURL = _fixture.BaseUrl,
                IgnoreHTTPSErrors = true,
                ExtraHTTPHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + (await key.JsonAsync())!.Value.GetProperty("rawToken").GetString() },
            });
            async Task<string> AgentNoteAsync(string title, string area)
            {
                var res = await agent.PostAsync("/api/v1/notes", new() { DataObject = new { title, content = $"{title}: about ~€5 (~3 days).", tags = new[] { area } } });
                Assert.True(res.Ok, await res.TextAsync());
                return (await res.JsonAsync())!.Value.GetProperty("id").GetString()!;
            }
            var a = await AgentNoteAsync("Review A", "area:one");
            var b = await AgentNoteAsync("Review B", "area:one");
            var c = await AgentNoteAsync("Review C", "area:one");
            var locked = await agent.PatchAsync($"/api/v1/notes/{c}", new() { DataObject = new { locked = true } });
            Assert.True(locked.Ok, await locked.TextAsync());
            var d = await AgentNoteAsync("Later D", "area:two");
            var e = await AgentNoteAsync("Later E", "area:two");
            var f = await AgentNoteAsync("Other F", "area:three");
            await agent.DisposeAsync();
            async Task<JsonElement> NoteAsync(string id) => (await (await page.APIRequest.GetAsync($"{api}/notes/{id}")).JsonAsync())!.Value;
            async Task<bool> PendingAsync(string id) => (await TagsAsync(page, $"{api}/notes/{id}")).Contains("review:pending");
            async Task UntilApprovedAsync(params string[] ids)
            {
                for (var i = 0; i < 50; i++)
                {
                    var any = false;
                    foreach (var id in ids) any |= await PendingAsync(id);
                    if (!any) return;
                    await Task.Delay(100, TestContext.Current.CancellationToken);
                }
                Assert.Fail("still awaiting review");
            }

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/notes");
            var rows = page.Locator("fb-notes-view .nv-item");
            await Assertions.Expect(rows).ToHaveCountAsync(6, new() { Timeout = 15000 });
            ILocator Row(string title) => page.Locator("fb-notes-view .nv-item", new() { HasText = title });

            // Three marked (the open one comes along): Approve in the list head.
            await Row("Review A").ClickAsync();
            await Row("Review B").ClickAsync(new() { Modifiers = new[] { KeyboardModifier.ControlOrMeta } });
            await Row("Review C").ClickAsync(new() { Modifiers = new[] { KeyboardModifier.ControlOrMeta } });
            await Assertions.Expect(page.Locator("fb-notes-view #list-title")).ToHaveTextAsync("3 selected");
            await page.Locator("fb-notes-view #approve-marked-btn").ClickAsync();
            await UntilApprovedAsync(a, b, c);
            foreach (var (id, title) in new[] { (a, "Review A"), (b, "Review B"), (c, "Review C") })
            {
                var note = await NoteAsync(id);
                Assert.Equal($"{title}: about ~€5 (~3 days).", note.GetProperty("content").GetString());
                var tags = note.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
                Assert.Contains("area:one", tags);
                Assert.Contains("source:mcp", tags);
            }
            Assert.True((await NoteAsync(c)).GetProperty("locked").GetBoolean());
            await Assertions.Expect(page.Locator("fb-notes-view #approve-marked-btn")).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("fb-notes-view #list-title")).Not.ToHaveTextAsync("3 selected");
            await Assertions.Expect(page.Locator("fb-notes-view .nv-item.pending")).ToHaveCountAsync(3);

            // Everything a filter shows, after a question; the rest awaits review still.
            var filter = await PostAsync(page, $"{api}/notes/filters", new { name = "Area two", expr = "area:two" });
            await page.ReloadAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(6, new() { Timeout = 15000 });
            await page.Locator("fb-notes-view #filters-btn").ClickAsync();
            await page.Locator($"fb-notes-view #filters-menu button[data-action='filter:{filter}']").ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(2);
            await page.Locator("fb-notes-view #filters-btn").ClickAsync();
            await page.Locator("fb-notes-view #filters-menu button[data-action='approve-shown']").ClickAsync();
            var dialog = page.Locator("sac-dialog[open]");
            await Assertions.Expect(dialog).ToContainTextAsync("Approve 2 notes?");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Approve" }).ClickAsync();
            await UntilApprovedAsync(d, e);
            Assert.True(await PendingAsync(f));
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
