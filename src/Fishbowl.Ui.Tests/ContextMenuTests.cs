using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// Context menus (kit 2.29, sac.contextMenu): an app, not a website — a list
// row opens its own menu on a right-click (a long press on touch), the
// browser's never shows (text fields keep theirs).
[Collection(UiCollection.Name)]
public class ContextMenuTests
{
    private readonly PlaywrightFixture _fixture;

    public ContextMenuTests(PlaywrightFixture fixture) => _fixture = fixture;

    private static ILocator Menu(IPage page) => page.Locator("sac-menu.sac-context-menu[open]");
    private static ILocator Item(IPage page, string label) =>
        Menu(page).Locator("button[data-action]", new() { HasText = label });

    /// <summary>Right-click a row; the menu shows, its fade-in done.</summary>
    private static async Task OpenAsync(IPage page, ILocator row)
    {
        await Assertions.Expect(Menu(page)).ToHaveCountAsync(0);
        await row.ScrollIntoViewIfNeededAsync();
        await row.ClickAsync(new() { Button = MouseButton.Right });
        await Assertions.Expect(Menu(page).Locator("button[data-action]").First).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(250);
    }

    [Fact]
    public async Task Lists_OpenTheirOwnMenu_NeverTheBrowsers_Test()
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
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Menus " + Guid.NewGuid().ToString("N")[..6] } });
            slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString();
            var api = $"{_fixture.BaseUrl}/api/v1/spaces/{slug}";
            Assert.True((await page.APIRequest.PostAsync($"{api}/notes", new() { DataObject = new { title = "Menu note", content = "text" } })).Ok);
            Assert.True((await page.APIRequest.PostAsync($"{api}/todos", new() { DataObject = new { title = "Menu todo" } })).Ok);
            Assert.True((await page.APIRequest.PostAsync($"{api}/contacts", new() { DataObject = new { kind = "person", firstName = "Menu", lastName = "Person" } })).Ok);

            // Notes: Open, Pin, Archive, Delete — pinning from the menu pins the row.
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/notes");
            var note = page.Locator("fb-notes-view .nv-item", new() { HasText = "Menu note" });
            await Assertions.Expect(note).ToBeVisibleAsync(new() { Timeout = 15000 });
            await OpenAsync(page, note);
            foreach (var label in new[] { "Open", "Pin to top", "Archive", "Delete" })
                await Assertions.Expect(Item(page, label)).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_context_menu.png") });
            await Item(page, "Pin to top").ClickAsync();
            await Assertions.Expect(note.Locator(".nv-item-action.pin")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));

            // The browser's own menu never shows on the app; a text field keeps it.
            Assert.False(await page.EvaluateAsync<bool>(
                "() => document.querySelector('#app-root').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }))"));
            Assert.True(await page.EvaluateAsync<bool>(
                "() => document.querySelector('fb-notes-view input[type=search], fb-notes-view input').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }))"));

            // Todos: done from the menu.
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/todos");
            var todo = page.Locator("fb-todos-view .tv-item", new() { HasText = "Menu todo" });
            await Assertions.Expect(todo).ToBeVisibleAsync(new() { Timeout = 15000 });
            await OpenAsync(page, todo);
            await Assertions.Expect(Item(page, "Delete todo")).ToBeVisibleAsync();
            await Item(page, "Mark as done").ClickAsync();
            await Assertions.Expect(todo).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bcompleted\b"));

            // Contacts: "Select" first (marking starts there on touch), then Open and Delete.
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/contacts");
            var person = page.Locator("fb-contacts-view .cv-item", new() { HasText = "Menu Person" });
            await Assertions.Expect(person).ToBeVisibleAsync(new() { Timeout = 15000 });
            await OpenAsync(page, person);
            await Assertions.Expect(Menu(page).Locator("button[data-action]").First).ToContainTextAsync("Select");
            await Assertions.Expect(Item(page, "Open")).ToBeVisibleAsync();
            await Assertions.Expect(Item(page, "Delete contact")).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            Assert.Empty(errors);
        }
        finally
        {
            if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }
}
