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
            BypassCSP = true,
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
            BypassCSP = true,
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

    [Fact]
    public async Task Calendar_AllDayOnThenOff_RestoresTheTimes_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var title = "cal-times " + Guid.NewGuid().ToString("N")[..6];
        string? id = null;
        try
        {
            var now = DateTime.Now;
            var startLocal = new DateTime(now.Year, now.Month, 12, 14, 30, 0, DateTimeKind.Local);
            var res = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/events", new APIRequestContextOptions
            {
                DataObject = new
                {
                    title,
                    startAt = startLocal.ToUniversalTime().ToString("o"),
                    endAt = startLocal.AddMinutes(75).ToUniversalTime().ToString("o"),
                },
            });
            Assert.True(res.Ok, $"create failed: {res.Status}");
            id = (await res.JsonAsync())!.Value.GetProperty("id").GetString();

            await page.GotoAsync(_fixture.BaseUrl + "/#/calendar");
            await page.Locator(".cv-agenda-item", new PageLocatorOptions { HasText = title }).First.ClickAsync();
            var times = "() => [document.querySelector('#cv-start')._fbTime.value, document.querySelector('#cv-end')._fbTime.value].join(' ')";
            await page.WaitForFunctionAsync($"() => ({times})() === '14:30 15:45'");
            var shots = Path.Combine(Path.GetTempPath(), "cal-final");
            Directory.CreateDirectory(shots);
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "allday-before.png") });

            // On, then off again: the event's own times come back, not 09:00.
            var toggle = page.Locator("sac-toggle#cv-allday");
            await toggle.ClickAsync();
            await Assertions.Expect(toggle).ToHaveAttributeAsync("checked", "");
            await toggle.ClickAsync();
            await Assertions.Expect(toggle).Not.ToHaveAttributeAsync("checked", "");
            await page.WaitForFunctionAsync($"() => ({times})() === '14:30 15:45'");
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "allday-after.png") });

            // …and that is what gets saved.
            JsonElement ev = default;
            for (var i = 0; i < 30; i++)
            {
                ev = (await (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/events/{id}")).JsonAsync())!.Value;
                if (!ev.GetProperty("allDay").GetBoolean()
                    && ev.GetProperty("startAt").GetDateTimeOffset().ToLocalTime().TimeOfDay == new TimeSpan(14, 30, 0)) break;
                await page.WaitForTimeoutAsync(200);
            }
            Assert.False(ev.GetProperty("allDay").GetBoolean());
            Assert.Equal(new TimeSpan(14, 30, 0), ev.GetProperty("startAt").GetDateTimeOffset().ToLocalTime().TimeOfDay);
            Assert.Equal(new TimeSpan(15, 45, 0), ev.GetProperty("endAt").GetDateTimeOffset().ToLocalTime().TimeOfDay);
        }
        finally
        {
            if (id is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/events/{id}");
            await context.CloseAsync();
        }
    }

    private static string Shots()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cal-final2");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Calendar_MoreMenu_ListsTheDayAndOpensAnEvent_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var tag = "cal-more " + Guid.NewGuid().ToString("N")[..6];
        var ids = new List<string>();
        try
        {
            var now = DateTime.Now;
            var day = new DateTime(now.Year, now.Month, 20, 0, 0, 0, DateTimeKind.Local);
            for (var i = 0; i < 5; i++)
            {
                var start = day.AddHours(9 + i * 2);
                var res = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/events", new APIRequestContextOptions
                {
                    DataObject = new
                    {
                        title = $"{tag} #{i}",
                        startAt = start.ToUniversalTime().ToString("o"),
                        endAt = start.AddHours(1).ToUniversalTime().ToString("o"),
                    },
                });
                Assert.True(res.Ok, $"create failed: {res.Status}");
                ids.Add((await res.JsonAsync())!.Value.GetProperty("id").GetString()!);
            }

            await page.GotoAsync(_fixture.BaseUrl + "/#/calendar");
            var cell = page.Locator($".cv-day[data-key='{day:yyyy-MM-dd}']");
            var more = cell.Locator(".cv-more");
            await Assertions.Expect(more).ToHaveTextAsync("+2 more");

            // Keyboard: the link is a button; Enter opens the day's list.
            await more.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            var menu = page.Locator("sac-menu#cv-more-menu");
            await Assertions.Expect(menu).ToHaveAttributeAsync("open", "");
            await Assertions.Expect(menu.Locator("button", new LocatorLocatorOptions { HasText = tag })).ToHaveCountAsync(5);
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots(), "more-menu.png") });
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(menu).Not.ToHaveAttributeAsync("open", "");

            // Mouse: a pick opens that event.
            await more.ClickAsync();
            await menu.Locator("button", new LocatorLocatorOptions { HasText = $"{tag} #4" }).ClickAsync();
            await Assertions.Expect(page.Locator("#cv-title")).ToHaveValueAsync($"{tag} #4");
        }
        finally
        {
            foreach (var id in ids) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/events/{id}");
            await context.CloseAsync();
        }
    }

    // All-day events are dates: made in Tokyo, the 15th is the 15th in
    // Los Angeles and Lisbon too (it used to be local midnight in Tokyo,
    // i.e. the 14th in the Americas).
    [Fact]
    public async Task Calendar_AllDay_IsTheSameDayInEveryZone_Test()
    {
        var title = "cal-zone " + Guid.NewGuid().ToString("N")[..6];
        var now = DateTime.Now;
        var key = new DateTime(now.Year, now.Month, 15).ToString("yyyy-MM-dd");
        var tokyo = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            TimezoneId = "Asia/Tokyo",
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await tokyo.NewPageAsync();
        var contexts = new List<IBrowserContext> { tokyo };
        try
        {
            await page.GotoAsync(_fixture.BaseUrl + "/#/calendar");
            await page.Locator($".cv-day[data-key='{key}'] .cv-day-num").DblClickAsync();
            await page.Locator("#cv-title").FillAsync(title);
            var toggle = page.Locator("sac-toggle#cv-allday");
            await toggle.ClickAsync();
            await Assertions.Expect(toggle).ToHaveAttributeAsync("checked", "");

            JsonElement ev = default;
            for (var i = 0; i < 30; i++)
            {
                var found = await EventsTitledAsync(page, title);
                if (found.Length == 1 && found[0].GetProperty("allDay").GetBoolean()) { ev = found[0]; break; }
                await page.WaitForTimeoutAsync(200);
            }
            Assert.Equal(key, ev.GetProperty("startDate").GetString());
            Assert.Equal(DateOnly.Parse(key).AddDays(1).ToString("yyyy-MM-dd"), ev.GetProperty("endDate").GetString());
            await page.Locator("#cv-back-btn, .cv-back-btn").First.ClickAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots(), "zone-tokyo.png") });

            foreach (var zone in new[] { "America/Los_Angeles", "Europe/Lisbon" })
            {
                var ctx = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
                {
                    IgnoreHTTPSErrors = true,
                    BypassCSP = true,
                    TimezoneId = zone,
                    ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
                });
                contexts.Add(ctx);
                var p = await ctx.NewPageAsync();
                await p.GotoAsync(_fixture.BaseUrl + "/#/calendar");
                var chips = p.Locator(".cv-grid .cv-chip", new PageLocatorOptions { HasText = title });
                await Assertions.Expect(chips).ToHaveCountAsync(1);
                await Assertions.Expect(p.Locator($".cv-day[data-key='{key}'] .cv-chip", new PageLocatorOptions { HasText = title })).ToHaveCountAsync(1);
                // The editor shows the same single day.
                await chips.ClickAsync();
                await p.WaitForFunctionAsync(
                    "() => document.querySelector('#cv-start')?.value && document.querySelector('#cv-end')?.value");
                if (zone == "America/Los_Angeles")
                {
                    await p.Locator("#cv-back-btn, .cv-back-btn").First.ClickAsync();
                    await p.ScreenshotAsync(new() { Path = Path.Combine(Shots(), "zone-la.png") });
                }
            }
        }
        finally
        {
            foreach (var e in await EventsTitledAsync(page, title))
                await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/events/{e.GetProperty("id").GetString()}");
            foreach (var c in contexts) await c.CloseAsync();
        }
    }

    [Fact]
    public async Task Calendar_MultiDayAllDay_ByDates_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var title = "cal-trip " + Guid.NewGuid().ToString("N")[..6];
        var single = "cal-holiday " + Guid.NewGuid().ToString("N")[..6];
        var ids = new List<string>();
        try
        {
            var now = DateTime.Now;
            var first = new DateOnly(now.Year, now.Month, 8);
            foreach (var (t, s, e) in new[] { (title, first, first.AddDays(3)), (single, first.AddDays(5), first.AddDays(6)) })
            {
                var res = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/events", new APIRequestContextOptions
                {
                    DataObject = new
                    {
                        title = t,
                        allDay = true,
                        startAt = s.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("o"),
                        startDate = s.ToString("yyyy-MM-dd"),
                        endDate = e.ToString("yyyy-MM-dd"),
                    },
                });
                Assert.True(res.Ok, $"create failed: {res.Status}");
                ids.Add((await res.JsonAsync())!.Value.GetProperty("id").GetString()!);
            }

            await page.GotoAsync(_fixture.BaseUrl + "/#/calendar");
            // Three dates (end exclusive): the first plus two continuations.
            await Assertions.Expect(page.Locator(".cv-grid .cv-chip", new PageLocatorOptions { HasText = title })).ToHaveCountAsync(3);
            await Assertions.Expect(page.Locator($".cv-day[data-key='{first.AddDays(2):yyyy-MM-dd}'] .cv-chip.cont", new PageLocatorOptions { HasText = title })).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator($".cv-day[data-key='{first.AddDays(3):yyyy-MM-dd}'] .cv-chip", new PageLocatorOptions { HasText = title })).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".cv-grid .cv-chip", new PageLocatorOptions { HasText = single })).ToHaveCountAsync(1);
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots(), "allday-month.png") });

            // The editor shows the last day inclusive.
            await page.Locator(".cv-grid .cv-chip:not(.cont)", new PageLocatorOptions { HasText = title }).ClickAsync();
            await Assertions.Expect(page.Locator("sac-toggle#cv-allday")).ToHaveAttributeAsync("checked", "");
            var last = await page.EvaluateAsync<string>(
                "() => { const d = fb.format.readInput(document.querySelector('#cv-end')); return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`; }");
            Assert.Equal(first.AddDays(2).ToString("yyyy-MM-dd"), last);
        }
        finally
        {
            foreach (var id in ids) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/events/{id}");
            await context.CloseAsync();
        }
    }
}
