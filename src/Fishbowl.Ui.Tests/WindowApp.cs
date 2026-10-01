using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The window apps (fb.windowApps: Messages, Trash, Spaces, Apps, API keys,
// Secrets, Users, System) have no address: a test opens one from the
// desktop the way its tile does.
public static class WindowApp
{
    public static async Task<ILocator> OpenAsync(IPage page, string baseUrl, string name, string hash = "#/", string? tab = null)
    {
        await page.GotoAsync(baseUrl + "/" + hash);
        // No WaitForFunctionAsync: not every context bypasses the page CSP.
        await Assertions.Expect(page.Locator("fb-hub-view")).ToBeAttachedAsync(new() { Timeout = 10000 });
        await page.EvaluateAsync("([n, t]) => fb.windowApps.open(n, { tab: t || undefined })", new object?[] { name, tab });
        var win = page.Locator($"#fb-win-{name}");
        await Assertions.Expect(win).ToHaveAttributeAsync("open", "", new() { Timeout = 5000 });
        return win;
    }

    // Every open window closed, so the desktop under them takes clicks again.
    public static Task CloseAllAsync(IPage page) =>
        page.EvaluateAsync("() => document.querySelectorAll('sac-window[open]').forEach(w => w.close())");
}
