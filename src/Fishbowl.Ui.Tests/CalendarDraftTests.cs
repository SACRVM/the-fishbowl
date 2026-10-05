using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// A new event is POSTed by its first save. Deleting it while that save is
// still on its way must not leave it behind, and a draft saved on leaving
// for another workspace lands where it was made.
[Collection(UiCollection.Name)]
public class CalendarDraftTests
{
    private readonly PlaywrightFixture _fixture;

    public CalendarDraftTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Calendar_DeleteWhileFirstSaveInFlight_AndDraftSavedOnLeaving_StayInTheirSpace_Test()
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
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Diary " + Guid.NewGuid().ToString("N")[..6] } });
            slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString()!;
            async Task<string> Titles(string root) =>
                await (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}{root}/events")).TextAsync();

            // The first save of a new event takes its time.
            await page.RouteAsync($"**/api/v1/spaces/{slug}/events", async route =>
            {
                if (route.Request.Method == "POST") await Task.Delay(800, TestContext.Current.CancellationToken);
                await route.ContinueAsync();
            });
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/calendar");

            // Title, then Trash: the click blurs the title, the blur starts the POST.
            var gone = "cal-gone " + Guid.NewGuid().ToString("N")[..6];
            await page.Locator("#cv-new-btn").ClickAsync();
            await page.Locator("#cv-title").FillAsync(gone);
            await page.Locator("#fb-view-toolbar button[title='Delete event']").ClickAsync();
            await page.Locator("sac-dialog[title='Delete this event?']")
                .GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("#cv-editor-wrap")).ToBeHiddenAsync(new() { Timeout = 10000 });
            await page.WaitForTimeoutAsync(1000);
            Assert.DoesNotContain(gone, await Titles($"/api/v1/spaces/{slug}"));
            await Assertions.Expect(page.Locator("fb-calendar-view")).Not.ToContainTextAsync(gone);

            // A draft with a title, then straight to the personal workspace:
            // the save on leaving goes to the space.
            var kept = "cal-kept " + Guid.NewGuid().ToString("N")[..6];
            await page.Locator("#cv-new-btn").ClickAsync();
            await page.Locator("#cv-title").FillAsync(kept);
            await page.EvaluateAsync("() => { location.hash = '#/calendar'; }");
            var found = false;
            for (var i = 0; i < 30 && !found; i++)
            {
                found = (await Titles($"/api/v1/spaces/{slug}")).Contains(kept);
                if (!found) await page.WaitForTimeoutAsync(200);
            }
            Assert.True(found, "the draft was not saved in the space");
            Assert.DoesNotContain(kept, await Titles(""));
            Assert.Empty(errors);
        }
        finally
        {
            if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }
}
