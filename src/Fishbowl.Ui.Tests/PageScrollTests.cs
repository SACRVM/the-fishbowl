using Microsoft.Playwright;
using Xunit;

namespace Fishbowl.Ui.Tests;

/// <summary>
/// An app, not a website: the page itself never scrolls. Every full-screen
/// app keeps to the viewport and scrolls only inside its panes — a second
/// scrollbar at the window's edge means something pushed the page longer.
/// (The desktop is the exception: its tiles scroll the page, like SACRVM
/// Desktop's.)
/// </summary>
[Collection(UiCollection.Name)]
public class PageScrollTests
{
    private readonly PlaywrightFixture _fixture;

    public PageScrollTests(PlaywrightFixture fixture) => _fixture = fixture;

    // What sticks out below or beside the viewport, for the failure message.
    internal const string Overflow = @"() => {
        const root = document.scrollingElement;
        const over = [...document.querySelectorAll('body *')].filter(e => {
            const r = e.getBoundingClientRect();
            return r.height > 0 && (r.bottom > innerHeight + 1 || r.right > innerWidth + 1);
        }).slice(0, 5).map(e => e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/).join('.') : '')
            + ' ' + Math.round(e.getBoundingClientRect().bottom) + 'x' + Math.round(e.getBoundingClientRect().right));
        const scrollers = [document.documentElement, document.body, ...document.querySelectorAll('body *')].filter(e => {
            const cs = getComputedStyle(e);
            return (e === document.documentElement || /(auto|scroll)/.test(cs.overflowY)) && e.scrollHeight > e.clientHeight + 1;
        }).map(e => e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/).join('.') : '') + ' ' + e.scrollHeight + '/' + e.clientHeight);
        return { h: root.scrollHeight - root.clientHeight, w: root.scrollWidth - root.clientWidth, over: over.join(' | ') + ' || scrollers: ' + scrollers.join(' | ') };
    }";

    [Theory]
    [InlineData(1200, 700)]
    [InlineData(390, 760)]
    public async Task NoApp_ScrollsThePage_Test(int width, int height)
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = width, Height = height },
        });
        var page = await context.NewPageAsync();
        string? slug = null;
        try
        {
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Page " + Guid.NewGuid().ToString("N")[..6] } });
            slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString();
            var api = $"{_fixture.BaseUrl}/api/v1/spaces/{slug}";
            for (var i = 1; i <= 30; i++)
            {
                await page.APIRequest.PostAsync($"{api}/notes", new() { DataObject = new { title = $"Note {i:00}", content = string.Concat(Enumerable.Repeat("A line of text.\n\n", 40)) } });
                await page.APIRequest.PostAsync($"{api}/todos", new() { DataObject = new { title = $"Todo {i:00}" } });
                await page.APIRequest.PostAsync($"{api}/contacts", new() { DataObject = new { kind = "person", firstName = "Person", lastName = $"{i:00}" } });
            }

            var problems = new List<string>();
            async Task Check(string route, string ready, string? click = null)
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/{route}");
                await page.Locator(ready).First.WaitForAsync(new() { Timeout = 15000 });
                if (click is not null) await page.Locator(click).First.ClickAsync();
                await page.WaitForTimeoutAsync(600);
                var o = await page.EvaluateAsync<System.Text.Json.JsonElement>(Overflow);
                if (o.GetProperty("h").GetInt32() > 0 || o.GetProperty("w").GetInt32() > 0)
                    problems.Add($"{route}{(click is null ? "" : " (open)")}: page scrolls {o.GetProperty("h")}px down, {o.GetProperty("w")}px across — {o.GetProperty("over")}");
            }

            await Check("notes", "fb-notes-view .nv-item");
            await Check("notes", "fb-notes-view .nv-item", "fb-notes-view .nv-item");
            await Check("todos", "fb-todos-view .tv-item");
            await Check("contacts", "fb-contacts-view .cv-item");
            await Check("contacts", "fb-contacts-view .cv-item", "fb-contacts-view .cv-item");
            await Check("calendar", "fb-calendar-view .cv-month-nav");
            await Check("files", "fb-files-view sac-file-browser");
            await Check("tables", "fb-tables-view");
            await Check("mail", "fb-mail-view");
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
        finally
        {
            if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }
}
