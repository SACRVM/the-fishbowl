using System.IO.Compression;
using System.Text;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// Files phase 3 in the browser: deleting a space with "Archive before
// deleting" (checked by default) lists it under Archived spaces, Restore
// brings it back, and Settings → Your data downloads the export ZIPs.
[Collection(UiCollection.Name)]
public class DataLifecycleTests
{
    private readonly PlaywrightFixture _fixture;

    public DataLifecycleTests(PlaywrightFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SpaceDelete_ArchivesByDefault_ThenRestore_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1200, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            var name = "Archive me " + Guid.NewGuid().ToString("N")[..6];
            var created = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces",
                new APIRequestContextOptions { DataObject = new { name } });
            var slug = (await created.JsonAsync())!.Value.GetProperty("slug").GetString()!;
            await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/notes",
                new APIRequestContextOptions { DataObject = new { title = "survives the archive" } });

            await page.GotoAsync(_fixture.BaseUrl + "/#/spaces");
            var row = page.Locator($"fb-spaces-settings-view #space-list .space-row[data-slug='{slug}']");
            await row.Locator(".delete-btn").ClickAsync();

            var dialog = page.Locator("sac-dialog[open]");
            await Assertions.Expect(dialog.Locator("input[name=archive]")).ToBeCheckedAsync(new() { Timeout = 5000 });
            await Assertions.Expect(dialog).ToContainTextAsync("Archived spaces");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();

            await Assertions.Expect(row).ToHaveCountAsync(0, new() { Timeout = 10000 });
            var archived = page.Locator("fb-spaces-settings-view .archive-row").Filter(new() { HasText = name });
            await Assertions.Expect(archived).ToHaveCountAsync(1, new() { Timeout = 10000 });
            await Assertions.Expect(archived).ToContainTextAsync("Deleted in 30 days");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_archived_spaces.png"), FullPage = true });

            // The ZIP downloads from the row.
            var download = await page.RunAndWaitForDownloadAsync(() => archived.GetByRole(AriaRole.Link, new() { Name = "Download" }).ClickAsync());
            Assert.EndsWith(".zip", download.SuggestedFilename);

            await archived.GetByRole(AriaRole.Button, new() { Name = "Restore" }).ClickAsync();
            var back = page.Locator("fb-spaces-settings-view #space-list .space-row").Filter(new() { HasText = name });
            await Assertions.Expect(back).ToHaveCountAsync(1, new() { Timeout = 10000 });
            var restoredSlug = await back.GetAttributeAsync("data-slug");
            var notes = await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/spaces/{restoredSlug}/notes");
            Assert.Contains("survives the archive", await notes.TextAsync());

            // Tidy up: the restored space goes without an archive, the archive too.
            await page.EvaluateAsync("async (s) => fb.api.spaces.delete(s, { archive: false })", restoredSlug);
            var list = await (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/archive/spaces")).JsonAsync();
            foreach (var a in list!.Value.EnumerateArray())
                if (a.GetProperty("name").GetString() == name)
                    await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/archive/spaces/{a.GetProperty("id").GetString()}");
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task YourData_ExportsDownload_AndStorageShows_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1200, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            var path = "export-" + Guid.NewGuid().ToString("N")[..6] + "/hello.txt";
            var put = await page.APIRequest.PutAsync($"{_fixture.BaseUrl}/api/v1/files/content?path={Uri.EscapeDataString(path)}&parents=1",
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string> { ["X-Fishbowl-Upload"] = "1", ["If-None-Match"] = "*" },
                    DataByte = Encoding.UTF8.GetBytes("export me"),
                });
            Assert.True(put.Ok);

            await page.GotoAsync(_fixture.BaseUrl + "/#/data");
            var view = page.Locator("fb-data-settings-view");
            await Assertions.Expect(view.Locator(".export-row")).ToHaveCountAsync(3, new() { Timeout = 10000 });
            await Assertions.Expect(view.Locator(".storage")).ToContainTextAsync("You use");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_your_data.png"), FullPage = true });

            var download = await page.RunAndWaitForDownloadAsync(() =>
                view.Locator(".export-row[data-kind='files'] a").ClickAsync());
            Assert.EndsWith("-files.zip", download.SuggestedFilename);
            var saved = Path.Combine(Path.GetTempPath(), "fishbowl_export_" + Guid.NewGuid().ToString("N")[..6] + ".zip");
            await download.SaveAsAsync(saved);
            try
            {
                using var zip = ZipFile.OpenRead(saved);
                Assert.NotNull(zip.GetEntry(path));
            }
            finally
            {
                File.Delete(saved);
            }
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
