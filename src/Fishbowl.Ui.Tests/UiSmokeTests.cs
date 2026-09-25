using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

public class UiSmokeTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture _fixture;

    public UiSmokeTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Hub_LoadsAndNavigatesToNotes_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.BaseUrl);

        // Hub view renders with its tiles.
        var notesTile = page.Locator("a.tile[href='#/notes']");
        var todosTile = page.Locator("a.tile[href='#/todos']");
        var calendarTile = page.Locator("a.tile[href='#/calendar']");
        await notesTile.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });
        Assert.True(await notesTile.IsVisibleAsync());
        Assert.True(await todosTile.IsVisibleAsync());
        Assert.True(await calendarTile.IsVisibleAsync());

        // Click Notes tile -> hash changes, notes view mounts.
        await notesTile.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith("#/notes"), new PageWaitForURLOptions { Timeout = 3000 });
        Assert.EndsWith("#/notes", page.Url);

        var notesView = page.Locator("fb-notes-view");
        await notesView.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });
        Assert.True(await notesView.IsVisibleAsync());

        await context.CloseAsync();
    }

    // A phone in portrait: touch, 375px wide. The shell and the list/detail
    // views come from the vendored appkit, which collapses a split to one
    // pane at this width — these guard that the views keep opting in.
    private static BrowserNewContextOptions Phone() => new()
    {
        IgnoreHTTPSErrors = true,
        ViewportSize = new ViewportSize { Width = 375, Height = 740 },
        IsMobile = true,
        HasTouch = true,
    };

    [Theory]
    [InlineData("#/")]
    [InlineData("#/notes")]
    [InlineData("#/todos")]
    [InlineData("#/calendar")]
    public async Task Phone_ViewHasNoHorizontalScroll_Test(string hash)
    {
        var context = await _fixture.Browser!.NewContextAsync(Phone());
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.BaseUrl + "/" + hash);
        await page.Locator("#app-root > *").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });
        await page.WaitForTimeoutAsync(300);

        var overflow = await page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
        Assert.Equal(0, overflow);

        await context.CloseAsync();
    }

    [Fact]
    public async Task Phone_NotesOpensEditorAndGoesBack_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(Phone());
        var page = await context.NewPageAsync();

        // Seed one note through the API (the test-auth user owns it).
        var created = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/notes", new APIRequestContextOptions
        {
            DataObject = new { title = "Phone smoke note", content = "Body" },
        });
        Assert.True(created.Ok, $"seed note failed: {created.Status}");

        await page.GotoAsync(_fixture.BaseUrl + "/#/notes");
        var split = page.Locator("fb-notes-view sac-split");
        await split.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });

        // Collapsed: the list shows alone.
        Assert.True(await split.EvaluateAsync<bool>("s => s.collapsed"));
        Assert.Equal("start", await split.GetAttributeAsync("show"));

        // Opening a note brings the editor forward.
        await page.Locator(".nv-item", new PageLocatorOptions { HasText = "Phone smoke note" }).First.TapAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('fb-notes-view sac-split').getAttribute('show') === 'end'");
        Assert.Equal("Phone smoke note", await page.Locator("#title").InputValueAsync());
        Assert.True(await page.Locator("#title").IsVisibleAsync());

        // The split's back bar returns to the list.
        await split.Locator(".back button").TapAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('fb-notes-view sac-split').getAttribute('show') === 'start'");
        Assert.True(await page.Locator(".nv-item").First.IsVisibleAsync());

        await context.CloseAsync();
    }
}
