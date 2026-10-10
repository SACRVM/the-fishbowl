using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// Every list's keys (kit 2.30, sac.selection keyboard): ↓ opens the next row
// instead of scrolling, Shift+↓ marks (the head says how many), Delete asks
// once for the marked ones. Each app in a space of its own.
[Collection(UiCollection.Name)]
public class ListKeyboardTests
{
    private static readonly Regex Selected = new(@"\bselected\b");
    private readonly PlaywrightFixture _fixture;

    public ListKeyboardTests(PlaywrightFixture fixture) => _fixture = fixture;

    private async Task InSpaceAsync(string what, Func<IPage, string, Task> body)
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
        string? slug = null;
        try
        {
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = what + " " + Guid.NewGuid().ToString("N")[..6] } });
            slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString()!;
            await body(page, slug);
            Assert.Empty(errors);
        }
        finally
        {
            if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    private async Task<string> PostAsync(IPage page, string slug, string path, object body)
    {
        var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/{path}", new() { DataObject = body });
        Assert.True(res.Ok, await res.TextAsync());
        return (await res.JsonAsync())!.Value.GetProperty("id").GetString()!;
    }

    // The walk every app's list shares: open the first row, ↓ opens the
    // second, Shift+↓ marks it and the third, Delete asks for both.
    private static async Task WalkAsync(IPage page, ILocator rows, ILocator head, string dialogTitle)
    {
        await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 15000 });
        await rows.Nth(0).ClickAsync();
        await Assertions.Expect(rows.Nth(0)).ToHaveClassAsync(Selected, new() { Timeout = 5000 });

        await page.Keyboard.PressAsync("ArrowDown");
        await Assertions.Expect(rows.Nth(1)).ToHaveClassAsync(Selected, new() { Timeout = 5000 });
        Assert.Equal(0, await page.EvaluateAsync<double>("() => window.scrollY"));

        await page.Keyboard.PressAsync("Shift+ArrowDown");
        await Assertions.Expect(head).ToHaveTextAsync("2 selected");

        await page.Keyboard.PressAsync("Delete");
        await page.Locator($"sac-dialog[title='{dialogTitle}']").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(1, new() { Timeout = 5000 });
        await Assertions.Expect(head).Not.ToHaveTextAsync("2 selected");
    }

    [Fact]
    public Task Notes_Keys_OpenMarkAndDelete_Test() => InSpaceAsync("Note keys", async (page, slug) =>
    {
        foreach (var t in new[] { "First", "Second", "Third" })
            await PostAsync(page, slug, "notes", new { title = t, content = t + " text" });
        await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/notes");
        var view = page.Locator("fb-notes-view");
        await WalkAsync(page, view.Locator(".nv-item"), view.Locator("#list-title"), "Delete 2 notes?");
    });

    // A tag picked or a search typed that still shows the open note: it stays
    // highlighted and the arrows go on from it — from the search field too.
    [Fact]
    public Task Notes_FilterOrSearch_KeysGoOnFromTheOpenNote_Test() => InSpaceAsync("Note filter keys", async (page, slug) =>
    {
        await PostAsync(page, slug, "notes", new { title = "Alpha", content = "alpha text", tags = new[] { "work" } });
        await PostAsync(page, slug, "notes", new { title = "Beta", content = "beta text" });
        await PostAsync(page, slug, "notes", new { title = "Gamma", content = "gamma text", tags = new[] { "work" } });
        await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/notes");
        var view = page.Locator("fb-notes-view");
        var rows = view.Locator(".nv-item");
        var alpha = rows.Filter(new() { HasText = "Alpha" });
        var gamma = rows.Filter(new() { HasText = "Gamma" });
        var step = async () => await rows.EvaluateAllAsync<int>("(rs) => rs.findIndex((r) => r.classList.contains('selected'))") == 0 ? "ArrowDown" : "ArrowUp";
        await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 15000 });
        await alpha.ClickAsync();
        await Assertions.Expect(alpha).ToHaveClassAsync(Selected, new() { Timeout = 5000 });

        await view.Locator("#tag-filter sac-chip[label='work']").ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(2, new() { Timeout = 5000 });
        await Assertions.Expect(alpha).ToHaveClassAsync(Selected);
        await page.Keyboard.PressAsync(await step());
        await Assertions.Expect(gamma).ToHaveClassAsync(Selected, new() { Timeout = 5000 });

        var search = view.Locator("#search-input");
        await search.FillAsync("text");
        await Assertions.Expect(gamma).ToHaveClassAsync(Selected, new() { Timeout = 5000 });
        await search.PressAsync(await step());
        await Assertions.Expect(alpha).ToHaveClassAsync(Selected, new() { Timeout = 5000 });
    });

    [Fact]
    public Task Todos_Keys_OpenMarkAndDelete_Test() => InSpaceAsync("Todo keys", async (page, slug) =>
    {
        foreach (var t in new[] { "First", "Second", "Third" })
            await PostAsync(page, slug, "todos", new { title = t });
        await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/todos");
        var view = page.Locator("fb-todos-view");
        await WalkAsync(page, view.Locator(".tv-item"), view.Locator("#list-title"), "Delete 2 todos?");
    });

    [Fact]
    public Task Contacts_Keys_OpenMarkAndDelete_Test() => InSpaceAsync("Contact keys", async (page, slug) =>
    {
        foreach (var n in new[] { "Ada", "Bea", "Cy" })
            await PostAsync(page, slug, "contacts", new { kind = "person", firstName = n, lastName = "Keys" });
        await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/contacts");
        var view = page.Locator("fb-contacts-view");
        await WalkAsync(page, view.Locator(".cv-item"), view.Locator("#cv-head-title"), "Delete 2 contacts?");
    });

    [Fact]
    public Task Calendar_AgendaKeys_OpenMarkAndDelete_Test() => InSpaceAsync("Event keys", async (page, slug) =>
    {
        var now = DateTime.UtcNow;
        for (var day = 10; day <= 12; day++)
        {
            var start = new DateTime(now.Year, now.Month, day, 9, 0, 0, DateTimeKind.Utc);
            await PostAsync(page, slug, "events", new { title = $"Event {day}", startAt = start.ToString("o"), endAt = start.AddHours(1).ToString("o") });
        }
        await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/calendar");
        var view = page.Locator("fb-calendar-view");
        await WalkAsync(page, view.Locator("#cv-agenda .cv-agenda-item"), view.Locator("#cv-list-title"), "Delete 2 events?");
        // The open event was among them: the editor gave way to the month.
        await Assertions.Expect(view.Locator("#cv-editor-wrap")).ToBeHiddenAsync();
    });

    // The trash has nothing to open: the keys move a cursor, Shift marks,
    // the window's bar offers the marked ones, Delete deletes them for good.
    [Fact]
    public Task Trash_Keys_MarkAndDeleteForGood_Test() => InSpaceAsync("Trash keys", async (page, slug) =>
    {
        foreach (var t in new[] { "First", "Second", "Third" })
        {
            var id = await PostAsync(page, slug, "notes", new { title = t });
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/notes/{id}");
        }
        var win = await WindowApp.OpenAsync(page, _fixture.BaseUrl, "trash", $"#/space/{slug}/");
        var rows = win.Locator(".fb-trash-row");
        await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 15000 });

        await win.Locator("#trash-list").FocusAsync();
        await page.Keyboard.PressAsync("Home");
        await page.Keyboard.PressAsync("Shift+ArrowDown");
        var bar = page.Locator("#fb-win-trash > .wa-bar[slot='toolbar']");
        await Assertions.Expect(bar).ToContainTextAsync("Restore selected (2)");

        await page.Keyboard.PressAsync("Delete");
        await page.Locator("sac-dialog[title='Delete 2 items for good?']").GetByRole(AriaRole.Button, new() { Name = "Delete for good", Exact = true }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(1, new() { Timeout = 5000 });
        await Assertions.Expect(bar).Not.ToContainTextAsync("Restore selected");
    });
}
