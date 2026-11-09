using ImageMagick;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The Photos app (spec 2026-10-30-photos-design): uploads filed by the day
// they were taken, the timeline by month with previews, the viewer walking
// it, favourites, albums from marked photos, deleting to the Files trash,
// and the grid without a mouse.
[Collection(UiCollection.Name)]
public class PhotosTests
{
    private readonly PlaywrightFixture _fixture;

    public PhotosTests(PlaywrightFixture fixture) => _fixture = fixture;

    private static byte[] Jpeg(string taken, string color)
    {
        using var image = new MagickImage(new MagickColor(color), 640, 480);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Apple");
        exif.SetValue(ExifTag.Model, "iPhone 15");
        exif.SetValue(ExifTag.DateTimeOriginal, taken);
        exif.SetValue(ExifTag.OffsetTimeOriginal, "+02:00");
        image.SetProfile(exif);
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    private async Task<(IBrowserContext Context, IPage Page, List<string> Errors, string Slug)> SpaceWithPhotosAsync(int width = 1400, int height = 900)
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = width, Height = height },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces",
            new() { DataObject = new { name = "Photos " + Guid.NewGuid().ToString("N")[..6] } });
        var slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString()!;
        foreach (var (name, taken, color) in new[]
                 {
                     ("IMG_0001.JPG", "2024:07:01 18:30:05", "SteelBlue"),
                     ("IMG_0002.JPG", "2024:07:03 09:10:00", "Orange"),
                     ("IMG_0003.JPG", "2023:12:24 20:00:00", "SeaGreen"),
                 })
        {
            var up = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/photos/upload?name={name}",
                new() { DataByte = Jpeg(taken, color), Headers = new Dictionary<string, string> { ["Content-Type"] = "image/jpeg" } });
            Assert.True(up.Ok, $"upload {name}: {up.Status} {await up.TextAsync()}");
        }
        return (context, page, errors, slug);
    }

    [Fact]
    public async Task Photos_TimelineViewerFavouriteAlbumAndDelete_Test()
    {
        var (context, page, errors, slug) = await SpaceWithPhotosAsync();
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/photos");
            var view = page.Locator("fb-photos-view");
            var cells = view.Locator(".pv-cell[data-id]");
            await Assertions.Expect(cells).ToHaveCountAsync(3, new() { Timeout = 15000 });
            // By month, newest first; each with its previews.
            var months = await view.Locator(".pv-month").AllInnerTextsAsync();
            Assert.Equal(2, months.Count);
            Assert.StartsWith("July 2024", months[0]);
            Assert.StartsWith("December 2023", months[1]);
            await Assertions.Expect(view.Locator("#pv-sub")).ToContainTextAsync("3 items");
            await page.WaitForFunctionAsync(@"() => [...document.querySelectorAll('fb-photos-view .pv-cell img')]
                .every(i => i.complete && i.naturalWidth > 0) && document.querySelectorAll('fb-photos-view .pv-cell img').length === 3");
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "photos-timeline.png") });

            // The newest opens in the viewer; → walks on, F favourites it.
            await cells.First.ClickAsync();
            var viewer = page.Locator("sac-window.fb-photo-viewer");
            await Assertions.Expect(viewer).ToBeVisibleAsync();
            await Assertions.Expect(viewer.Locator("#pvw-info")).ToContainTextAsync("Apple iPhone 15", new() { Timeout = 10000 });
            await Assertions.Expect(viewer.Locator("#pvw-info")).ToContainTextAsync("Photos/2024/07/IMG_0002.JPG");
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "photos-viewer.png") });
            await page.Keyboard.PressAsync("ArrowRight");
            await Assertions.Expect(viewer.Locator("#pvw-info")).ToContainTextAsync("IMG_0001.JPG", new() { Timeout = 10000 });
            await page.Keyboard.PressAsync("f");
            await Assertions.Expect(viewer.Locator("[data-act='fav']")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("\\bon\\b"));
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(viewer).ToHaveCountAsync(0);
            await Assertions.Expect(view.Locator(".pv-cell[data-index='1'] sac-icon[name='fb-heart-fill']")).ToHaveCountAsync(1);

            await view.Locator(".pv-sections [data-view='favorites']").ClickAsync();
            await Assertions.Expect(cells).ToHaveCountAsync(1, new() { Timeout = 10000 });
            await view.Locator(".pv-sections [data-view='library']").ClickAsync();
            await Assertions.Expect(cells).ToHaveCountAsync(3, new() { Timeout = 10000 });

            // Two marked → a new album with them.
            var mod = OperatingSystem.IsMacOS() ? KeyboardModifier.Meta : KeyboardModifier.Control;
            await cells.Nth(0).ClickAsync(new() { Modifiers = [mod] });
            await cells.Nth(2).ClickAsync(new() { Modifiers = [mod] });
            await Assertions.Expect(view.Locator("#pv-marked-count")).ToHaveTextAsync("2 selected");
            await view.Locator("#pv-mark-album .icon-btn").ClickAsync();
            await view.Locator("#pv-mark-album button[data-action='new']").ClickAsync();
            var prompt = page.Locator("sac-dialog");
            await Assertions.Expect(prompt).ToBeVisibleAsync();
            await page.Keyboard.TypeAsync("Summer");
            await page.Keyboard.PressAsync("Enter");
            var album = view.Locator("#pv-albums .pv-row", new() { HasText = "Summer" });
            await Assertions.Expect(album).ToContainTextAsync("2", new() { Timeout = 10000 });
            await Assertions.Expect(view.Locator("#pv-marktools")).ToBeHiddenAsync();
            await album.ClickAsync();
            await Assertions.Expect(view.Locator("#pv-title")).ToHaveTextAsync("Summer");
            await Assertions.Expect(cells).ToHaveCountAsync(2, new() { Timeout = 10000 });

            // Without a mouse: the grid takes the keys; Delete asks, then the
            // files go to the Files trash.
            await view.Locator(".pv-sections [data-view='library']").ClickAsync();
            await Assertions.Expect(cells).ToHaveCountAsync(3, new() { Timeout = 10000 });
            await view.Locator("#pv-scroller").FocusAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            await Assertions.Expect(view.Locator(".pv-cell.cursor")).ToHaveAttributeAsync("data-index", "0");
            await page.Keyboard.PressAsync("ArrowRight");
            await Assertions.Expect(view.Locator(".pv-cell.cursor")).ToHaveAttributeAsync("data-index", "1");
            await page.Keyboard.PressAsync("Delete");
            var confirm = page.Locator("sac-dialog");
            await Assertions.Expect(confirm).ToContainTextAsync("Delete this photo?");
            await confirm.Locator("button", new() { HasText = "Delete" }).ClickAsync();
            await Assertions.Expect(cells).ToHaveCountAsync(2, new() { Timeout = 10000 });
            var trash = await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/files/trash");
            Assert.Contains("Photos/2024/07/IMG_0001.JPG", await trash.TextAsync());

            Assert.Empty(errors);
        }
        finally { await context.CloseAsync(); }
    }

    [Fact]
    public async Task Photos_UploadFromTheBar_FilesItByTheDayItWasTaken_Test()
    {
        var (context, page, errors, slug) = await SpaceWithPhotosAsync();
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/photos");
            var view = page.Locator("fb-photos-view");
            await Assertions.Expect(view.Locator(".pv-cell[data-id]")).ToHaveCountAsync(3, new() { Timeout = 15000 });

            var dir = Path.Combine(Path.GetTempPath(), "fb-photos-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "Party.jpg");
            await File.WriteAllBytesAsync(file, Jpeg("2022:05:14 21:45:00", "Crimson"), TestContext.Current.CancellationToken);
            var chooser = await page.RunAndWaitForFileChooserAsync(() => view.Locator("#pv-add").ClickAsync());
            await chooser.SetFilesAsync(file);

            await Assertions.Expect(view.Locator(".pv-cell[data-id]")).ToHaveCountAsync(4, new() { Timeout = 15000 });
            await Assertions.Expect(view.Locator(".pv-month").Last).ToContainTextAsync("May 2022");
            var list = await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/files/list?path=Photos/2022/05");
            Assert.Contains("Party.jpg", await list.TextAsync());
            Assert.Empty(errors);
        }
        finally { await context.CloseAsync(); }
    }

    [Fact]
    public async Task Photos_OnAPhone_TheGridFirst_TheAlbumsBehindTheBackBar_Test()
    {
        var (context, page, errors, slug) = await SpaceWithPhotosAsync(390, 844);
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/photos");
            var view = page.Locator("fb-photos-view");
            await Assertions.Expect(view.Locator(".pv-cell[data-id]")).ToHaveCountAsync(3, new() { Timeout = 15000 });
            await Assertions.Expect(view.Locator("#pv-scroller")).ToBeVisibleAsync();
            // At least three to a row, edge to edge.
            var first = (await view.Locator(".pv-cell[data-index='0']").BoundingBoxAsync())!;
            var second = (await view.Locator(".pv-cell[data-index='1']").BoundingBoxAsync())!;
            Assert.Equal(first.Y, second.Y, 1);
            Assert.True(first.Width < 390 / 3.0, $"a cell is {first.Width}px wide");
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "photos-phone.png") });
            Assert.Empty(errors);
        }
        finally { await context.CloseAsync(); }
    }
}
