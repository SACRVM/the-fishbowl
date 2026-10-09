using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// Dialogs and windows on the keys (kit 2.31): a dialog opens with a button
// focused, ←/→ walk its buttons and Enter presses one; a window takes the
// focus, Escape closes it and the focus goes back to its opener.
[Collection(UiCollection.Name)]
public class DialogKeyboardTests
{
    private readonly PlaywrightFixture _fixture;

    public DialogKeyboardTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Dialog_ArrowsWalkTheButtons_EnterPresses_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        string? slug = null;
        try
        {
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Dialog keys " + Guid.NewGuid().ToString("N")[..6] } });
            slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString()!;
            await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/notes", new() { DataObject = new { title = "Arrow note", content = "x" } });
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/notes");
            var rows = page.Locator("fb-notes-view .nv-item");
            await Assertions.Expect(rows).ToHaveCountAsync(1, new() { Timeout = 15000 });
            await rows.First.ClickAsync();

            // Cancel · Archive (primary) · Delete (arms after 2 s): the
            // primary has the focus first.
            await page.Keyboard.PressAsync("Delete");
            await Assertions.Expect(page.Locator("sac-dialog[title='Delete this note?']")).ToBeVisibleAsync();
            await Dialogs.ExpectFocusedAsync(page, "Archive");
            await page.Keyboard.PressAsync("ArrowRight");
            await Dialogs.ExpectFocusedAsync(page, "Delete");
            await page.Keyboard.PressAsync("ArrowRight");
            await Dialogs.ExpectFocusedAsync(page, "Cancel");   // wraps

            // A key cancelled the arming: Delete never takes the focus back.
            await page.WaitForTimeoutAsync(2500);
            Assert.Equal("Cancel", await Dialogs.FocusedAsync(page));

            await page.Keyboard.PressAsync("ArrowLeft");
            await Dialogs.ExpectFocusedAsync(page, "Delete");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(rows).ToHaveCountAsync(0, new() { Timeout = 5000 });
        }
        finally
        {
            if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Window_TakesTheFocus_EscapeCloses_FocusGoesBack_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/");
            var bell = page.Locator("#fb-messages-btn");
            await Assertions.Expect(bell).ToBeVisibleAsync(new() { Timeout = 15000 });
            await bell.ClickAsync();

            var win = page.Locator("#fb-win-messages");
            await Assertions.Expect(win).ToBeVisibleAsync(new() { Timeout = 5000 });
            await Poll.UntilAsync(() => page.EvaluateAsync<bool>(
                "() => { const w = document.getElementById('fb-win-messages'); const a = document.activeElement; return a === w || w.contains(a); }"));

            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(win).ToBeHiddenAsync(new() { Timeout = 5000 });
            await Poll.UntilAsync(async () => await page.EvaluateAsync<string?>("() => document.activeElement?.id") == "fb-messages-btn");
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}

/// <summary>The open dialog's focused button, by its label.</summary>
internal static class Dialogs
{
    public static Task<string?> FocusedAsync(IPage page) => page.EvaluateAsync<string?>(
        "() => { const d = [...document.querySelectorAll('sac-dialog[open]')].pop(); return d?.shadowRoot?.activeElement?.textContent?.trim() ?? null; }");

    public static async Task ExpectFocusedAsync(IPage page, string label)
    {
        string? now = null;
        for (var i = 0; i < 30; i++)
        {
            now = await FocusedAsync(page);
            if (now == label) return;
            await page.WaitForTimeoutAsync(100);
        }
        Assert.Fail($"Expected the dialog's \"{label}\" button to have the focus, found \"{now}\".");
    }
}

internal static class Poll
{
    public static async Task UntilAsync(Func<Task<bool>> check, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await check())
        {
            if (DateTime.UtcNow > until) Assert.Fail("Condition not met in time.");
            await Task.Delay(100);
        }
    }
}
