using System.Text.Json;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The way in (space-apps spec, phase 7): the OAuth consent page a connector
// like claude.ai sends you to, and "Connect an agent" on the API keys page.
[Collection(UiCollection.Name)]
public class OAuthTests
{
    private readonly PlaywrightFixture _fixture;

    public OAuthTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task OAuth_Consent_AllowSendsACodeBack_KeysGuideShowsTheCommand_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1280, Height = 860 },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            const string Callback = "https://claude.ai/api/mcp/auth_callback";
            var reg = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/oauth/register",
                new() { DataObject = new { client_name = "Claude", redirect_uris = new[] { Callback } } });
            var clientId = (await reg.JsonAsync())!.Value.GetProperty("client_id").GetString();
            // The app's side of the redirect: answer it here instead of leaving.
            await page.RouteAsync("https://claude.ai/**", r => r.FulfillAsync(new() { Status = 200, Body = "ok", ContentType = "text/plain" }));

            var challenge = new string('a', 43);
            await page.GotoAsync($"{_fixture.BaseUrl}/oauth/authorize?response_type=code&client_id={clientId}" +
                $"&redirect_uri={Uri.EscapeDataString(Callback)}&code_challenge={challenge}&code_challenge_method=S256&state=s1");
            await Assertions.Expect(page.Locator("#oauth-ask")).ToHaveTextAsync("Claude wants to use your Fishbowl.", new() { Timeout = 10000 });
            await Assertions.Expect(page.Locator("#oauth-build")).ToBeHiddenAsync();       // personal: nothing to build
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "oauth-consent.png") });
            await page.Locator("input[value=read]").CheckAsync();
            await page.Locator("#oauth-allow").ClickAsync();
            await page.WaitForURLAsync(u => u.StartsWith(Callback), new() { Timeout = 10000 });
            var back = new Uri(page.Url);
            Assert.Contains("code=", back.Query);
            Assert.Contains("state=s1", back.Query);

            // Connect an agent → Claude Code: a key and the command, once.
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "keys");
            var guide = page.Locator("#agent-guide");
            await Assertions.Expect(guide).ToContainTextAsync("Add custom connector");
            await guide.Locator("#guide-client").SelectOptionAsync("code");
            await guide.Locator("#guide-create").ClickAsync();
            var shown = page.Locator("sac-dialog .fb-token-block");
            await Assertions.Expect(shown).ToContainTextAsync("claude mcp add --transport http fishbowl");
            await Assertions.Expect(shown).ToContainTextAsync("Authorization: Bearer fb_live_");
            Assert.Empty(errors);
        }
        finally { await context.CloseAsync(); }
    }
}
