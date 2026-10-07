using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The list-and-editor apps keep their search, filters and list header in
// place while the list scrolls: the pane is exactly the split's height and
// only the list inside it scrolls (before, the whole pane grew with its rows
// and the header scrolled away).
[Collection(UiCollection.Name)]
public class ListPaneScrollTests
{
    private readonly PlaywrightFixture _fixture;

    public ListPaneScrollTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ListPanes_KeepSearchAndHeader_WhileTheListScrolls_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = 1200, Height = 500 },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        string? slug = null;
        try
        {
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Scroll " + Guid.NewGuid().ToString("N")[..6] } });
            slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString();
            var api = $"{_fixture.BaseUrl}/api/v1/spaces/{slug}";
            var month = DateTime.Today;
            for (var i = 1; i <= 25; i++)
            {
                await page.APIRequest.PostAsync($"{api}/notes", new() { DataObject = new { title = $"Note {i:00}", content = "x" } });
                await page.APIRequest.PostAsync($"{api}/todos", new() { DataObject = new { title = $"Todo {i:00}" } });
                await page.APIRequest.PostAsync($"{api}/contacts", new() { DataObject = new { kind = "person", firstName = "Person", lastName = $"{i:00}" } });
                await page.APIRequest.PostAsync($"{api}/events", new() { DataObject = new { title = $"Event {i:00}", startAt = new DateTime(month.Year, month.Month, i, 10, 0, 0, DateTimeKind.Utc).ToString("o") } });
                if (i <= 15) await page.APIRequest.PostAsync($"{api}/tables", new() { DataObject = new { name = $"list_{i:00}", columns = Array.Empty<object>() } });
            }

            async Task Check(string route, string fixedPart, string list, string item, int rows)
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/{route}");
                await Assertions.Expect(page.Locator(item)).ToHaveCountAsync(rows, new() { Timeout = 15000 });
                var before = (await page.Locator(fixedPart).BoundingBoxAsync())!.Y;
                // At the top the list doesn't fade; scrolled, its top edge does.
                var fade = $"() => getComputedStyle(document.querySelector(\"{list}\")).getPropertyValue('--fb-scroll-fade').trim()";
                Assert.Equal("0px", await page.EvaluateAsync<string>(fade));
                await page.Locator(list).HoverAsync();
                await page.Mouse.WheelAsync(0, 3000);
                await page.WaitForFunctionAsync($"() => document.querySelector(\"{list}\").scrollTop > 0");
                var after = (await page.Locator(fixedPart).BoundingBoxAsync())!.Y;
                Assert.True(before == after, $"{route}: {fixedPart} moved from {before} to {after}");
                await page.WaitForFunctionAsync($"() => ({fade})() === '24px'");
                await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"list-scroll-{route}.png") });
            }

            await Check("notes", "fb-notes-view .nv-search", "fb-notes-view .nv-items", "fb-notes-view .nv-item", 25);
            await Check("todos", "fb-todos-view .tv-search", "fb-todos-view .tv-items", "fb-todos-view .tv-item", 25);
            await Check("contacts", "fb-contacts-view .cv-search", "fb-contacts-view #cv-items", "fb-contacts-view .cv-item", 25);
            await Check("calendar", "fb-calendar-view .cv-month-nav", "fb-calendar-view #cv-agenda", "fb-calendar-view .cv-agenda-item", 25);
            await Check("tables", "fb-tables-view .tb-list-title", "fb-tables-view #tb-items", "fb-tables-view .tb-item", 15);
            Assert.Empty(errors);
        }
        finally
        {
            if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }
}
