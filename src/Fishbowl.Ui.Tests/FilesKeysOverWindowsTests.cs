using System.Text;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The Files keys are page-wide: one pressed in a window over the view
// (here the Files trash) is not for the panes. And Undo after a Delete
// restores in the workspace the files were deleted in, even when the pane
// has moved to another one since.
[Collection(UiCollection.Name)]
public class FilesKeysOverWindowsTests
{
    private readonly PlaywrightFixture _fixture;

    public FilesKeysOverWindowsTests(PlaywrightFixture fixture) => _fixture = fixture;

    private async Task<(IBrowserContext Context, IPage Page, List<string> Errors)> OpenAsync()
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
        return (context, page, errors);
    }

    private async Task PutAsync(IPage page, string path, string content)
    {
        var res = await page.APIRequest.PutAsync($"{_fixture.BaseUrl}/api/v1/files/content?path={Uri.EscapeDataString(path)}&parents=1",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["X-Fishbowl-Upload"] = "1", ["If-None-Match"] = "*" },
                DataByte = Encoding.UTF8.GetBytes(content),
            });
        Assert.True(res.Ok, $"PUT {path}: {res.Status} {await res.TextAsync()}");
    }

    private async Task<bool> ExistsAsync(IPage page, string path) =>
        (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/files/stat?path={Uri.EscapeDataString(path)}")).Ok;

    private static ILocator Pane(IPage page, string side) => page.Locator($"fb-files-view .fv-pane[data-side='{side}']");
    private static ILocator Row(IPage page, string side, string name) =>
        Pane(page, side).Locator("sac-file-browser").Locator(".canvas > .row").Filter(new() { HasText = name });

    [Fact]
    public async Task Files_DeleteKeyInTrashWindow_LeavesThePanesAlone_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var folder = "over-" + Guid.NewGuid().ToString("N")[..6];
            await PutAsync(page, $"{folder}/a.txt", "a");
            await PutAsync(page, $"{folder}/b.txt", "b");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/files/{folder}");
            await Assertions.Expect(Row(page, "left", "b.txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            await Row(page, "left", "a.txt").ClickAsync();
            await page.Keyboard.PressAsync("Delete");
            await Assertions.Expect(Row(page, "left", "a.txt")).ToHaveCountAsync(0, new() { Timeout = 10000 });
            await Row(page, "left", "b.txt").ClickAsync();

            // Delete on the trash window's Restore button: nothing happens to b.txt.
            await page.Locator("#fb-view-toolbar button[title='Trash']").ClickAsync();
            var restore = page.Locator("sac-window .fv-trash-row").Filter(new() { HasText = $"{folder}/a.txt" })
                .GetByRole(AriaRole.Button, new() { Name = "Restore" });
            await restore.FocusAsync();
            await page.Keyboard.PressAsync("Delete");
            await restore.ClickAsync();
            await Assertions.Expect(Row(page, "left", "a.txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            Assert.True(await ExistsAsync(page, $"{folder}/b.txt"));
            await Assertions.Expect(Row(page, "left", "b.txt")).ToHaveCountAsync(1);
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Files_UndoAfterThePaneMovedOn_RestoresWhereItWasDeleted_Test()
    {
        var (context, page, errors) = await OpenAsync();
        string? slug = null;
        try
        {
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Undo " + Guid.NewGuid().ToString("N")[..6] } });
            slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString()!;
            var folder = "undo-ws-" + Guid.NewGuid().ToString("N")[..6];
            await PutAsync(page, $"{folder}/a.txt", "a");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/files/{folder}");
            await Assertions.Expect(Row(page, "left", "a.txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            await Row(page, "left", "a.txt").ClickAsync();
            await page.Keyboard.PressAsync("Delete");
            await Assertions.Expect(Row(page, "left", "a.txt")).ToHaveCountAsync(0, new() { Timeout = 10000 });

            // Alt+1: the left pane goes to the space; then Undo.
            await page.Keyboard.PressAsync("Alt+1");
            await Pane(page, "left").Locator($"sac-menu button[data-action='space:{slug}']").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"#/space/{slug}/files"), new() { Timeout = 10000 });
            await page.Locator("#sac-toast-stack .toast").Filter(new() { HasText = "Moved 1 item to the trash." })
                .Locator("button.action").ClickAsync(new() { Timeout = 10000 });
            await Assertions.Expect(page.Locator("#sac-toast-stack").GetByText("Restored 1 item.").First).ToBeVisibleAsync(new() { Timeout = 10000 });
            Assert.True(await ExistsAsync(page, $"{folder}/a.txt"));
            Assert.Empty(errors);
        }
        finally
        {
            if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }
}
