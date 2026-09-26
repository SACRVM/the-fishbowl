using System.Text;
using Dapper;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// Files phase 2 in the browser: the commander view on the kit's
// sac-file-browser, its modern chords (no F-keys), uploads with a name
// clash, the trash, cross-workspace transfer, the preview pane and quick
// look, the phone layout, and a read-only space pane that offers no write
// action at all. Files are seeded through the API, like a sync client would.
[Collection(UiCollection.Name)]
public class FilesTests
{
    private readonly PlaywrightFixture _fixture;

    public FilesTests(PlaywrightFixture fixture) => _fixture = fixture;

    private async Task<(IBrowserContext Context, IPage Page, List<string> Errors)> OpenAsync(BrowserNewContextOptions? options = null)
    {
        var context = await _fixture.Browser!.NewContextAsync(options ?? new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        // A refused request (a 412 name clash, a 404 while looking for a free
        // name) is logged by the browser as a failed resource — expected here.
        page.Console += (_, m) => { if (m.Type == "error" && !m.Text.StartsWith("Failed to load resource")) errors.Add(m.Text); };
        return (context, page, errors);
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    private async Task PutAsync(IPage page, string path, string content, string? space = null)
    {
        var root = space is null ? "/api/v1/files" : $"/api/v1/spaces/{space}/files";
        var res = await page.APIRequest.PutAsync($"{_fixture.BaseUrl}{root}/content?path={Uri.EscapeDataString(path)}&parents=1",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["X-Fishbowl-Upload"] = "1", ["If-None-Match"] = "*" },
                DataByte = Encoding.UTF8.GetBytes(content),
            });
        Assert.True(res.Ok, $"PUT {path}: {res.Status} {await res.TextAsync()}");
    }

    private async Task<bool> ExistsAsync(IPage page, string path, string? space = null)
    {
        var root = space is null ? "/api/v1/files" : $"/api/v1/spaces/{space}/files";
        var res = await page.APIRequest.GetAsync($"{_fixture.BaseUrl}{root}/stat?path={Uri.EscapeDataString(path)}");
        return res.Ok;
    }

    private static ILocator Pane(IPage page, string side) => page.Locator($"fb-files-view .fv-pane[data-side='{side}']");
    private static ILocator Rows(IPage page, string side) => Pane(page, side).Locator("sac-file-browser").Locator(".canvas > .row");
    private static ILocator Row(IPage page, string side, string name) => Rows(page, side).Filter(new() { HasText = name });
    private static ILocator Column(IPage page, string side) => page.Locator($"fb-files-view .fv-col[data-col='{side}']");
    private static ILocator Status(IPage page, string side) => Column(page, side).Locator(".fv-status");
    private static ILocator Tab(IPage page, string side, string name) => Column(page, side).Locator($"sac-tab[name='{name}']");

    [Fact]
    public async Task Files_CommanderLoads_ListsSeededFiles_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var folder = Unique("load-");
            await PutAsync(page, $"{folder}/hello.txt", "Hello, Fishbowl.");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/files/{folder}");
            await Assertions.Expect(Row(page, "left", "hello.txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            // Both panes, the shortcut bar with its chords, no F-keys.
            await Assertions.Expect(Pane(page, "right").Locator("sac-file-browser")).ToBeVisibleAsync();
            var bar = page.Locator("fb-files-view sac-shortcut-bar");
            await Assertions.Expect(bar).ToContainTextAsync("New folder");
            Assert.DoesNotContain("F7", await bar.InnerTextAsync());
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_files.png") });
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private async Task<string> CreateSpaceAsync(IPage page, string name)
    {
        var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new APIRequestContextOptions
        {
            DataObject = new { name = name + " " + Guid.NewGuid().ToString("N")[..6] },
        });
        Assert.True(res.Ok, await res.TextAsync());
        return (await res.JsonAsync())!.Value.GetProperty("slug").GetString()!;
    }

    // A space someone else owns, where the test user may only read.
    private async Task<string> SeedReadonlySpaceAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = new DatabaseFactory(_fixture.DataDir);
        var system = new SystemRepository(db);
        var ownerId = "ui-owner-" + Guid.NewGuid().ToString("N")[..8];
        await system.CreateUserAsync(ownerId, "Owner", null, null, ct);
        var space = await new SpaceRepository(db).CreateAsync(ownerId, "Read only " + Guid.NewGuid().ToString("N")[..6], ct);
        using var conn = db.CreateSystemConnection();
        await conn.ExecuteAsync(
            "INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@s, @u, 'readonly', @t)",
            new { s = space.Id, u = "test-internal-id", t = DateTime.UtcNow.ToString("o") });
        return space.Slug;
    }

