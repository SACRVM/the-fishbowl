using System.Text.Json;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The calendar's tools (sync spec, phase 1): the contacts' birthdays as
// read-only entries that show the person in brief (Go to contact opens it), the toolbar's .ics import and
// export, and subscription links — made, read without signing in, revoked.
[Collection(UiCollection.Name)]
public class CalendarToolsTests
{
    private readonly PlaywrightFixture _fixture;

    public CalendarToolsTests(PlaywrightFixture fixture) => _fixture = fixture;

    private const string Ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VEVENT\r\nUID:ui-import-1@test\r\n"
        + "DTSTAMP:20261001T000000Z\r\nSUMMARY:Imported dentist\r\nDTSTART:{0}T090000Z\r\nDTEND:{0}T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    [Fact]
    public async Task Calendar_Birthdays_Import_Export_AndSubscriptionLinks_Test()
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
        try
        {
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Cal " + Guid.NewGuid().ToString("N")[..6] } });
            var slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString();
            // A birthday this month (day 15, an age of 30).
            var today = DateTime.Today;
            var day = new DateTime(today.Year, today.Month, 15);
            await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/contacts",
                new() { DataObject = new { kind = "person", firstName = "Grace", lastName = "Hopper", birthday = day.AddYears(-30).ToString("yyyy-MM-dd"), email = "grace@navy.test", role = "Rear Admiral" } });

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/calendar");
            var view = page.Locator("fb-calendar-view");
            var chip = view.Locator(".cv-chip.birthday");
            await Assertions.Expect(chip).ToHaveCountAsync(1, new() { Timeout = 15000 });
            await Assertions.Expect(chip).ToContainTextAsync("Grace Hopper (30)");
            await Assertions.Expect(chip.Locator("sac-icon[name='fb-gift']")).ToHaveCountAsync(1);
            await Assertions.Expect(view.Locator(".cv-agenda-item.birthday")).ToContainTextAsync("Grace Hopper");

            // The toolbar: import (a writer), export, links.
            var toolbar = page.Locator("#fb-view-toolbar");
            await Assertions.Expect(toolbar.Locator("button[title='Import an .ics file']")).ToBeVisibleAsync();
            await Assertions.Expect(toolbar.Locator("button[title='Export as an .ics file']")).ToBeVisibleAsync();

            // Import through the file picker: one event, then it shows.
            var ics = string.Format(Ics, day.ToString("yyyyMMdd"));
            var chooser = await page.RunAndWaitForFileChooserAsync(() => toolbar.Locator("button[title='Import an .ics file']").ClickAsync());
            await chooser.SetFilesAsync(new FilePayload { Name = "dentist.ics", MimeType = "text/calendar", Buffer = System.Text.Encoding.UTF8.GetBytes(ics) });
            await Assertions.Expect(view.Locator(".cv-chip", new() { HasText = "Imported dentist" })).ToHaveCountAsync(1, new() { Timeout = 10000 });

            // Export carries both the stored event and none of the birthdays.
            var export = await (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/events/export")).TextAsync();
            Assert.Contains("SUMMARY:Imported dentist", export);
            Assert.DoesNotContain("Grace", export);

            // A subscription link: made, read without a session, revoked.
            await toolbar.Locator("button[title='Subscription links']").ClickAsync();
            var dlg = page.Locator("#cv-feeds-dialog");
            await dlg.Locator("#cv-feed-new").ClickAsync();
            var url = await dlg.Locator(".cv-feed-url").InputValueAsync();
            Assert.Contains("/feeds/fbcal_", url);
            var anon = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
            var feed = await anon.APIRequest.GetAsync(url);
            Assert.Equal(200, feed.Status);
            Assert.Contains("SUMMARY:Imported dentist", await feed.TextAsync());
            // One link in the list; revoking it also takes its URL off the screen.
            var revoke = dlg.Locator(".cv-feed-row button[data-id]");
            await Assertions.Expect(revoke).ToHaveCountAsync(1);
            await revoke.ClickAsync();
            await Assertions.Expect(revoke).ToHaveCountAsync(0);
            await Assertions.Expect(dlg.Locator(".cv-feed-url")).ToHaveCountAsync(0);
            Assert.Equal(404, (await anon.APIRequest.GetAsync(url)).Status);
            await anon.CloseAsync();
            await dlg.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
            await Assertions.Expect(dlg).ToHaveCountAsync(0, new() { Timeout = 5000 });

            // A birthday shows the person in brief; Go to contact opens the contact.
            await chip.ClickAsync();
            var card = page.Locator("sac-dialog#cv-bday-dialog[title='Grace Hopper']");
            await Assertions.Expect(card.Locator(".cv-bday-line.lead")).ToContainTextAsync("30");
            await Assertions.Expect(card.Locator("a[href='mailto:grace@navy.test']")).ToHaveCountAsync(1);
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "calendar-birthday-card.png") });
            Assert.EndsWith($"#/space/{slug}/calendar", page.Url);
            await card.GetByRole(AriaRole.Button, new() { Name = "Go to contact" }).ClickAsync();
            await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex($"#/space/{slug}/contacts$"));
            await Assertions.Expect(page.Locator("fb-contacts-view input[data-key='lastName']")).ToHaveValueAsync("Hopper", new() { Timeout = 10000 });
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
