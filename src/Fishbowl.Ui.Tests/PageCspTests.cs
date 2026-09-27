using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The page CSP (PageCsp): every view loads under it without a violation, and
// script that slips into the page as markup doesn't run.
[Collection(UiCollection.Name)]
public class PageCspTests
{
    private readonly PlaywrightFixture _fixture;

    public PageCspTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Views_LoadUnderTheCsp_AndInjectedScriptNeverRuns_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        try
        {
            var page = await context.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, e) => errors.Add(e);
            page.Console += (_, m) => { if (m.Type == "error" && !m.Text.StartsWith("Failed to load resource")) errors.Add(m.Text); };

            var response = await page.GotoAsync(_fixture.BaseUrl + "/#/");
            Assert.Contains("script-src 'self'", await response!.HeaderValueAsync("content-security-policy") ?? "");

            foreach (var view in new[] { "notes", "todos", "calendar", "files", "messages", "apps", "admin/system" })
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/#/{view}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }

            // What a sanitiser bug would let through: an inline handler and a
            // script element built from markup. Neither may run.
            await page.EvaluateAsync(@"() => {
                window.__cspHit = false;
                const box = document.createElement('div');
                box.innerHTML = '<img src=""data:,x"" onerror=""window.__cspHit = true"">';
                document.body.append(box);
                const s = document.createElement('script');
                s.textContent = 'window.__cspHit = true';
                document.body.append(s);
            }");
            await page.WaitForTimeoutAsync(300);
            Assert.False(await page.EvaluateAsync<bool>("() => window.__cspHit"));

            // The two refusals above are the only violations the page may log.
            var violations = errors.Where(e => e.Contains("Content Security Policy")).ToList();
            Assert.Equal(2, violations.Count);
            Assert.Empty(errors.Except(violations));
        }
        finally { await context.CloseAsync(); }
    }

    [Fact]
    public async Task Login_RunsItsScriptUnderTheCsp_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        try
        {
            var page = await context.NewPageAsync();
            var errors = new List<string>();
            page.Console += (_, m) => { if (m.Type == "error" && m.Text.Contains("Content Security Policy")) errors.Add(m.Text); };
            await page.GotoAsync(_fixture.BaseUrl + "/login");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            Assert.Empty(errors);
        }
        finally { await context.CloseAsync(); }
    }
}
