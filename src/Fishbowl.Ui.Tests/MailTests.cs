using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The Mail app (spec 2026-10-07-mail-design, phase 1) against the fixture's
// sample mail server (Fishbowl.Host.Testing.SampleMailboxConnector: hosts
// ending in ".test", password "test-password"). Each test adds the account to
// a fresh space with its own address, so the sample store isn't shared.
[Collection(UiCollection.Name)]
public class MailTests
{
    private readonly PlaywrightFixture _fixture;

    public MailTests(PlaywrightFixture fixture) => _fixture = fixture;

    private static string Shot(string name) => Path.Combine(Path.GetTempPath(), $"mail-{name}.png");

    private async Task<(IBrowserContext Context, IPage Page, string Slug, string Api)> SpaceWithMailAsync(int width = 1400, int height = 900)
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = width, Height = height },
        });
        var page = await context.NewPageAsync();
        var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Mail " + Guid.NewGuid().ToString("N")[..6] } });
        var slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString()!;
        var api = $"{_fixture.BaseUrl}/api/v1/spaces/{slug}/mail";
        var add = await page.APIRequest.PostAsync($"{api}/accounts", new()
        {
            DataObject = new
            {
                provider = "custom",
                name = "Sample",
                address = $"pw-{Guid.NewGuid():N}@fishbowl.test",
                password = "test-password",
                imapHost = "imap.fishbowl.test",
                smtpHost = "smtp.fishbowl.test",
            },
        });
        Assert.True(add.Ok, await add.TextAsync());
        // The sync starts at once (the account asks for it); the sample is one pass.
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (true)
        {
            var accounts = (await (await page.APIRequest.GetAsync($"{api}/accounts")).JsonAsync())!.Value;
            var a = accounts[0];
            if (a.GetProperty("state").GetString() == "ok" && a.GetProperty("backfillDone").GetBoolean()) break;
            Assert.True(DateTime.UtcNow < deadline, "the sample account never synced: " + accounts);
            await page.WaitForTimeoutAsync(250);
        }
        return (context, page, slug, api);
    }

    [Fact]
    public async Task Mail_OneList_ThreadsBothWays_SafeHtml_Test()
    {
        var (context, page, slug, api) = await SpaceWithMailAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/mail");
            var rows = page.Locator("fb-mail-view .mv-item");
            // The list: the engine thread (3 messages, in and out), the newsletter
            // and the invoice — not what is only archived.
            await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 15000 });
            var engine = rows.Filter(new() { HasText = "Analytical engine notes" });
            await Assertions.Expect(engine.Locator(".mv-count")).ToHaveTextAsync("3");
            await Assertions.Expect(engine).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bunread\b"));
            await Assertions.Expect(page.Locator("fb-mail-view .mv-item", new() { HasText = "Old project" })).ToHaveCountAsync(0);
            await page.ScreenshotAsync(new() { Path = Shot("list") });

            // A thread holds both directions, oldest first; opening it reads it.
            await engine.ClickAsync();
            var msgs = page.Locator("fb-mail-view .mv-msg");
            await Assertions.Expect(msgs).ToHaveCountAsync(3);
            await Assertions.Expect(msgs.Nth(1).Locator(".mv-dir")).ToHaveAttributeAsync("name", "fb-mail-out");
            await Assertions.Expect(msgs.Nth(2)).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bopen\b"));
            await Assertions.Expect(msgs.Nth(2).Locator(".mv-text")).ToContainTextAsync("column by column");
            await Assertions.Expect(engine).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bunread\b"));
            await page.ScreenshotAsync(new() { Path = Shot("thread") });

            // HTML is its own sandboxed page; remote images wait to be asked for.
            await rows.Filter(new() { HasText = "This week in engines" }).ClickAsync();
            await Assertions.Expect(page.Locator("fb-mail-view .mv-images")).ToBeVisibleAsync();
            var frame = page.Locator("fb-mail-view iframe.mv-html");
            await Assertions.Expect(frame).ToHaveAttributeAsync("sandbox", "allow-same-origin allow-popups allow-popups-to-escape-sandbox");
            await Assertions.Expect(page.FrameLocator("fb-mail-view iframe.mv-html").Locator("h1")).ToHaveTextAsync("This week");
            // The frame is as tall as the page in it: nothing cut off.
            await page.WaitForFunctionAsync(@"() => {
                const f = document.querySelector('fb-mail-view iframe.mv-html');
                const d = f && f.contentDocument;
                // …and as wide: a 100vw block doesn't push the page sideways.
                return d && d.body && d.body.scrollHeight > 40 && d.documentElement.scrollHeight <= f.clientHeight + 1
                    && d.documentElement.scrollWidth <= d.documentElement.clientWidth;
            }");
            // Text the mail gives no font of its own is set like the app: the kit's Inter.
            Assert.Equal("Inter|loaded", await frame.EvaluateAsync<string>(@"async f => {
                const d = f.contentDocument, faces = await d.fonts.load('14px Inter');
                return getComputedStyle(d.querySelector('h1')).fontFamily.split(',')[0] + '|' + (faces[0]?.status ?? 'none');
            }"));
            var src = await frame.GetAttributeAsync("src");
            var html = await page.APIRequest.GetAsync(src!.StartsWith("http") ? src : _fixture.BaseUrl + src);
            var csp = html.Headers["content-security-policy"];
            Assert.Contains("sandbox", csp);
            Assert.Contains("img-src data: 'self';", csp);   // its own embedded parts, nothing remote
            Assert.DoesNotContain("allow-scripts", csp);
            await page.ScreenshotAsync(new() { Path = Shot("html") });
            await page.Locator("fb-mail-view .mv-images [data-images='once']").ClickAsync();
            await Assertions.Expect(frame).ToHaveAttributeAsync("src", new System.Text.RegularExpressions.Regex("images=true"));

            // An attachment comes from the server on demand.
            await rows.Filter(new() { HasText = "Your invoice" }).ClickAsync();
            var link = page.Locator("fb-mail-view .mv-att");
            await Assertions.Expect(link).ToContainTextAsync("invoice.pdf");
            var href = await link.GetAttributeAsync("href");
            var file = await page.APIRequest.GetAsync(_fixture.BaseUrl + href);
            Assert.True(file.Ok);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(await file.BodyAsync()));
            // A plain mail (no backgrounds) is in the app's colours, without paper;
            // its embedded logo (cid:) comes from the mail server.
            var plain = page.Locator("fb-mail-view iframe.mv-html");
            await Assertions.Expect(plain).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bplain\b"));
            await page.WaitForFunctionAsync(@"() => {
                const img = document.querySelector('fb-mail-view iframe.mv-html')?.contentDocument?.querySelector('img');
                return img && img.complete && img.naturalWidth === 8;
            }");
            await page.ScreenshotAsync(new() { Path = Shot("plain") });

            // Archived and search.
            await page.Locator("fb-mail-view #mv-tabs sac-tab[name='archived']").ClickAsync();
            await Assertions.Expect(page.Locator("fb-mail-view .mv-item", new() { HasText = "Old project" })).ToHaveCountAsync(1);
            await page.Locator("fb-mail-view #mv-tabs sac-tab[name='list']").ClickAsync();
            await page.Locator("#mv-search").FillAsync("variables");
            await Assertions.Expect(rows).ToHaveCountAsync(1);
            await Assertions.Expect(rows.First).ToContainTextAsync("Analytical engine notes");

            // Tags are the workspace's.
            await page.Locator("#mv-search").FillAsync("");
            await Assertions.Expect(rows).ToHaveCountAsync(3);
            var put = await page.APIRequest.GetAsync($"{api}/threads?q=invoice");
            var threadId = (await put.JsonAsync())!.Value[0].GetProperty("threadId").GetString();
            var tagged = await page.APIRequest.PutAsync($"{api}/threads/{threadId}/tags", new() { DataObject = new { tags = new[] { "Bills" } } });
            Assert.True(tagged.Ok, await tagged.TextAsync());
            Assert.Equal("bills", (await tagged.JsonAsync())!.Value.GetProperty("tags")[0].GetString());
            Assert.Empty(errors);
        }
        finally
        {
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Mail_WrongPassword_IsAnswered_AndTheContactShowsItsMail_Test()
    {
        var (context, page, slug, api) = await SpaceWithMailAsync();
        try
        {
            var refused = await page.APIRequest.PostAsync($"{api}/accounts", new()
            {
                DataObject = new { provider = "custom", address = "x@fishbowl.test", password = "wrong", imapHost = "imap.fishbowl.test", smtpHost = "smtp.fishbowl.test" },
            });
            Assert.Equal(400, refused.Status);
            Assert.Equal("mail_auth_failed", (await refused.JsonAsync())!.Value.GetProperty("error").GetString());

            var contact = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/contacts", new()
            {
                DataObject = new { kind = "person", firstName = "Ada", lastName = "Lovelace", emails = new[] { new { label = "work", value = "ada@example.org" } } },
            });
            var id = (await contact.JsonAsync())!.Value.GetProperty("id").GetString();
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/contacts");
            await page.Locator("fb-contacts-view .cv-item", new() { HasText = "Ada Lovelace" }).ClickAsync();
            var mailRow = page.Locator("fb-contacts-view .cv-link-row", new() { HasText = "Analytical engine notes" });
            await Assertions.Expect(mailRow).ToBeVisibleAsync(new() { Timeout = 10000 });
            await page.ScreenshotAsync(new() { Path = Shot("contact") });
            await mailRow.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"#/space/{slug}/mail$"));
            await Assertions.Expect(page.Locator("fb-mail-view .mv-msg")).ToHaveCountAsync(3, new() { Timeout = 10000 });
            await Assertions.Expect(page.Locator("fb-mail-view .mv-msg-from .mv-contact").First).ToBeVisibleAsync();
            Assert.NotNull(id);
        }
        finally
        {
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    // Tags as in Notes: a strip of chips under the search (all picked must
    // match), the account's source tag — its name, no prefix — always in it
    // and fixed in a conversation's tag bar; given tags in the chip input.
    [Fact]
    public async Task Mail_TagStrip_FiltersLikeNotes_SourceTagIsFixed_Test()
    {
        var (context, page, slug, api) = await SpaceWithMailAsync();
        try
        {
            var invoice = (await (await page.APIRequest.GetAsync($"{api}/threads?q=invoice")).JsonAsync())!.Value[0].GetProperty("threadId").GetString();
            Assert.True((await page.APIRequest.PutAsync($"{api}/threads/{invoice}/tags", new() { DataObject = new { tags = new[] { "bills" } } })).Ok);

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/mail");
            var rows = page.Locator("fb-mail-view .mv-item");
            await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 15000 });
            var strip = page.Locator("fb-mail-view #mv-tag-filter sac-chip");
            await Assertions.Expect(strip.First).ToHaveAttributeAsync("label", "sample");   // the account "Sample"
            await Assertions.Expect(page.Locator("fb-mail-view #mv-tag-filter sac-chip[label='bills']")).ToHaveCountAsync(1);
            await page.ScreenshotAsync(new() { Path = Shot("tags") });

            await page.Locator("fb-mail-view #mv-tag-filter sac-chip[label='bills']").ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(1);
            await Assertions.Expect(rows.First).ToContainTextAsync("Your invoice");
            await page.Locator("fb-mail-view #mv-tag-filter sac-chip[label='bills']").ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(3);

            // The conversation: the source fixed before the input, the given tags in it.
            await rows.Filter(new() { HasText = "Your invoice" }).ClickAsync();
            await Assertions.Expect(page.Locator("fb-mail-view .mv-tagbar > sac-chip[label='sample']")).ToHaveCountAsync(1);
            var given = await page.Locator("fb-mail-view .mv-tagbar sac-chip-input").EvaluateAsync<string[]>("el => el.value");
            Assert.Equal(new[] { "bills" }, given);
            // A plain mail's frame says the same colour scheme as its page: no white canvas under it.
            Assert.Equal("light", await page.Locator("fb-mail-view iframe.mv-html").EvaluateAsync<string>("el => getComputedStyle(el).colorScheme"));
        }
        finally
        {
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    // A mail made for light and dark (Revolut) shows its own dark design in the
    // dark theme — never white text on white paper, whatever the system
    // prefers (emulated the other way round each time); a light-only
    // newsletter stays on paper.
    [Fact]
    public async Task Mail_MadeForDark_ShowsItsDarkDesign_PaperStaysLight_Test()
    {
        var (context, page, slug, api) = await SpaceWithMailAsync();
        try
        {
            const string inFrame = @"(el, sel) => {
                const d = el.contentDocument, t = d.querySelector(sel);
                return [t ? getComputedStyle(t).color : '', getComputedStyle(d.documentElement).backgroundColor].join('|');
            }";
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/mail");
            var rows = page.Locator("fb-mail-view .mv-item");
            await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 15000 });

            // The newsletter (paper) in the dark theme: on white.
            await rows.Filter(new() { HasText = "This week in engines" }).ClickAsync();
            var frame = page.Locator("fb-mail-view iframe.mv-html");
            await Assertions.Expect(frame).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\b(plain|own-dark)\b"));
            await page.WaitForFunctionAsync("() => document.querySelector('fb-mail-view iframe.mv-html')?.contentDocument?.querySelector('h1')");
            Assert.EndsWith("|rgb(255, 255, 255)", await frame.EvaluateAsync<string>(inFrame, "h1"));

            // The adaptive one: its dark rules apply, on the app's ground.
            await page.Locator("fb-mail-view #mv-tabs sac-tab[name='archived']").ClickAsync();
            await rows.Filter(new() { HasText = "Old project" }).ClickAsync();
            await Assertions.Expect(frame).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bown-dark\b"));
            await page.WaitForFunctionAsync("() => document.querySelector('fb-mail-view iframe.mv-html')?.contentDocument?.querySelector('.t')");
            Assert.Equal("rgb(255, 255, 255)|rgba(0, 0, 0, 0)", await frame.EvaluateAsync<string>(inFrame, ".t"));
            await page.ScreenshotAsync(new() { Path = Shot("own-dark") });

            // The light theme on a dark system: the same mail on paper, its light design.
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
            await page.EvaluateAsync("() => localStorage.setItem('sac-theme', 'light')");
            await page.ReloadAsync();
            await page.Locator("fb-mail-view #mv-tabs sac-tab[name='archived']").ClickAsync();
            await rows.Filter(new() { HasText = "Old project" }).ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('fb-mail-view iframe.mv-html')?.contentDocument?.querySelector('.t')");
            await Assertions.Expect(frame).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bown-dark\b"));
            Assert.Equal("rgb(25, 28, 31)|rgb(255, 255, 255)", await frame.EvaluateAsync<string>(inFrame, ".t"));
        }
        finally
        {
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    // Unread shows as a dot; a conversation is deleted from its head — the
    // dialog asks only here or everywhere.
    [Fact]
    public async Task Mail_UnreadDot_DeleteOnlyInFishbowl_Test()
    {
        var (context, page, slug, api) = await SpaceWithMailAsync();
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/mail");
            var rows = page.Locator("fb-mail-view .mv-item");
            await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 15000 });
            var news = rows.Filter(new() { HasText = "This week in engines" });
            await Assertions.Expect(news).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bunread\b"));
            Assert.Equal("8px", await news.EvaluateAsync<string>("el => getComputedStyle(el, '::before').width"));
            Assert.Equal("none", await rows.Filter(new() { HasText = "Your invoice" }).EvaluateAsync<string>("el => getComputedStyle(el, '::before').content"));

            // A row's own menu (kit 2.29): marked read from there, the dot goes.
            var menu = page.Locator("sac-menu.sac-context-menu[open]");
            await news.ClickAsync(new() { Button = MouseButton.Right });
            await Assertions.Expect(menu.Locator("button[data-action]", new() { HasText = "Delete…" })).ToBeVisibleAsync();
            await page.WaitForTimeoutAsync(250);
            await menu.Locator("button[data-action]", new() { HasText = "Mark read" }).ClickAsync();
            await Assertions.Expect(news).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bunread\b"));

            await rows.Filter(new() { HasText = "Your invoice" }).ClickAsync();
            await page.Locator("fb-mail-view #mv-delete").ClickAsync();
            var dialog = page.Locator("sac-dialog[title='Delete this conversation?']");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Only in Fishbowl", Exact = true }).ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(2);
            await Assertions.Expect(rows.Filter(new() { HasText = "Your invoice" })).ToHaveCountAsync(0);
            // The last one went: the one above it is open now.
            await Assertions.Expect(news).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bselected\b"));
            await Assertions.Expect(page.Locator("fb-mail-view .mv-thread")).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Shot("deleted") });
        }
        finally
        {
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    // The list's keys (kit 2.30): ↓ opens the next conversation, Shift+↓
    // marks, Delete asks once for the marked ones — only here or everywhere.
    [Fact]
    public async Task Mail_Keys_OpenTheNext_MarkAndDelete_Test()
    {
        var (context, page, slug, _) = await SpaceWithMailAsync();
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/mail");
            var rows = page.Locator("fb-mail-view .mv-item");
            var selected = new System.Text.RegularExpressions.Regex(@"\bselected\b");
            await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 15000 });
            await rows.Nth(0).ClickAsync();
            await Assertions.Expect(rows.Nth(0)).ToHaveClassAsync(selected);

            await page.Keyboard.PressAsync("ArrowDown");
            await Assertions.Expect(rows.Nth(1)).ToHaveClassAsync(selected, new() { Timeout = 5000 });
            await Assertions.Expect(page.Locator("fb-mail-view .mv-thread")).ToBeVisibleAsync();

            var head = page.Locator("fb-mail-view #mv-head-title");
            await page.Keyboard.PressAsync("Shift+ArrowUp");
            await Assertions.Expect(head).ToHaveTextAsync("2 selected");

            // The first two go; the one below them is open at once, the list stays where it is.
            await page.Keyboard.PressAsync("Delete");
            await page.Locator("sac-dialog[title='Delete 2 conversations?']")
                .GetByRole(AriaRole.Button, new() { Name = "Only in Fishbowl", Exact = true }).ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(1, new() { Timeout = 5000 });
            await Assertions.Expect(head).ToHaveTextAsync("Inbox");
            await Assertions.Expect(rows.Nth(0)).ToContainTextAsync("Your invoice");
            await Assertions.Expect(rows.Nth(0)).ToHaveClassAsync(selected);
            await Assertions.Expect(page.Locator("fb-mail-view .mv-thread")).ToBeVisibleAsync();
        }
        finally
        {
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    // Phase 2: a conversation is flagged from its head (the star stays on) and
    // archived from its row's menu — it leaves the list, waits under Archived,
    // and the toast's Undo brings it back.
    [Fact]
    public async Task Mail_Flag_Archive_AndUndo_Test()
    {
        var (context, page, slug, _) = await SpaceWithMailAsync();
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/mail");
            var rows = page.Locator("fb-mail-view .mv-item");
            await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 15000 });
            var engine = rows.Filter(new() { HasText = "Analytical engine notes" });

            await engine.ClickAsync();
            var flag = page.Locator("fb-mail-view #mv-flag");
            await Assertions.Expect(flag).ToHaveAttributeAsync("aria-pressed", "false");
            await flag.ClickAsync();
            await Assertions.Expect(page.Locator("fb-mail-view #mv-flag")).ToHaveAttributeAsync("aria-pressed", "true");
            await Assertions.Expect(engine.Locator("sac-icon[name='star']")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("fb-mail-view #mv-archive")).ToHaveAttributeAsync("title", "Archive");
            await page.ScreenshotAsync(new() { Path = Shot("flagged") });

            var menu = page.Locator("sac-menu.sac-context-menu[open]");
            await engine.ClickAsync(new() { Button = MouseButton.Right });
            await Assertions.Expect(menu.Locator("button[data-action]", new() { HasText = "Remove flag" })).ToBeVisibleAsync();
            await page.WaitForTimeoutAsync(250);
            await menu.Locator("button[data-action]", new() { HasText = "Archive" }).ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(2);
            await Assertions.Expect(page.Locator("fb-mail-view .mv-empty")).ToBeVisibleAsync();

            // Under Archived, with "Move to inbox" in its head.
            await page.Locator("fb-mail-view #mv-tabs sac-tab[name='archived']").ClickAsync();
            await Assertions.Expect(engine).ToBeVisibleAsync();
            await engine.ClickAsync();
            await Assertions.Expect(page.Locator("fb-mail-view #mv-archive")).ToHaveAttributeAsync("title", "Move to inbox");

            // Moved back from its menu; the toast's Undo archives it again.
            await engine.ClickAsync(new() { Button = MouseButton.Right });
            var back = menu.Locator("button[data-action]", new() { HasText = "Move to inbox" });
            await Assertions.Expect(back).ToBeVisibleAsync();
            await page.WaitForTimeoutAsync(250);
            await back.ClickAsync();
            await Assertions.Expect(engine).ToHaveCountAsync(0);
            await page.Locator("#sac-toast-stack .toast").Filter(new() { HasText = "Moved to the inbox." }).Locator("button.action").ClickAsync();
            await Assertions.Expect(engine).ToBeVisibleAsync();
            await page.Locator("fb-mail-view #mv-tabs sac-tab[name='list']").ClickAsync();   // the inbox again: not in it
            await Assertions.Expect(rows).ToHaveCountAsync(2);
            await page.ScreenshotAsync(new() { Path = Shot("archived") });
        }
        finally
        {
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    // Phase 2, writing: a reply is filled from its message and shows the
    // original under the text; sent, it stands in its conversation. A new mail
    // left half-written waits under Drafts, opens again and can be discarded.
    [Fact]
    public async Task Mail_Reply_Send_AndADraftWaits_Test()
    {
        var (context, page, slug, _) = await SpaceWithMailAsync();
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/mail");
            var rows = page.Locator("fb-mail-view .mv-item");
            await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 15000 });
            await rows.Filter(new() { HasText = "Analytical engine notes" }).ClickAsync();
            await Assertions.Expect(page.Locator("fb-mail-view .mv-msg")).ToHaveCountAsync(3);

            // Reply: to Ada, "Re:", the original quoted under the text.
            await page.Locator("fb-mail-view #mv-reply").ClickAsync();
            var compose = page.Locator("fb-mail-view .mc");
            await Assertions.Expect(compose.Locator("h2")).ToHaveTextAsync("Reply");
            await Assertions.Expect(compose.Locator("#mc-to")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("ada@example\\.org"));
            await Assertions.Expect(compose.Locator("#mc-subject")).ToHaveValueAsync("Re: Analytical engine notes");
            await Assertions.Expect(compose.Locator("#mc-quote")).ToContainTextAsync("The variables live in the store");
            await compose.Locator("#mc-text").ClickAsync();
            await page.Keyboard.TypeAsync("Thanks, Ada.");
            await Assertions.Expect(compose.Locator("#mc-state")).ToHaveTextAsync("Draft saved", new() { Timeout = 5000 });
            await page.ScreenshotAsync(new() { Path = Shot("compose") });

            await compose.Locator("#mc-send").ClickAsync();
            await Assertions.Expect(page.Locator("#sac-toast-stack .toast").Filter(new() { HasText = "Sent." })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("fb-mail-view .mv-msg")).ToHaveCountAsync(4);
            await Assertions.Expect(page.Locator("fb-mail-view .mv-msg").Last.Locator(".mv-dir")).ToHaveAttributeAsync("name", "fb-mail-out");

            // A new mail, half-written, left for another conversation: it waits under Drafts.
            await page.Locator("fb-mail-view #mv-new").ClickAsync();
            await Assertions.Expect(compose.Locator("h2")).ToHaveTextAsync("New mail");
            await compose.Locator("#mc-to").FillAsync("Grace <grace@example.org>");
            await compose.Locator("#mc-subject").FillAsync("Hello Grace");
            await Assertions.Expect(compose.Locator("#mc-state")).ToHaveTextAsync("Draft saved", new() { Timeout = 5000 });
            await rows.Filter(new() { HasText = "Your invoice" }).ClickAsync();
            await Assertions.Expect(compose).ToHaveCountAsync(0);

            var drafts = page.Locator("fb-mail-view #mv-tabs sac-tab[name='drafts']");
            await Assertions.Expect(drafts.Locator(".mv-tab-count")).ToHaveTextAsync("1");
            await drafts.ClickAsync();
            var draft = rows.Filter(new() { HasText = "Hello Grace" });
            await Assertions.Expect(draft).ToContainTextAsync("Grace");
            await Assertions.Expect(rows).ToHaveCountAsync(1);   // the sent reply's draft is gone
            await draft.ClickAsync();
            await Assertions.Expect(compose.Locator("#mc-subject")).ToHaveValueAsync("Hello Grace");

            // Discarded from its menu, after a question: gone.
            await draft.ClickAsync(new() { Button = MouseButton.Right });
            var menu = page.Locator("sac-menu.sac-context-menu[open]");
            await Assertions.Expect(menu.Locator("button[data-action]", new() { HasText = "Discard" })).ToBeVisibleAsync();
            await page.WaitForTimeoutAsync(250);
            await menu.Locator("button[data-action]", new() { HasText = "Discard" }).ClickAsync();
            await page.Locator("sac-dialog[title='Discard this draft?']").GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("fb-mail-view .mv-none")).ToHaveTextAsync("No drafts");
            await Assertions.Expect(compose).ToHaveCountAsync(0);
        }
        finally
        {
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    // Spam, read live: the Spam button counts what came since the last look;
    // the view shows each as text, and "Not spam" brings one into the list.
    [Fact]
    public async Task Mail_Spam_CountsNew_ShowsText_NotSpamBringsItIn_Test()
    {
        var (context, page, slug, _) = await SpaceWithMailAsync();
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/mail");
            var rows = page.Locator("fb-mail-view .mv-item");
            await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 15000 });
            await Assertions.Expect(rows.Filter(new() { HasText = "Contract renewal" })).ToHaveCountAsync(0);
            var spam = page.Locator("fb-mail-view #mv-tabs sac-tab[name='spam']");
            await Assertions.Expect(spam.Locator(".mv-tab-count")).ToHaveTextAsync("2", new() { Timeout = 15000 });

            await spam.ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(2);
            await Assertions.Expect(spam.Locator(".mv-tab-count")).ToBeHiddenAsync();
            // Search and the account's chip stay, and narrow the spam here.
            var search = page.Locator("fb-mail-view #mv-search");
            await search.FillAsync("prize");
            await Assertions.Expect(rows).ToHaveCountAsync(1);
            await search.FillAsync("");
            await Assertions.Expect(rows).ToHaveCountAsync(2);
            await Assertions.Expect(page.Locator("fb-mail-view #mv-tag-filter sac-chip")).ToHaveCountAsync(1);
            await rows.Filter(new() { HasText = "Contract renewal" }).ClickAsync();
            await Assertions.Expect(page.Locator("fb-mail-view #mv-pane h2")).ToHaveTextAsync("Contract renewal");
            await Assertions.Expect(page.Locator("fb-mail-view #mv-pane .mv-text")).ToContainTextAsync("renewal by Friday");
            await Assertions.Expect(page.Locator("fb-mail-view #mv-pane iframe")).ToHaveCountAsync(0);   // never its HTML
            // Its address is a link — the bracket after it isn't — and from spam it warns first.
            var link = page.Locator("fb-mail-view #mv-pane .mv-text a");
            await Assertions.Expect(link).ToHaveAttributeAsync("href", "https://example.org/sign");
            await link.ClickAsync();
            var warning = page.Locator("sac-dialog[title='Open a link from spam?']");
            await Assertions.Expect(warning.GetByText("It goes to example.org:")).ToBeVisibleAsync();
            await warning.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(warning).ToHaveCountAsync(0);
            await page.ScreenshotAsync(new() { Path = Shot("spam") });

            await page.Locator("fb-mail-view #mv-not-spam").ClickAsync();
            await Assertions.Expect(page.Locator("#sac-toast-stack .toast").Filter(new() { HasText = "Moved to the inbox." })).ToBeVisibleAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(1);

            // The sync brings it in like any mail.
            await page.Locator("fb-mail-view #mv-tabs sac-tab[name='list']").ClickAsync();
            var renewal = rows.Filter(new() { HasText = "Contract renewal" });
            for (var i = 0; i < 20 && await renewal.CountAsync() == 0; i++)
            {
                await page.WaitForTimeoutAsync(1000);
                await page.Locator("fb-mail-view #mv-search").FillAsync(i % 2 == 0 ? " " : "");
            }
            await Assertions.Expect(renewal).ToBeVisibleAsync(new() { Timeout = 5000 });
        }
        finally
        {
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    // A phone: one pane at a time — the list, then the conversation with the split's back bar.
    [Fact]
    public async Task Mail_Phone_ListThenThread_Test()
    {
        var (context, page, slug, _) = await SpaceWithMailAsync(390, 844);
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/mail");
            var engine = page.Locator("fb-mail-view .mv-item", new() { HasText = "Analytical engine notes" });
            await Assertions.Expect(engine).ToBeVisibleAsync(new() { Timeout = 15000 });
            await page.ScreenshotAsync(new() { Path = Shot("phone-list") });
            await engine.ClickAsync();
            await Assertions.Expect(page.Locator("fb-mail-view .mv-msg")).ToHaveCountAsync(3);
            await Assertions.Expect(page.Locator("fb-mail-view .mv-msg").Nth(2)).ToBeInViewportAsync();
            await page.ScreenshotAsync(new() { Path = Shot("phone-thread") });
            var overflow = await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth");
            Assert.False(overflow, "the page scrolls sideways on a phone");
        }
        finally
        {
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Mail_AccountsWindow_ShowsTheAccount_AndAddsOne_Test()
    {
        var (context, page, slug, _) = await SpaceWithMailAsync();
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/mail");
            await Assertions.Expect(page.Locator("fb-mail-view .mv-item").First).ToBeVisibleAsync(new() { Timeout = 15000 });
            await page.Locator("#mv-accounts").ClickAsync();
            var win = page.Locator("#fb-mail-accounts");
            await Assertions.Expect(win.Locator(".fb-mail-account")).ToHaveCountAsync(1);
            await Assertions.Expect(win.Locator(".fb-mail-account .fb-row-name")).ToHaveTextAsync("Sample");
            await Assertions.Expect(win.Locator(".fb-mail-account-state")).ToContainTextAsync("36 messages");
            await page.ScreenshotAsync(new() { Path = Shot("accounts") });

            await win.Locator(".wa-bar button").ClickAsync();
            var dlg = page.Locator("sac-dialog .fb-mail-form");
            await Assertions.Expect(dlg).ToBeVisibleAsync();
            await Assertions.Expect(dlg.Locator("[data-hint]")).ToContainTextAsync("app-specific password");
            await page.ScreenshotAsync(new() { Path = Shot("add") });
        }
        finally
        {
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Mail_Rules_ANewRuleTagsTheMailAlreadyHere_Test()
    {
        var (context, page, slug, _) = await SpaceWithMailAsync();
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/mail");
            var engine = page.Locator("fb-mail-view .mv-item", new() { HasText = "Analytical engine notes" });
            await Assertions.Expect(engine).ToBeVisibleAsync(new() { Timeout = 15000 });
            await page.Locator("#mv-rules").ClickAsync();
            var win = page.Locator("#fb-mail-rules");
            await Assertions.Expect(win.Locator(".empty-state")).ToBeVisibleAsync();

            await win.Locator(".wa-bar button").ClickAsync();
            var form = page.Locator("sac-dialog .fb-mail-form");
            await Assertions.Expect(form).ToBeVisibleAsync();
            // The condition's text has the focus: "From or to" contains …
            await Assertions.Expect(form.Locator(".fb-rule-cond input[data-value]")).ToBeFocusedAsync();
            await page.Keyboard.TypeAsync("Lovelace");
            // "all / any" only shows with a second condition.
            var match = form.Locator("sac-select[name='match']");
            await Assertions.Expect(match).ToBeHiddenAsync();
            await form.Locator("[data-add-cond]").ClickAsync();
            await Assertions.Expect(form.Locator(".fb-rule-cond")).ToHaveCountAsync(2);
            await Assertions.Expect(form.Locator(".fb-rule-cond").Nth(1).Locator("input[data-value]")).ToBeFocusedAsync();
            await Assertions.Expect(match).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Shot("rule-two") });
            await form.Locator(".fb-rule-cond").Nth(1).Locator("[data-remove]").ClickAsync();
            await Assertions.Expect(match).ToBeHiddenAsync();
            var tags = form.Locator("sac-chip-input[name='tags']");
            await tags.Locator(".add-btn").ClickAsync();
            await tags.Locator("input.entry").FillAsync("helvetia");
            await tags.Locator(".opt.create").ClickAsync();
            await tags.Locator(".swatch-btn[data-color='teal']").ClickAsync();
            // A new rule also runs over what is already here.
            await Assertions.Expect(form.Locator("input[name='applyNow']")).ToBeCheckedAsync();
            await page.ScreenshotAsync(new() { Path = Shot("rule") });
            await page.GetByRole(AriaRole.Button, new() { Name = "Create", Exact = true }).ClickAsync();

            await Assertions.Expect(win.Locator(".fb-mail-rule")).ToHaveCountAsync(1);
            await Assertions.Expect(win.Locator(".fb-mail-rule .fb-row-name")).ToHaveTextAsync("Lovelace");
            await Assertions.Expect(win.Locator(".fb-mail-rule .fb-row-meta")).ToContainTextAsync("From or to: Lovelace");
            await Assertions.Expect(engine.Locator("sac-chip[label='helvetia']")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("fb-mail-view .mv-item", new() { HasText = "Your invoice" }).Locator("sac-chip[label='helvetia']")).ToHaveCountAsync(0);
            await page.ScreenshotAsync(new() { Path = Shot("rules") });

            // Escape closes the window.
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(win).ToBeHiddenAsync();
        }
        finally
        {
            await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }
}
