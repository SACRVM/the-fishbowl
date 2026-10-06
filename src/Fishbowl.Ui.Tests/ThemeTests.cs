using Microsoft.Playwright;
using System.Text.RegularExpressions;

namespace Fishbowl.Ui.Tests;

// Light / dark: three items in the account menu (never a switch in the nav),
// the current one checked. The choice is the kit's (sac.apps.theme:
// localStorage "sac-theme", <html data-theme>), and js/lib/theme.js puts it on
// <html> before the first paint — on the SPA and the server-rendered pages alike.
[Collection(UiCollection.Name)]
public class ThemeTests
{
    private readonly PlaywrightFixture _fixture;

    public ThemeTests(PlaywrightFixture fixture) => _fixture = fixture;

    private static async Task PickAsync(IPage page, string mode)
    {
        await page.Locator("#fb-account [slot='trigger']").ClickAsync();
        await page.Locator($"#fb-account button[data-action='theme:{mode}']").ClickAsync();
    }

    private static ILocator Icon(IPage page, string mode) =>
        page.Locator($"#fb-account button[data-action='theme:{mode}'] sac-icon");

    [Fact]
    public async Task Theme_InTheAccountMenu_KeptAcrossReloads_AndOnLogin_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new()
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        });
        try
        {
            var page = await context.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, e) => errors.Add(e);
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            var html = page.Locator("html");
            await Assertions.Expect(page.Locator("#fb-account")).ToBeVisibleAsync(new() { Timeout = 15000 });

            // No switch in the nav — the account menu holds it.
            await Assertions.Expect(page.Locator("#fb-nav sac-theme-toggle")).ToHaveCountAsync(0);
            // Dark is the kit's ground: no attribute, Dark checked.
            await Assertions.Expect(html).Not.ToHaveAttributeAsync("data-theme", new Regex(".*"));
            await Assertions.Expect(Icon(page, "dark")).ToHaveAttributeAsync("name", "check");
            await Assertions.Expect(Icon(page, "light")).ToHaveAttributeAsync("name", "fb-theme-light");

            await PickAsync(page, "light");
            await Assertions.Expect(html).ToHaveAttributeAsync("data-theme", "light");
            await Assertions.Expect(Icon(page, "light")).ToHaveAttributeAsync("name", "check");
            await Assertions.Expect(Icon(page, "dark")).ToHaveAttributeAsync("name", "fb-theme-dark");
            var bg = await page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor");

            // A reload: on <html> before any deferred script ran (theme.js in <head>).
            await page.AddInitScriptAsync(@"document.addEventListener('readystatechange', () => {
                if (document.readyState === 'interactive') window.__themeAtParse = document.documentElement.getAttribute('data-theme');
            });");
            await page.ReloadAsync();
            await Assertions.Expect(html).ToHaveAttributeAsync("data-theme", "light");
            Assert.Equal("light", await page.EvaluateAsync<string?>("() => window.__themeAtParse"));
            await Assertions.Expect(Icon(page, "light")).ToHaveAttributeAsync("name", "check");

            // The server-rendered pages follow it.
            await page.GotoAsync(_fixture.BaseUrl + "/login");
            await Assertions.Expect(html).ToHaveAttributeAsync("data-theme", "light");
            Assert.Equal(bg, await page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor"));

            // Like the system, then back to dark: the attribute and the stored choice go.
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await PickAsync(page, "auto");
            await Assertions.Expect(html).ToHaveAttributeAsync("data-theme", "auto");
            await PickAsync(page, "dark");
            await Assertions.Expect(html).Not.ToHaveAttributeAsync("data-theme", new Regex(".*"));
            Assert.Null(await page.EvaluateAsync<string?>("() => localStorage.getItem('sac-theme')"));
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
