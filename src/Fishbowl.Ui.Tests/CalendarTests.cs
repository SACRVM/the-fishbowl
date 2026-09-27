using System.Text.Json;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The calendar editor in a real browser: saves of a new event run one after
// another (typing a title and flipping "All day" right away used to create
// two events), the All-day switch, and multi-day events on every day.
[Collection(UiCollection.Name)]
public class CalendarTests
{
    private readonly PlaywrightFixture _fixture;

    public CalendarTests(PlaywrightFixture fixture) => _fixture = fixture;

    private async Task<JsonElement[]> EventsTitledAsync(IPage page, string title)
    {
        var from = DateTime.UtcNow.AddDays(-40).ToString("o");
        var to = DateTime.UtcNow.AddDays(40).ToString("o");
        var res = await page.APIRequest.GetAsync(
            $"{_fixture.BaseUrl}/api/v1/events?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");
        return (await res.JsonAsync())!.Value.EnumerateArray()
            .Where(e => e.GetProperty("title").GetString() == title).ToArray();
    }

    [Fact]
    public async Task Calendar_NewEvent_TitleThenAllDay_CreatesOneEvent_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var title = "cal-once " + Guid.NewGuid().ToString("N")[..6];
        try
        {
            await page.GotoAsync(_fixture.BaseUrl + "/#/calendar");
            await page.Locator("#cv-new-btn").ClickAsync();
            await page.Locator("#cv-title").FillAsync(title);
            // Leaving the title (blur → save) and flipping the switch (→ save)
            // fire two saves of a not-yet-created event back to back.
            var toggle = page.Locator("sac-toggle#cv-allday");
            await toggle.ClickAsync();
            await Assertions.Expect(toggle).ToHaveAttributeAsync("checked", "");

            JsonElement[] found = [];
            for (var i = 0; i < 30; i++)
            {
                found = await EventsTitledAsync(page, title);
                if (found.Length > 0 && found.All(e => e.GetProperty("allDay").GetBoolean())) break;
                await page.WaitForTimeoutAsync(200);
            }
            await page.WaitForTimeoutAsync(800);
            found = await EventsTitledAsync(page, title);
            var ev = Assert.Single(found);
            Assert.True(ev.GetProperty("allDay").GetBoolean());
            // The browser's zone rides along, so a series repeats in local time.
            Assert.False(string.IsNullOrEmpty(ev.GetProperty("timeZone").GetString()));

            // Reminder and Repeat carry the kit's select chevron.
            await Assertions.Expect(page.Locator(".select > #cv-reminder")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".select > #cv-repeat")).ToHaveCountAsync(1);
        }
        finally
        {
            foreach (var e in await EventsTitledAsync(page, title))
                await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/events/{e.GetProperty("id").GetString()}");
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Calendar_MultiDayEvent_ShowsOnEveryDay_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var title = "cal-span " + Guid.NewGuid().ToString("N")[..6];
        string? id = null;
        try
        {
            // Three days in the middle of the current month, local 09:00 → +2 days 17:00.
            var now = DateTime.Now;
            var startLocal = new DateTime(now.Year, now.Month, 10, 9, 0, 0, DateTimeKind.Local);
            var res = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/events", new APIRequestContextOptions
            {
                DataObject = new
                {
                    title,
                    startAt = startLocal.ToUniversalTime().ToString("o"),
                    endAt = startLocal.AddDays(2).AddHours(8).ToUniversalTime().ToString("o"),
                },
            });
            Assert.True(res.Ok, $"create failed: {res.Status}");
            id = (await res.JsonAsync())!.Value.GetProperty("id").GetString();

            await page.GotoAsync(_fixture.BaseUrl + "/#/calendar");
            var chips = page.Locator(".cv-grid .cv-chip", new PageLocatorOptions { HasText = title });
            await Assertions.Expect(chips).ToHaveCountAsync(3);
            // Only the first day shows the start time; the others are continuations.
            await Assertions.Expect(page.Locator(".cv-grid .cv-chip.cont", new PageLocatorOptions { HasText = title })).ToHaveCountAsync(2);
            await Assertions.Expect(page.Locator(".cv-agenda-item", new PageLocatorOptions { HasText = title })).ToHaveCountAsync(3);
        }
        finally
        {
            if (id is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/events/{id}");
            await context.CloseAsync();
        }
    }
}
