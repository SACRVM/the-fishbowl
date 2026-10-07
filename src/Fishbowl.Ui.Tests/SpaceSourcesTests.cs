using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// Spaces as sources (docs/superpowers/specs/2026-10-07-space-sources-design.md):
// the personal Calendar shows the spaces switched on in its toolbar menu —
// kept per user across a reload — each in its colour (grey without one), with
// their contacts' birthdays; a space's event is read-only here and opens in
// its space; with Personal off there is nothing to create.
[Collection(UiCollection.Name)]
public class SpaceSourcesTests
{
    private readonly PlaywrightFixture _fixture;

    public SpaceSourcesTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PersonalCalendar_ShowsSpacesSwitchedOn_ReadOnly_Test()
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
        var slugs = new List<string>();
        var tag = Guid.NewGuid().ToString("N")[..6];
        try
        {
            async Task<(string Id, string Slug)> Space(string name)
            {
                var r = (await (await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name } })).JsonAsync())!.Value;
                slugs.Add(r.GetProperty("slug").GetString()!);
                return (r.GetProperty("id").GetString()!, r.GetProperty("slug").GetString()!);
            }
            var club = await Space("Club " + tag);
            var plain = await Space("Plain " + tag);
            await page.APIRequest.PatchAsync($"{_fixture.BaseUrl}/api/v1/spaces/{club.Slug}", new() { DataObject = new { color = "green" } });
            var month = DateTime.Today;
            string At(int day) => new DateTime(month.Year, month.Month, day, 10, 0, 0, DateTimeKind.Utc).ToString("o");
            await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{club.Slug}/events", new() { DataObject = new { title = $"Club night {tag}", startAt = At(10) } });
            await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{plain.Slug}/events", new() { DataObject = new { title = $"Plain day {tag}", startAt = At(12) } });
            await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/events", new() { DataObject = new { title = $"Mine {tag}", startAt = At(14) } });
            await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{club.Slug}/contacts",
                new() { DataObject = new { kind = "person", firstName = "Ada", lastName = tag, birthday = $"1990-{month.Month:00}-16" } });

            await page.GotoAsync($"{_fixture.BaseUrl}/#/calendar");
            var view = page.Locator("fb-calendar-view");
            ILocator Chip(string text) => view.Locator(".cv-chip", new() { HasText = text });
            await Assertions.Expect(Chip($"Mine {tag}")).ToHaveCountAsync(1, new() { Timeout = 15000 });
            await Assertions.Expect(Chip($"Club night {tag}")).ToHaveCountAsync(0);

            var menu = page.Locator("#fb-view-toolbar sac-menu#cv-sources");
            async Task Toggle(string action)
            {
                await menu.Locator("button[slot='trigger']").ClickAsync();
                await menu.Locator($"button[data-action='{action}']").ClickAsync();
            }

            // Club on: its event and its birthday, in its colour; Plain on: grey.
            await Toggle(club.Id);
            await Assertions.Expect(Chip($"Club night {tag}")).ToHaveAttributeAsync("style", new Regex("--accent: var\\(--palette-green\\)"), new() { Timeout = 10000 });
            await Assertions.Expect(Chip($"Ada {tag}")).ToHaveCountAsync(1);
            await Toggle(plain.Id);
            await Assertions.Expect(Chip($"Plain day {tag}")).ToHaveAttributeAsync("style", new Regex("--accent: var\\(--text-muted\\)"), new() { Timeout = 10000 });
            await Assertions.Expect(menu.Locator($"button[data-action='{club.Id}'] .fb-context-check")).ToHaveCountAsync(1);

            // Kept on the server: a reload shows the same.
            await page.ReloadAsync();
            await Assertions.Expect(Chip($"Club night {tag}")).ToHaveCountAsync(1, new() { Timeout = 15000 });
            await Assertions.Expect(Chip($"Plain day {tag}")).ToHaveCountAsync(1);

            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "calendar-sources-grid.png") });

            // A space's event is read-only here: a card, and Open in Club.
            await Chip($"Club night {tag}").ClickAsync();
            var card = page.Locator("sac-dialog#cv-source-dialog");
            await Assertions.Expect(card).ToContainTextAsync($"Club {tag}");
            await Assertions.Expect(view.Locator("#cv-editor-wrap")).ToBeHiddenAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "calendar-sources.png") });
            await card.GetByRole(AriaRole.Button, new() { Name = $"Open in Club {tag}" }).ClickAsync();
            await page.WaitForURLAsync(new Regex($"#/space/{club.Slug}/calendar$"));
            await Assertions.Expect(page.Locator("fb-calendar-view #cv-title")).ToHaveValueAsync($"Club night {tag}", new() { Timeout = 10000 });

            // Back in personal: Personal off — its events go, and so does "+".
            await page.GotoAsync($"{_fixture.BaseUrl}/#/calendar");
            await Assertions.Expect(Chip($"Mine {tag}")).ToHaveCountAsync(1, new() { Timeout = 15000 });
            await Toggle("personal");
            await Assertions.Expect(Chip($"Mine {tag}")).ToHaveCountAsync(0, new() { Timeout = 10000 });
            await Assertions.Expect(view.Locator("#cv-new-btn")).ToBeHiddenAsync();
            await Assertions.Expect(Chip($"Club night {tag}")).ToHaveCountAsync(1);
            Assert.Empty(errors);
        }
        finally
        {
            // Every UI test shares this user's personal calendar: put it back.
            await page.APIRequest.PutAsync($"{_fixture.BaseUrl}/api/v1/me/sources", new() { DataObject = new { app = "calendar", source = "personal", on = true } });
            foreach (var slug in slugs) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }
}
