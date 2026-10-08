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
                return d && d.body && d.body.scrollHeight > 40 && d.body.scrollHeight <= f.clientHeight + 1;
            }");
            var src = await frame.GetAttributeAsync("src");
            var html = await page.APIRequest.GetAsync(src!.StartsWith("http") ? src : _fixture.BaseUrl + src);
            var csp = html.Headers["content-security-policy"];
            Assert.Contains("sandbox", csp);
            Assert.Contains("img-src data:;", csp);
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

            // Archived and search.
            await page.Locator("fb-mail-view .mv-head [data-view='archived']").ClickAsync();
            await Assertions.Expect(page.Locator("fb-mail-view .mv-item", new() { HasText = "Old project" })).ToHaveCountAsync(1);
            await page.Locator("fb-mail-view .mv-head [data-view='archived']").ClickAsync();
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
}
