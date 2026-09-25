using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

[Collection(UiCollection.Name)]
public class UiSmokeTests
{
    private readonly PlaywrightFixture _fixture;

    public UiSmokeTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Hub_LoadsAndNavigatesToNotes_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.BaseUrl);

        // Hub view renders with its tiles.
        var notesTile = page.Locator("a.tile[href='#/notes']");
        var todosTile = page.Locator("a.tile[href='#/todos']");
        var calendarTile = page.Locator("a.tile[href='#/calendar']");
        await notesTile.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });
        Assert.True(await notesTile.IsVisibleAsync());
        Assert.True(await todosTile.IsVisibleAsync());
        Assert.True(await calendarTile.IsVisibleAsync());

        // Click Notes tile -> hash changes, notes view mounts.
        await notesTile.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith("#/notes"), new PageWaitForURLOptions { Timeout = 3000 });
        Assert.EndsWith("#/notes", page.Url);

        var notesView = page.Locator("fb-notes-view");
        await notesView.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });
        Assert.True(await notesView.IsVisibleAsync());

        await context.CloseAsync();
    }

    // A phone in portrait: touch, 375px wide. The shell and the list/detail
    // views come from the vendored appkit, which collapses a split to one
    // pane at this width — these guard that the views keep opting in.
    private static BrowserNewContextOptions Phone() => new()
    {
        IgnoreHTTPSErrors = true,
        ViewportSize = new ViewportSize { Width = 375, Height = 740 },
        IsMobile = true,
        HasTouch = true,
    };

    [Theory]
    [InlineData("#/")]
    [InlineData("#/notes")]
    [InlineData("#/todos")]
    [InlineData("#/calendar")]
    public async Task Phone_ViewHasNoHorizontalScroll_Test(string hash)
    {
        var context = await _fixture.Browser!.NewContextAsync(Phone());
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.BaseUrl + "/" + hash);
        await page.Locator("#app-root > *").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });
        await page.WaitForTimeoutAsync(300);

        var overflow = await page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
        Assert.Equal(0, overflow);

        await context.CloseAsync();
    }

    [Fact]
    public async Task Phone_NotesOpensEditorAndGoesBack_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(Phone());
        var page = await context.NewPageAsync();

        // Seed one note through the API (the test-auth user owns it).
        var created = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/notes", new APIRequestContextOptions
        {
            DataObject = new { title = "Phone smoke note", content = "Body" },
        });
        Assert.True(created.Ok, $"seed note failed: {created.Status}");

        await page.GotoAsync(_fixture.BaseUrl + "/#/notes");
        var split = page.Locator("fb-notes-view sac-split");
        await split.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });

        // Collapsed: the list shows alone.
        Assert.True(await split.EvaluateAsync<bool>("s => s.collapsed"));
        Assert.Equal("start", await split.GetAttributeAsync("show"));

        // Opening a note brings the editor forward.
        await page.Locator(".nv-item", new PageLocatorOptions { HasText = "Phone smoke note" }).First.TapAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('fb-notes-view sac-split').getAttribute('show') === 'end'");
        // No title field: an API-made note opens with its title as the first line.
        Assert.Equal("# Phone smoke note\n\nBody",
            await page.Locator("fb-notes-view #content").EvaluateAsync<string>("e => e.value"));
        Assert.True(await page.Locator("fb-notes-view #content").IsVisibleAsync());

        // The split's back bar returns to the list.
        await split.Locator(".back button").TapAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('fb-notes-view sac-split').getAttribute('show') === 'start'");
        Assert.True(await page.Locator(".nv-item").First.IsVisibleAsync());

        await context.CloseAsync();
    }

    [Theory]
    [InlineData("#/keys", "fb-keys-settings-view")]
    [InlineData("#/spaces", "fb-spaces-settings-view")]
    public async Task SettingsView_LoadsOnKitComponents_Test(string hash, string view)
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);

        await page.GotoAsync(_fixture.BaseUrl + "/" + hash);
        await page.Locator(view).WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });

        // The status banner is the kit's now, and every element the view
        // renders is a registered component — no fb-* leftovers.
        Assert.Equal(1, await page.Locator($"{view} sac-status-banner").CountAsync());
        var unknown = await page.EvaluateAsync<string[]>(@"v => [...document.querySelector(v).querySelectorAll('*')]
            .map(e => e.localName).filter(n => n.includes('-') && !customElements.get(n))", view);
        Assert.Empty(unknown);
        Assert.Empty(errors);

        await context.CloseAsync();
    }

    [Fact]
    public async Task Notes_TagInput_CreatesAndSavesTag_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var created = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/notes", new APIRequestContextOptions
        {
            DataObject = new { title = "Tag smoke", content = "# Tag smoke\n" },
        });
        var id = (await created.JsonAsync())?.GetProperty("id").GetString();

        await page.GotoAsync(_fixture.BaseUrl + "/#/notes");
        await page.Locator(".nv-item", new PageLocatorOptions { HasText = "Tag smoke" }).First.ClickAsync();
        var input = page.Locator("fb-notes-view sac-chip-input#tag-input");
        await input.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });

        // A brand-new tag: type it, pick "Create", pick a colour.
        await input.Locator(".add-btn").ClickAsync();
        await input.Locator("input.entry").FillAsync("smoke-tag");
        await input.Locator(".opt.create").ClickAsync();
        await input.Locator(".swatch-btn[data-color='teal']").ClickAsync();

        // The note saves with the tag, and the tag is registered in its colour.
        string[] tags = [];
        for (var i = 0; i < 30 && !tags.Contains("smoke-tag"); i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            var note = await (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/notes/{id}")).JsonAsync();
            tags = note!.Value.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray();
        }
        Assert.Contains("smoke-tag", tags);
        var registry = await (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/tags")).JsonAsync();
        var tag = registry!.Value.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "smoke-tag");
        Assert.Equal("teal", tag.GetProperty("color").GetString());

        // System tags only the server sets are not offered.
        var offered = await input.EvaluateAsync<string[]>("e => e.suggestions.map(s => s.name)");
        Assert.DoesNotContain("source:mcp", offered);

        await context.CloseAsync();
    }

    [Fact]
    public async Task WorkspaceSwitcher_SwitchesIntoSpaceAndBack_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var name = "Switch smoke " + Guid.NewGuid().ToString("N")[..6];
        var space = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/spaces", new APIRequestContextOptions
        {
            DataObject = new { name },
        });
        Assert.True(space.Ok, $"space create failed: {space.Status}");
        var slug = (await space.JsonAsync())!.Value.GetProperty("slug").GetString()!;

        await page.GotoAsync(_fixture.BaseUrl + "/#/notes");
        var pill = page.Locator("#fb-context [slot='trigger']");
        await Assertions.Expect(pill).ToContainTextAsync("Personal");
        Assert.DoesNotContain("space", await pill.GetAttributeAsync("class") ?? "");

        // The menu marks the active workspace and lists the new space.
        await pill.ClickAsync();
        await Assertions.Expect(page.Locator("#fb-context button[data-action='ctx:user']")).ToHaveAttributeAsync("aria-current", "true");
        await page.Locator($"#fb-context button[data-action='ctx:space:{slug}']").ClickAsync();

        // Into the space: URL, label and the warm "shared data" tint.
        await page.WaitForURLAsync(u => u.Contains($"#/space/{slug}/"), new PageWaitForURLOptions { Timeout = 3000 });
        await Assertions.Expect(pill).ToContainTextAsync(name);
        Assert.Contains("space", await pill.GetAttributeAsync("class") ?? "");

        // And back out through "Personal".
        await pill.ClickAsync();
        await page.Locator("#fb-context button[data-action='ctx:user']").ClickAsync();
        await Assertions.Expect(pill).ToContainTextAsync("Personal");
        Assert.DoesNotContain("#/space/", page.Url);

        // "Manage spaces…" goes to the spaces settings.
        await pill.ClickAsync();
        await page.Locator("#fb-context button[data-action='manage-spaces']").ClickAsync();
        await page.WaitForURLAsync(u => u.EndsWith("#/spaces"), new PageWaitForURLOptions { Timeout = 3000 });

        await context.CloseAsync();
    }

    [Fact]
    public async Task TagManager_RecoloursAndProtectsSystemTags_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await page.APIRequest.PutAsync(_fixture.BaseUrl + "/api/v1/tags/manage-smoke", new APIRequestContextOptions
        {
            DataObject = new { color = "blue" },
        });

        await page.GotoAsync(_fixture.BaseUrl + "/#/notes");
        await page.Locator("fb-notes-view").WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });
        await page.EvaluateAsync("() => fb.tagManager.open()");

        var win = page.Locator("sac-window#fb-tag-manager");
        await Assertions.Expect(win).ToHaveAttributeAsync("open", "");
        // The name sits in the input's value property (never markup), so
        // find the row by it rather than by an attribute selector.
        await page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('#fb-tag-manager .fb-tags-name')].some(i => i.value === 'manage-smoke')");
        await page.EvaluateAsync(@"() => [...document.querySelectorAll('#fb-tag-manager .fb-tags-row')]
            .find(r => r.querySelector('.fb-tags-name').value === 'manage-smoke')
            .querySelector("".fb-tags-swatch[data-color='green']"").click()");

        string? color = null;
        for (var i = 0; i < 30 && color != "green"; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            var tags = await (await page.APIRequest.GetAsync(_fixture.BaseUrl + "/api/v1/tags")).JsonAsync();
            color = tags!.Value.EnumerateArray()
                .First(t => t.GetProperty("name").GetString() == "manage-smoke").GetProperty("color").GetString();
        }
        Assert.Equal("green", color);

        // System tags: name locked, no delete button.
        var system = win.Locator(".fb-tags-row", new LocatorLocatorOptions { Has = page.Locator(".fb-tags-badge") }).First;
        Assert.Equal("", await system.Locator("input.fb-tags-name").GetAttributeAsync("readonly"));
        Assert.Equal(0, await system.Locator(".fb-tags-delete").CountAsync());

        await context.CloseAsync();
    }

    [Fact]
    public async Task Notes_TitleIsFirstLine_NeverASecret_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();

        var created = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/notes", new APIRequestContextOptions
        {
            DataObject = new { title = "Title line smoke", content = "" },
        });
        Assert.True(created.Ok, $"seed note failed: {created.Status}");
        var id = (await created.JsonAsync())?.GetProperty("id").GetString();

        await page.GotoAsync(_fixture.BaseUrl + "/#/notes");
        await page.Locator(".nv-item", new PageLocatorOptions { HasText = "Title line smoke" }).First.ClickAsync();
        var editor = page.Locator("fb-notes-view #content");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('fb-notes-view #content')?.value === '# Title line smoke\\n'");

        // The title travels in plain text (FTS, embeddings, MCP), so a leading
        // secret block is skipped whole and never becomes it. Checked on the
        // helper directly: saving a secret goes through the vault prompt.
        Assert.Equal("Derived title", await page.EvaluateAsync<string>(
            "() => titleFromText(':::secret\\nhunter2\\n:::end\\n# Derived title\\n\\nBody text')"));
        Assert.Equal("", await page.EvaluateAsync<string>(
            "() => titleFromText(':::secret\\nhunter2\\n:::end\\n')"));

        // The save path: editing the first line renames the note.
        await editor.EvaluateAsync(@"e => {
            e.value = '# Derived title\n\nBody text';
            e.dispatchEvent(new Event('change'));
        }");

        string? title = null;
        for (var i = 0; i < 30 && title != "Derived title"; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            var got = await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/notes/{id}");
            title = (await got.JsonAsync())?.GetProperty("title").GetString();
        }
        Assert.Equal("Derived title", title);

        // The list row shows the new title, and its snippet doesn't repeat it.
        var row = page.Locator(".nv-item", new PageLocatorOptions { HasText = "Derived title" }).First;
        Assert.DoesNotContain("Derived title", await row.Locator(".nv-item-snippet").TextContentAsync() ?? "");

        // Frameless: no rounded corners, no focus ring on the writing surface.
        await editor.FocusAsync();
        var frame = await editor.EvaluateAsync<string>(
            "e => { const s = getComputedStyle(e); return s.borderTopLeftRadius + '|' + s.boxShadow; }");
        Assert.Equal("0px|none", frame);

        await context.CloseAsync();
    }
}
