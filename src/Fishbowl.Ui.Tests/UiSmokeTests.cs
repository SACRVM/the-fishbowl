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
    [InlineData("#/files")]
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
    public async Task DateFormat_ProfileSetting_DrivesDisplayAndInput_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var title = "Format smoke " + Guid.NewGuid().ToString("N")[..6];
        var created = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/todos", new APIRequestContextOptions
        {
            DataObject = new { title },
        });
        var id = (await created.JsonAsync())!.Value.GetProperty("id").GetString()!;

        // Profile → "Date & time format" → German.
        await page.GotoAsync(_fixture.BaseUrl + "/#/todos");
        var account = page.Locator("#fb-account");
        await Assertions.Expect(account).ToBeVisibleAsync();
        await account.Locator("[slot='trigger']").ClickAsync();
        await account.Locator("button[data-action='profile']").ClickAsync();
        await page.Locator("#fb-date-format").SelectOptionAsync("de");
        await page.Locator("#fb-profile-window").EvaluateAsync("w => w.close?.()");

        // The kit's date + time fields follow the setting (sac.regional):
        // the date is typed German-style, the time into its hour/minute
        // segments; saved as that local instant.
        await page.Locator(".tv-item", new PageLocatorOptions { HasText = title }).ClickAsync();
        var due = page.Locator("sac-date-field#due-at");
        var dueInput = due.Locator("input");
        var hour = page.Locator("#due-at + sac-time-field input.hour");
        await Assertions.Expect(dueInput).ToHaveAttributeAsync("placeholder", "dd.mm.yyyy");
        await dueInput.FillAsync("26.9.2026");
        await dueInput.PressAsync("Enter");
        await Assertions.Expect(due).ToHaveAttributeAsync("value", "2026-09-26");
        await Assertions.Expect(dueInput).ToHaveValueAsync("26.09.2026");
        await hour.ClickAsync();
        await page.Keyboard.TypeAsync("1430");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#due-at + sac-time-field")).ToHaveAttributeAsync("value", "14:30");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_date_fields.png") });
        await Assertions.Expect(page.Locator(".tv-item", new PageLocatorOptions { HasText = title }).Locator(".tv-item-due"))
            .ToBeVisibleAsync();
        var saved = "";
        for (var i = 0; i < 20 && saved != "2026-9-26-14-30"; i++)
        {
            saved = await page.EvaluateAsync<string>(
                $"async () => {{ const t = await fb.api.todos.get('{id}'); if (!t.dueAt) return ''; const d = new Date(t.dueAt); return [d.getFullYear(), d.getMonth() + 1, d.getDate(), d.getHours(), d.getMinutes()].join('-'); }}");
            if (saved != "2026-9-26-14-30") await page.WaitForTimeoutAsync(100);
        }
        Assert.Equal("2026-9-26-14-30", saved);

        // Back to the default for the other tests.
        var reset = await page.APIRequest.PatchAsync(_fixture.BaseUrl + "/api/v1/me", new APIRequestContextOptions
        {
            DataObject = new Dictionary<string, object?> { ["dateFormat"] = null },
        });
        Assert.True(reset.Ok);
        await context.CloseAsync();
    }

    [Fact]
    public async Task Todos_KeepCreationOrder_ThroughDoneAndUndone_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var tag = "ord" + Guid.NewGuid().ToString("N")[..6];
        var ids = new Dictionary<string, string>();
        foreach (var n in new[] { "A", "B", "C" })
        {
            var r = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/todos", new APIRequestContextOptions
            {
                DataObject = new { title = $"{tag} {n}" },
            });
            ids[n] = (await r.JsonAsync())!.Value.GetProperty("id").GetString()!;
        }

        // B done, then undone again — that used to send it to the end.
        foreach (var completedAt in new[] { DateTime.UtcNow.ToString("o"), null })
        {
            var put = await page.APIRequest.PutAsync(_fixture.BaseUrl + $"/api/v1/todos/{ids["B"]}", new APIRequestContextOptions
            {
                DataObject = new { id = ids["B"], title = $"{tag} B", completedAt },
            });
            Assert.True(put.Ok, $"todo update failed: {put.Status}");
        }
        await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/todos", new APIRequestContextOptions
        {
            DataObject = new { title = $"{tag} D" },
        });

        await page.GotoAsync(_fixture.BaseUrl + "/#/todos");
        var rows = page.Locator(".tv-item-title", new PageLocatorOptions { HasText = tag });
        await Assertions.Expect(rows).ToHaveCountAsync(4);
        Assert.Equal(new[] { $"{tag} A", $"{tag} B", $"{tag} C", $"{tag} D" },
            (await rows.AllTextContentsAsync()).Select(t => t.Trim()).ToArray());

        // Drag D above A; the new order survives a reload.
        var from = (await page.Locator(".tv-item", new PageLocatorOptions { HasText = $"{tag} D" }).BoundingBoxAsync())!;
        var onto = (await page.Locator(".tv-item", new PageLocatorOptions { HasText = $"{tag} A" }).BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(from.X + from.Width / 2, from.Y + from.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(from.X + from.Width / 2, from.Y + from.Height / 2 - 10, new MouseMoveOptions { Steps = 3 });
        await page.Mouse.MoveAsync(onto.X + onto.Width / 2, onto.Y + 4, new MouseMoveOptions { Steps = 12 });
        await page.Mouse.UpAsync();
        var expected = new[] { $"{tag} D", $"{tag} A", $"{tag} B", $"{tag} C" };
        await Assertions.Expect(rows).ToHaveTextAsync(expected);
        await page.WaitForTimeoutAsync(300); // let the PUT land
        await page.ReloadAsync();
        await Assertions.Expect(rows).ToHaveTextAsync(expected);

        await context.CloseAsync();
    }

    [Fact]
    public async Task Notes_LongTitle_NeverRunsUnderRowActions_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var title = "Overlong title " + Guid.NewGuid().ToString("N") + " that keeps going well past the list column";
        await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/notes", new APIRequestContextOptions
        {
            DataObject = new { title, content = "# " + title + "\n" },
        });

        await page.GotoAsync(_fixture.BaseUrl + "/#/notes");
        var row = page.Locator(".nv-item", new PageLocatorOptions { HasText = title[..40] }).First;
        await row.HoverAsync();
        var titleBox = (await row.Locator(".nv-item-title").BoundingBoxAsync())!;
        var actionsBox = (await row.Locator(".nv-item-actions").BoundingBoxAsync())!;
        Assert.True(titleBox.X + titleBox.Width <= actionsBox.X,
            $"title ends at {titleBox.X + titleBox.Width}, actions start at {actionsBox.X}");

        // One gutter: the search field and the rows share both edges — in
        // the notes and the todos list.
        foreach (var (hash, search, item) in new[] { ("#/notes", ".nv-search input", ".nv-item"), ("#/todos", ".tv-search input", ".tv-item") })
        {
            if (hash == "#/todos")
            {
                await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/todos", new APIRequestContextOptions { DataObject = new { title = "gutter" } });
                await page.GotoAsync(_fixture.BaseUrl + "/" + hash);
            }
            var s = (await page.Locator(search).BoundingBoxAsync())!;
            var r = (await page.Locator(item).First.BoundingBoxAsync())!;
            Assert.InRange(r.X, s.X - 0.5, s.X + 0.5);
            Assert.InRange(r.X + r.Width, s.X + s.Width - 0.5, s.X + s.Width + 0.5);
        }

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
    public async Task Accents_PersonalAndSpaceColours_ApplyAndPersist_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var rootVar = (string name) => page.EvaluateAsync<string>(
            $"() => document.documentElement.style.getPropertyValue('{name}')");

        // Personal accent: picked in the profile window, applied to --accent,
        // saved on the user.
        await page.GotoAsync(_fixture.BaseUrl + "/#/notes");
        var account = page.Locator("#fb-account");
        await Assertions.Expect(account).ToBeVisibleAsync();
        await account.Locator("[slot='trigger']").ClickAsync();
        await account.Locator("button[data-action='profile']").ClickAsync();
        await page.Locator("#fb-profile-window sac-swatch[label='teal']").ClickAsync();
        await page.WaitForFunctionAsync("() => document.documentElement.style.getPropertyValue('--accent') === 'var(--palette-teal)'");
        var me = await (await page.APIRequest.GetAsync(_fixture.BaseUrl + "/api/v1/me")).JsonAsync();
        Assert.Equal("teal", me!.Value.GetProperty("accent").GetString());

        // "Default" clears it again (other tests share this user).
        await page.Locator("#fb-profile-window sac-swatch[label='Default']").ClickAsync();
        await page.WaitForFunctionAsync("() => document.documentElement.style.getPropertyValue('--accent') === ''");

        // Space colour: picked in the spaces settings, and inside the space
        // it replaces --accent-warm; back in personal, the default returns.
        var name = "Colour space " + Guid.NewGuid().ToString("N")[..6];
        var created = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/spaces", new APIRequestContextOptions { DataObject = new { name } });
        var slug = (await created.JsonAsync())!.Value.GetProperty("slug").GetString()!;
        await page.GotoAsync(_fixture.BaseUrl + "/#/spaces");
        var row = page.Locator($".space-row[data-slug='{slug}']");
        // The row leads with the space's colour; clicking it opens the picker,
        // picking saves and closes.
        await row.Locator("button.space-color").ClickAsync();
        var picker = page.Locator("sac-dialog[open]");
        await picker.Locator("sac-swatch[label='green']").ClickAsync();
        await Assertions.Expect(page.Locator("sac-dialog")).ToHaveCountAsync(0);
        await Assertions.Expect(row.Locator("button.space-color")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("palette-green"));

        await page.GotoAsync(_fixture.BaseUrl + $"/#/space/{slug}/notes");
        await page.WaitForFunctionAsync("() => document.documentElement.style.getPropertyValue('--accent-warm') === 'var(--palette-green)'");
        // …and the whole UI's accent, not just the switcher tint.
        Assert.Equal("var(--palette-green)", await rootVar("--accent"));
        await page.Locator("#fb-context [slot='trigger']").ClickAsync();
        await page.Locator("#fb-context button[data-action='ctx:user']").ClickAsync();
        await page.WaitForFunctionAsync("() => document.documentElement.style.getPropertyValue('--accent-warm') === ''");
        Assert.Equal("", await rootVar("--accent"));

        await context.CloseAsync();
    }

    [Fact]
    public async Task Spaces_CreatedSpaceIsInSwitcherAndOpens_WithoutReload_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var name = "Live space " + Guid.NewGuid().ToString("N")[..6];

        // Loaded first, so the switcher's initial list can't contain it.
        await page.GotoAsync(_fixture.BaseUrl + "/#/spaces");
        await page.Locator("#name-input").FillAsync(name);
        await page.Locator("#create-btn").ClickAsync();
        var row = page.Locator(".space-row", new PageLocatorOptions { HasText = name });
        await Assertions.Expect(row).ToBeVisibleAsync();
        var slug = await row.GetAttributeAsync("data-slug");

        // The switcher picked it up with no reload.
        var pill = page.Locator("#fb-context [slot='trigger']");
        await pill.ClickAsync();
        await Assertions.Expect(page.Locator($"#fb-context button[data-action='ctx:space:{slug}']")).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");

        // And the row's open button switches into it.
        await row.Locator(".open-btn").ClickAsync();
        await page.WaitForURLAsync(u => u.Contains($"#/space/{slug}/notes"), new PageWaitForURLOptions { Timeout = 3000 });
        await Assertions.Expect(pill).ToContainTextAsync(name);

        // API keys opened from inside the space default to that space, and
        // list that space's keys first, under its name.
        var key = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/keys", new APIRequestContextOptions
        {
            DataObject = new { name = "space key", contextType = "space", contextId = slug, scopes = new[] { "read:notes" } },
        });
        Assert.True(key.Ok, $"key create failed: {key.Status}");
        await page.GotoAsync(_fixture.BaseUrl + $"/#/space/{slug}/keys");
        await Assertions.Expect(page.Locator("#key-context")).ToHaveValueAsync($"space::{slug}");
        var firstGroup = page.Locator("#key-list .key-group").First;
        await Assertions.Expect(firstGroup).ToHaveAttributeAsync("data-context", $"space:{slug}");
        await Assertions.Expect(firstGroup).ToContainTextAsync(name);
        // Each row names its context too, not only the group heading.
        await Assertions.Expect(page.Locator(".key-row", new PageLocatorOptions { HasText = "space key" }).Locator(".key-ctx"))
            .ToHaveTextAsync(name);

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
    public async Task Keys_TokenRevealSurvivesEscape_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/#/keys");
        await page.Locator("#key-name").FillAsync("reveal smoke");
        await page.Locator("#create-btn").ClickAsync();

        var dialog = page.Locator("sac-dialog[title='Key created']");
        await dialog.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
        var token = (await dialog.Locator(".fb-token-block").TextContentAsync())!.Trim();
        Assert.StartsWith("fb_live_", token);
        Assert.Equal(token, await dialog.Locator("sac-copy-button").GetAttributeAsync("value"));

        // The copy button sits centred beside the token box, a size up from
        // the kit's 26px default.
        var block = (await dialog.Locator(".fb-token-block").BoundingBoxAsync())!;
        var copy = (await dialog.Locator("sac-copy-button button").BoundingBoxAsync())!;
        Assert.InRange(copy.Y + copy.Height / 2, block.Y + block.Height / 2 - 2, block.Y + block.Height / 2 + 2);
        Assert.True(copy.Width >= 34, $"copy button only {copy.Width}px wide");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_token_dialog.png") });

        // Escape must not lose a token that is shown only once.
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForTimeoutAsync(400);
        var again = page.Locator("sac-dialog[title='Key created']");
        await again.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });
        Assert.Equal(token, (await again.Locator(".fb-token-block").TextContentAsync())!.Trim());

        // Only "I've saved it" closes it.
        await again.GetByRole(AriaRole.Button, new() { Name = "I've saved it" }).ClickAsync();
        await page.Locator("sac-dialog[title='Key created']").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 3000 });

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
