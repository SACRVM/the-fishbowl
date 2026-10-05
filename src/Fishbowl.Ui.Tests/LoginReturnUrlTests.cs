using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// /login's way back after signing in (login.js RETURN): only a path on this
// Fishbowl. A control character or whitespace a browser would drop
// ("/\t/evil.example" → "//evil.example") sends it home instead.
[Collection(UiCollection.Name)]
public class LoginReturnUrlTests
{
    private readonly PlaywrightFixture _fixture;

    public LoginReturnUrlTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("%2F%09%2Fevil.example", "/login/challenge/google")]
    [InlineData("%2F%0D%0A%2Fevil.example", "/login/challenge/google")]
    [InlineData("%2F%E2%80%A8%2Fevil.example", "/login/challenge/google")]
    [InlineData("%2F%5Cevil.example", "/login/challenge/google")]
    [InlineData("https%3A%2F%2Fevil.example%2F", "/login/challenge/google")]
    [InlineData("%2Foauth%2Fauthorize%3Fclient_id%3Dfbc_1", "/login/challenge/google?returnUrl=%2Foauth%2Fauthorize%3Fclient_id%3Dfbc_1")]
    public async Task TheGoogleButton_GoesBackOnlyToAPathHere(string returnUrl, string expectedHref)
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync($"{_fixture.BaseUrl}/login?returnUrl={returnUrl}");
            var google = page.Locator("a.btn[href^='/login/challenge/google']");
            await google.WaitForAsync();
            Assert.Equal(expectedHref, await google.GetAttributeAsync("href"));
        }
        finally { await context.CloseAsync(); }
    }
}