    private static async Task PickWorkspaceAsync(IPage page, string side, string workspace)
    {
        await page.Keyboard.PressAsync(side == "left" ? "Alt+1" : "Alt+2");
        await Pane(page, side).Locator($"sac-menu button[data-action='{workspace}']").ClickAsync();
    }

    private static Task FocusRowAsync(IPage page, string side, string name) => Row(page, side, name).ClickAsync();

    private static ILocator Toast(IPage page, string text) => page.Locator("#sac-toast-stack").GetByText(text).First;

    [Fact]
    public async Task Files_Keys_NewFolderRenameTrashRestoreAndDeletePermanently_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var folder = Unique("keys-");
            await PutAsync(page, $"{folder}/keep.txt", "keep");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/files/{folder}");
            await Assertions.Expect(Row(page, "left", "keep.txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            await FocusRowAsync(page, "left", "keep.txt");

            // Alt+N: a folder, and the pane walks into it (the URL follows).
            await page.Keyboard.PressAsync("Alt+N");
            await page.Keyboard.TypeAsync("Fresh");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"#/files/{folder}/Fresh$"), new() { Timeout = 10000 });
            Assert.True(await ExistsAsync(page, $"{folder}/Fresh"));

            // Backspace goes up and lands on the folder just left; Alt+R renames it.
            await page.Keyboard.PressAsync("Backspace");
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"#/files/{folder}$"), new() { Timeout = 10000 });
            await Assertions.Expect(Row(page, "left", "Fresh")).ToHaveCountAsync(1);
            await page.Keyboard.PressAsync("Alt+R");
            await page.Keyboard.PressAsync("Control+A");
            await page.Keyboard.TypeAsync("Renamed");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(Row(page, "left", "Renamed")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            Assert.True(await ExistsAsync(page, $"{folder}/Renamed"));

            // Delete: to the trash, no question.
            await page.Keyboard.PressAsync("Delete");
            await Assertions.Expect(Row(page, "left", "Renamed")).ToHaveCountAsync(0, new() { Timeout = 10000 });
            Assert.False(await ExistsAsync(page, $"{folder}/Renamed"));

            // The trash window restores it.
            await page.Locator("#fb-view-toolbar button[title='Trash']").ClickAsync();
            var trashRow = page.Locator("sac-window .fv-trash-row").Filter(new() { HasText = $"{folder}/Renamed" });
            await trashRow.GetByRole(AriaRole.Button, new() { Name = "Restore" }).ClickAsync();
            await Assertions.Expect(Row(page, "left", "Renamed")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            Assert.True(await ExistsAsync(page, $"{folder}/Renamed"));
            await page.EvaluateAsync("() => document.querySelectorAll('sac-window').forEach(w => w.close())");

            // Shift+Delete asks, then deletes for good — not into the trash.
            await FocusRowAsync(page, "left", "keep.txt");
            await page.Keyboard.PressAsync("Shift+Delete");
            var confirm = page.Locator("sac-dialog[title='Delete permanently?']");
            await confirm.GetByRole(AriaRole.Button, new() { Name = "Delete permanently" }).ClickAsync();
            await Assertions.Expect(Row(page, "left", "keep.txt")).ToHaveCountAsync(0, new() { Timeout = 10000 });
            var trash = await (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/files/trash")).TextAsync();
            Assert.DoesNotContain($"{folder}/keep.txt", trash);
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Files_CopyAndMoveToOtherPane_AcrossWorkspaces_WithClash_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var slug = await CreateSpaceAsync(page, "Files target");
            var folder = Unique("xfer-");
            await PutAsync(page, $"{folder}/a.txt", "alpha");
            await PutAsync(page, $"{folder}/b.txt", "bravo");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/files/{folder}");
            await Assertions.Expect(Row(page, "left", "a.txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });

            // Alt+2: the right pane goes to the space.
            await PickWorkspaceAsync(page, "right", $"space:{slug}");
            await Assertions.Expect(Pane(page, "right")).ToHaveAttributeAsync("data-space", "");

            // Alt+C copies the cursor file into the other pane's folder.
            await FocusRowAsync(page, "left", "a.txt");
            await page.Keyboard.PressAsync("Alt+C");
            await Assertions.Expect(Row(page, "right", "a.txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            Assert.True(await ExistsAsync(page, "a.txt", slug));
            Assert.True(await ExistsAsync(page, $"{folder}/a.txt"));

            // Again: the name is taken — Keep both.
            await FocusRowAsync(page, "left", "a.txt");
            await page.Keyboard.PressAsync("Alt+C");
            var clash = page.Locator("sac-dialog[title='A file with this name exists']");
            await clash.GetByRole(AriaRole.Button, new() { Name = "Keep both" }).ClickAsync();
            await Assertions.Expect(Row(page, "right", "a (2).txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });

            // Alt+M moves: gone here, there in the space.
            await FocusRowAsync(page, "left", "b.txt");
            await page.Keyboard.PressAsync("Alt+M");
            await Assertions.Expect(Row(page, "right", "b.txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            await Assertions.Expect(Row(page, "left", "b.txt")).ToHaveCountAsync(0);
            Assert.False(await ExistsAsync(page, $"{folder}/b.txt"));
            Assert.True(await ExistsAsync(page, "b.txt", slug));
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Files_Upload_WithNameClash_KeepBothAndReplace_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var folder = Unique("up-");
            await PutAsync(page, $"{folder}/seed.txt", "seed");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/files/{folder}");
            await Assertions.Expect(Row(page, "left", "seed.txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            var input = Pane(page, "left").Locator("sac-drop-zone input[type=file]");
            FilePayload Note(string text) => new() { Name = "note.txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes(text) };

            await input.SetInputFilesAsync(Note("one"));
            await Assertions.Expect(Row(page, "left", "note.txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });

            var clash = page.Locator("sac-dialog[title='A file with this name exists']");
            await input.SetInputFilesAsync(Note("two"));
            await clash.GetByRole(AriaRole.Button, new() { Name = "Keep both" }).ClickAsync();
            await Assertions.Expect(Row(page, "left", "note (2).txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });

            await input.SetInputFilesAsync(Note("three, longer"));
            await clash.GetByRole(AriaRole.Button, new() { Name = "Replace" }).ClickAsync();
            await Assertions.Expect(Row(page, "left", "note.txt")).ToContainTextAsync("13 B", new() { Timeout = 10000 });
            var body = await (await page.APIRequest.GetAsync(
                $"{_fixture.BaseUrl}/api/v1/files/content?path={Uri.EscapeDataString($"{folder}/note.txt")}")).TextAsync();
            Assert.Equal("three, longer", body);
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    // A tiny PDF — enough for the sniffer.
    private const string Pdf = "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj 2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj " +
        "3 0 obj<</Type/Page/MediaBox[0 0 200 200]/Parent 2 0 R>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF";

    [Fact]
    public async Task Files_PreviewPane_QuickLook_AndPdfInNewTab_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var folder = Unique("look-");
            await PutAsync(page, $"{folder}/a.md", "# Hello preview\n\nSome *markdown*.");
            await PutAsync(page, $"{folder}/z.pdf", Pdf);
            await page.GotoAsync($"{_fixture.BaseUrl}/#/files/{folder}");
            await Assertions.Expect(Row(page, "left", "a.md")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            await FocusRowAsync(page, "left", "a.md");

            // Alt+P: the right pane previews the left pane's cursor file.
            await page.Keyboard.PressAsync("Alt+P");
            var preview = page.Locator("fb-files-view .fv-preview sac-quick-look");
            await Assertions.Expect(preview.Locator("h1")).ToHaveTextAsync("Hello preview", new() { Timeout = 10000 });
            await Assertions.Expect(Pane(page, "right")).ToBeHiddenAsync();
            await Assertions.Expect(Tab(page, "right", "preview")).ToHaveAttributeAsync("active", "");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_files_preview.png") });

            // It follows the cursor: a PDF gets the info card with Open in new tab.
            await page.Keyboard.PressAsync("ArrowDown");
            var open = preview.GetByRole(AriaRole.Button, new() { Name = "Open in new tab" });
            await Assertions.Expect(open).ToBeVisibleAsync(new() { Timeout = 10000 });
            // (The headless shell downloads PDFs; Files_InlinePdf_RendersInChromium_Test
            // checks the real viewer.) Here: the new tab asks for the inline variant.
            var requested = new List<string>();
            context.Request += (_, r) => requested.Add(r.Url);
            var tabTask = context.WaitForPageAsync();
            await open.ClickAsync();
            var tab = await tabTask;
            for (var i = 0; i < 50 && !requested.Any(u => u.Contains("inline=1")); i++) await page.WaitForTimeoutAsync(100);
            Assert.Contains(requested, u => u.Contains("/files/content") && u.Contains("inline=1"));
            await tab.CloseAsync();

            // Space: quick look, maximised; Escape closes it.
            await page.Keyboard.PressAsync("Alt+P");
            await FocusRowAsync(page, "left", "a.md");
            await page.Keyboard.PressAsync("Space");
            var look = page.Locator("sac-window sac-quick-look");
            await Assertions.Expect(look.Locator("h1")).ToHaveTextAsync("Hello preview", new() { Timeout = 10000 });
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_files_quicklook.png") });
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page.Locator("sac-window sac-quick-look")).ToHaveCountAsync(0, new() { Timeout = 5000 });
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Files_RightTabs_BrowsePreviewProperties_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var folder = Unique("tabs-");
            await PutAsync(page, $"{folder}/notes.txt", "twelve bytes");
            await PutAsync(page, $"{folder}/sub/inner.txt", "x");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/files/{folder}");
            await Assertions.Expect(Row(page, "left", "notes.txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });

            // Left: only Browse. Right: Browse, Preview, Properties.
            Assert.Equal(1, await Column(page, "left").Locator("sac-tab").CountAsync());
            Assert.Equal(new[] { "Browse", "Preview", "Properties" },
                (await Column(page, "right").Locator("sac-tab").AllInnerTextsAsync()).Select(t => t.Trim()).ToArray());
            await Assertions.Expect(Status(page, "left")).ToContainTextAsync("1 folder · 1 file");

            // Alt+I: properties of the left pane's cursor file.
            await FocusRowAsync(page, "left", "notes.txt");
            await page.Keyboard.PressAsync("Alt+I");
            var props = page.Locator("fb-files-view .fv-props");
            await Assertions.Expect(props).ToBeVisibleAsync();
            await Assertions.Expect(Pane(page, "right")).ToBeHiddenAsync();
            await Assertions.Expect(Tab(page, "right", "props")).ToHaveAttributeAsync("active", "");
            await Assertions.Expect(props.Locator("h3")).ToHaveTextAsync("notes.txt");
            await Assertions.Expect(props).ToContainTextAsync("12 bytes");
            await Assertions.Expect(props).ToContainTextAsync($"/{folder}/notes.txt");
            await Assertions.Expect(props).ToContainTextAsync("Personal");
            await Assertions.Expect(props.GetByRole(AriaRole.Button, new() { Name = "Open in new tab" })).ToBeVisibleAsync();
            await Assertions.Expect(props.GetByRole(AriaRole.Link, new() { Name = "Download" })).ToBeVisibleAsync();
            await Assertions.Expect(Status(page, "right")).ToContainTextAsync("notes.txt");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_files_properties.png") });

            // A folder (a click would open it — walk there with the keys):
            // its item count comes from one listing.
            await page.Keyboard.PressAsync("Home");
            await Assertions.Expect(props.Locator("h3")).ToHaveTextAsync("sub", new() { Timeout = 10000 });
            await Assertions.Expect(props).ToContainTextAsync("0 folders, 1 file", new() { Timeout = 10000 });

            // Clicking a tab switches too; Alt+P flips Preview ↔ Browse.
            await Tab(page, "right", "preview").ClickAsync();
            await Assertions.Expect(page.Locator("fb-files-view .fv-preview")).ToBeVisibleAsync();
            await Assertions.Expect(props).ToBeHiddenAsync();
            await FocusRowAsync(page, "left", "notes.txt");
            await page.Keyboard.PressAsync("Alt+P");
            await Assertions.Expect(Pane(page, "right")).ToBeVisibleAsync();
            await Assertions.Expect(Tab(page, "right", "browse")).ToHaveAttributeAsync("active", "");
            await page.Keyboard.PressAsync("Alt+I");
            await Assertions.Expect(props).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Alt+I");
            await Assertions.Expect(Pane(page, "right")).ToBeVisibleAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    // Chromium must actually render the inline PDF under the headers FilesApi
    // sends (a `sandbox` CSP makes Chrome refuse): the viewer's embed shows
    // up instead of a blocked page or a download.
    [Fact]
    public async Task Files_InlinePdf_RendersInChromium_Test()
    {
        // The headless shell has no PDF viewer (it downloads instead); full
        // Chromium in new-headless mode has the real one.
        await using var browser = await _fixture.Playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Channel = "chromium" });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        try
        {
            var path = Unique("pdf-") + "/doc.pdf";
            await PutAsync(page, path, Pdf);
            var res = await page.GotoAsync($"{_fixture.BaseUrl}/api/v1/files/content?path={Uri.EscapeDataString(path)}&inline=1");
            Assert.NotNull(res);
            var headers = await res!.AllHeadersAsync();
            Assert.Equal("application/pdf", headers["content-type"]);
            Assert.StartsWith("inline", headers["content-disposition"]);
            Assert.DoesNotContain("sandbox", headers["content-security-policy"]);
            await Assertions.Expect(page.Locator("embed[type='application/pdf'], embed[type='application/x-google-chrome-pdf']").First)
                .ToBeAttachedAsync(new() { Timeout = 10000 });
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Files_ReadonlySpacePane_OffersNoWriteActions_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var slug = await SeedReadonlySpaceAsync();
            var folder = Unique("ro-");
            await PutAsync(page, $"{folder}/mine.txt", "mine");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/files/{folder}");
            await Assertions.Expect(Row(page, "left", "mine.txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            await PickWorkspaceAsync(page, "right", $"space:{slug}");
            await Assertions.Expect(Status(page, "right")).ToContainTextAsync("read-only", new() { Timeout = 10000 });

            // Left active: nothing can go into the read-only pane.
            await FocusRowAsync(page, "left", "mine.txt");
            var bar = page.Locator("fb-files-view sac-shortcut-bar");
            await Assertions.Expect(bar).ToContainTextAsync("New folder");
            await Assertions.Expect(bar).Not.ToContainTextAsync("Copy →");
            await Assertions.Expect(bar).Not.ToContainTextAsync("Move →");

            // Right active: no write action at all — absent, not greyed.
            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(Pane(page, "right")).ToHaveAttributeAsync("data-active", "");
            foreach (var label in new[] { "New folder", "Rename", "Trash", "Upload", "Move" })
                await Assertions.Expect(bar).Not.ToContainTextAsync(label);
            await Assertions.Expect(bar).ToContainTextAsync("Download");
            Assert.Equal(0, await page.Locator("#fb-view-toolbar button[title^='Upload']").CountAsync());
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Files_Phone_OnePane_CopyToPicksADestination_Test()
    {
        var (context, page, errors) = await OpenAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 375, Height = 740 },
            IsMobile = true,
            HasTouch = true,
        });
        try
        {
            var slug = await CreateSpaceAsync(page, "Phone target");
            var folder = Unique("phone-");
            await PutAsync(page, $"{folder}/photo.txt", "not really a photo");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/files/{folder}");
            await Assertions.Expect(Row(page, "left", "photo.txt")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            await Assertions.Expect(Pane(page, "right")).ToBeHiddenAsync();
            // One pane, one tab: no Preview/Properties tab on a phone.
            await Assertions.Expect(Tab(page, "left", "browse")).ToBeVisibleAsync();
            await Assertions.Expect(Tab(page, "right", "preview")).ToBeHiddenAsync();
            await Assertions.Expect(Tab(page, "right", "props")).ToBeHiddenAsync();
            var overflow = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
            Assert.Equal(0, overflow);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_files_phone.png") });

            // The bar folded into the nav's "…"; Copy to… opens the picker.
            await Assertions.Expect(page.Locator("fb-files-view sac-shortcut-bar")).ToHaveAttributeAsync("folded", "");
            await Row(page, "left", "photo.txt").TapAsync();
            await page.Locator("#fb-nav button.more-btn").ClickAsync();
            await page.Locator("#fb-nav").GetByText("Copy to…").ClickAsync();
            var dialog = page.Locator("sac-dialog[title='Copy to…']");
            await dialog.Locator("select").SelectOptionAsync($"space:{slug}");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Copy here" }).ClickAsync();
            await Assertions.Expect(Toast(page, "Copied 1 item")).ToBeVisibleAsync(new() { Timeout = 10000 });
            Assert.True(await ExistsAsync(page, "photo.txt", slug));
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
