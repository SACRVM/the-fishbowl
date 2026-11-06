using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// A workspace in a window of its own (fb.desktop.openWindow): from the
// Spaces window, from the switcher's icon at a workspace's end, with a
// modified click there and from the palette. The new window opens the
// workspace's desktop and names it in its title; this one stays where it is.
[Collection(UiCollection.Name)]
public class WorkspaceWindowTests
{
    private readonly PlaywrightFixture _fixture;

    public WorkspaceWindowTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Workspace_OpensInANewWindow_AndThisOneStays_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1200, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            var name = "Window " + Guid.NewGuid().ToString("N")[..6];
            var created = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces",
                new APIRequestContextOptions { DataObject = new { name } });
            var slug = (await created.JsonAsync())!.Value.GetProperty("slug").GetString()!;

            async Task ExpectSpaceWindow(IPage win)
            {
                await win.WaitForLoadStateAsync();
                Assert.EndsWith($"/#/space/{slug}", win.Url);
                await Assertions.Expect(win.Locator("fb-hub-view")).ToBeAttachedAsync(new() { Timeout = 15000 });
                await Assertions.Expect(win.Locator("#fb-context [slot='trigger']")).ToContainTextAsync(name, new() { Timeout = 10000 });
                // The task bar tells the windows apart: the space is in the title.
                await Assertions.Expect(win).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(name)), new() { Timeout = 10000 });
                await win.CloseAsync();
            }

            // The Spaces window: the row's own button.
            var spaces = await WindowApp.OpenAsync(page, _fixture.BaseUrl, "spaces");
            var row = page.Locator($"fb-spaces-settings-view #space-list .space-row[data-slug='{slug}']");
            var win = await context.RunAndWaitForPageAsync(() => row.Locator(".window-btn").ClickAsync());
            await ExpectSpaceWindow(win);
            // This window didn't move, and the Spaces window is still open.
            Assert.EndsWith("/#/", page.Url);
            await Assertions.Expect(spaces).ToHaveAttributeAsync("open", "");
            await Assertions.Expect(page.Locator("#fb-context [slot='trigger']")).ToContainTextAsync("Personal");
            await WindowApp.CloseAllAsync(page);

            // The switcher: the icon at the workspace's end, shown on hover.
            var pill = page.Locator("#fb-context [slot='trigger']");
            await pill.ClickAsync();
            var item = page.Locator($"#fb-context button[data-action='ctx:space:{slug}']");
            await item.HoverAsync();
            var icon = item.Locator(".fb-context-window");
            await Assertions.Expect(icon).ToHaveCSSAsync("opacity", "1");
            win = await context.RunAndWaitForPageAsync(() => icon.ClickAsync());
            await ExpectSpaceWindow(win);
            Assert.EndsWith("/#/", page.Url);
            await Assertions.Expect(page.Locator("#fb-context")).Not.ToHaveAttributeAsync("open", "");

            // A Ctrl-click on the row does the same instead of switching.
            await pill.ClickAsync();
            win = await context.RunAndWaitForPageAsync(() => item.ClickAsync(new() { Modifiers = new[] { KeyboardModifier.Control } }));
            await ExpectSpaceWindow(win);
            Assert.EndsWith("/#/", page.Url);

            // The palette: every workspace, the personal one too.
            var ids = await page.EvaluateAsync<string[]>("() => sac.commands.list().map((c) => c.id)");
            Assert.Contains("fb:desk:win:personal", ids);
            Assert.Contains($"fb:desk:win:{slug}", ids);
            win = await context.RunAndWaitForPageAsync(() =>
                page.EvaluateAsync($"() => sac.commands.list().find((c) => c.id === 'fb:desk:win:{slug}').run()"));
            await ExpectSpaceWindow(win);

            // This window's title names no workspace: it is the personal one.
            await Assertions.Expect(page).ToHaveTitleAsync("The Fishbowl");
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
