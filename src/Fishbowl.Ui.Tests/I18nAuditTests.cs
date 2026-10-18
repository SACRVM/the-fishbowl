using System.Text.Json;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The German UI, walked: every page, every window app, the system tabs, the
// profile, Your data, the tag manager, the palette, the About window, the
// mail windows, context menus and the login page — everything visible
// collected (text, title, aria-label, placeholder, label), step by step, into
// fishbowl-i18n-audit.json in the temp folder, for a person to read through.
// What an audit found in English stays out: the data grid's own strings (its
// German table sat in a satellite assembly no one loaded), the palette's
// colour names, an installed app's mode.
[Collection(UiCollection.Name)]
public class I18nAuditTests
{
    private readonly PlaywrightFixture _fixture;

    public I18nAuditTests(PlaywrightFixture fixture) => _fixture = fixture;

    // Every visible text node and the readable attributes, through open
    // shadow roots too.
    private const string Collect = @"() => {
        const out = new Set();
        const visible = (el) => {
            if (!el || !el.getClientRects().length) return false;
            const cs = getComputedStyle(el);
            return cs.visibility !== 'hidden' && cs.display !== 'none';
        };
        const walk = (root) => {
            const tw = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
            for (let n = tw.currentNode; n; n = tw.nextNode()) {
                if (n.nodeType === 3) {
                    const p = n.parentElement || (n.parentNode && n.parentNode.host);
                    const t = n.textContent.replace(/\s+/g, ' ').trim();
                    if (t && p && !['SCRIPT', 'STYLE', 'TEXTAREA'].includes(p.tagName) && visible(p)) out.add(t);
                } else if (n.nodeType === 1) {
                    for (const a of ['title', 'aria-label', 'placeholder', 'label', 'alt', 'add-label']) {
                        const v = n.getAttribute(a);
                        if (v && v.trim() && visible(n)) out.add('[' + a + '] ' + v.trim());
                    }
                    if (n.shadowRoot) walk(n.shadowRoot);
                }
            }
        };
        walk(document);
        return [...out];
    }";

    // Strings a German page must never show (each was once found there).
    private static readonly string[] English =
    [
        "Default", "blue", "orange", "red", "green", "purple", "yellow", "teal", "gray", "indigo",
        "Select all", "New row", "No rows", "Row", "0 rows", "No tags yet", "sandboxed", "Notifications", "Personal",
    ];

    [Fact]
    public async Task GermanUi_ShowsNoEnglish_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await context.NewPageAsync();
        // A translation table that doesn't parse leaves its whole area English.
        var pageErrors = new List<string>();
        page.PageError += (_, e) => pageErrors.Add(e);
        var b = _fixture.BaseUrl;
        var seen = new HashSet<string>();
        var steps = new Dictionary<string, List<string>>();
        async Task Step(string name, int settle = 700)
        {
            await page.WaitForTimeoutAsync(settle);
            var all = await page.EvaluateAsync<string[]>(Collect);
            steps[name] = all.Where(seen.Add).ToList();
        }
        async Task Escape() { await page.Keyboard.PressAsync("Escape"); await page.WaitForTimeoutAsync(300); }

        try
        {
            Assert.True((await page.APIRequest.PatchAsync(b + "/api/v1/me", new() { DataObject = new Dictionary<string, object?> { ["language"] = "de" } })).Ok);

            // Some data, so the views show rows, editors and their menus.
            var note = (await (await page.APIRequest.PostAsync(b + "/api/v1/notes", new() { DataObject = new { title = "Audit Notiz", content = "Inhalt", tags = new[] { "audit" } } })).JsonAsync())!.Value.GetProperty("id").GetString();
            await page.APIRequest.PostAsync(b + "/api/v1/todos", new() { DataObject = new { title = "Audit Aufgabe", dueAt = DateTime.UtcNow.AddDays(1).ToString("o") } });
            await page.APIRequest.PostAsync(b + "/api/v1/contacts", new() { DataObject = new { kind = "person", firstName = "Ada", lastName = "Audit", emails = new[] { new { label = "work", value = "ada@audit.test" } } } });
            await page.APIRequest.PostAsync(b + "/api/v1/events", new() { DataObject = new { title = "Audit Termin", startAt = DateTime.UtcNow.AddHours(2).ToString("o"), endAt = DateTime.UtcNow.AddHours(3).ToString("o") } });

            await page.GotoAsync(b + "/#/");
            await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "de", new() { Timeout = 10000 });
            await Step("desktop", 1500);
            await page.Locator("main.grid .tile").Nth(1).ClickAsync(new() { Button = MouseButton.Right });
            await Step("desktop-tile-menu");
            await Escape();
            await page.Keyboard.PressAsync("Control+k");
            await Step("palette");
            await Escape();

            foreach (var (hash, name) in new[] { ("#/notes", "notes"), ("#/todos", "todos"), ("#/calendar", "calendar"), ("#/contacts", "contacts"), ("#/files", "files") })
            {
                await page.GotoAsync(b + "/" + hash);
                await Step(name, 1200);
            }
            await page.GotoAsync(b + "/#/notes");
            await page.Locator(".nv-item", new() { HasText = "Audit Notiz" }).First.ClickAsync();
            await Step("notes-editor", 1000);
            await page.Locator(".nv-item", new() { HasText = "Audit Notiz" }).First.ClickAsync(new() { Button = MouseButton.Right });
            await Step("notes-row-menu");
            await Escape();
            await page.GotoAsync(b + "/#/todos");
            await page.Locator(".tv-item", new() { HasText = "Audit Aufgabe" }).First.ClickAsync();
            await Step("todos-editor", 1000);
            await page.GotoAsync(b + "/#/contacts");
            await page.Locator(".cv-item", new() { HasText = "Audit" }).First.ClickAsync();
            await Step("contacts-editor", 1000);
            await page.GotoAsync(b + "/#/calendar");
            await page.Locator(".cv-agenda-item", new() { HasText = "Audit Termin" }).First.ClickAsync();
            await Step("calendar-editor", 1000);

            foreach (var app in new[] { "messages", "trash", "spaces", "apps", "keys", "secrets", "users", "system" })
            {
                await page.GotoAsync(b + "/#/");
                await page.WaitForTimeoutAsync(800);
                await page.EvaluateAsync("(n) => fb.windowApps.open(n)", app);
                await Step("window-" + app, 1500);
                if (app == "system")
                {
                    var tabs = await page.Locator("fb-system-view sac-tab").AllAsync();
                    for (var i = 0; i < tabs.Count; i++)
                    {
                        await tabs[i].ClickAsync();
                        await Step($"system-tab-{i}", 800);
                    }
                }
            }

            await page.GotoAsync(b + "/#/");
            await page.WaitForTimeoutAsync(800);
            await page.EvaluateAsync("() => document.getElementById('fb-account').dispatchEvent(new CustomEvent('sac:select', { detail: { action: 'profile' } }))");
            await Step("profile");
            await Escape();
            await page.EvaluateAsync("() => fb.yourData.open()");
            await Step("your-data", 1200);
            await Escape();
            await page.EvaluateAsync("() => fb.tagManager.open()");
            await Step("tag-manager", 1000);
            await Escape();
            await page.EvaluateAsync("() => fb.about.open()");
            await Step("about", 1000);
            await Escape();
            await page.Locator("#fb-account").ClickAsync();
            await Step("account-menu");
            await Escape();

            // A space with a sample mailbox and a table.
            var res = await page.APIRequest.PostAsync(b + "/api/v1/spaces", new() { DataObject = new { name = "Audit " + Guid.NewGuid().ToString("N")[..6] } });
            var slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString()!;
            var mail = $"{b}/api/v1/spaces/{slug}/mail";
            await page.APIRequest.PostAsync($"{mail}/accounts", new()
            {
                DataObject = new { provider = "custom", name = "Sample", address = $"pw-{Guid.NewGuid():N}@fishbowl.test", password = "test-password", imapHost = "imap.fishbowl.test", smtpHost = "smtp.fishbowl.test" },
            });
            await page.APIRequest.PostAsync($"{b}/api/v1/spaces/{slug}/tables", new()
            {
                DataObject = new { name = "projekte", description = "Projekte", columns = new object[] { new { name = "status", type = "choice", options = new[] { "offen", "fertig" } }, new { name = "frist", type = "date" } } },
            });
            await page.GotoAsync($"{b}/#/space/{slug}/mail");
            await Assertions.Expect(page.Locator("fb-mail-view .mv-item")).Not.ToHaveCountAsync(0, new() { Timeout = 30000 });
            await Step("mail", 1000);
            await page.Locator("fb-mail-view .mv-item").First.ClickAsync(new() { Force = true });
            await Step("mail-thread", 1500);
            await page.Locator("fb-mail-view .mv-item").First.ClickAsync(new() { Button = MouseButton.Right, Force = true });
            await Step("mail-row-menu");
            await Escape();
            foreach (var tab in new[] { "archived", "drafts", "spam" })
            {
                var tabEl = page.Locator($"fb-mail-view #mv-tabs sac-tab[name='{tab}']");
                if (!await tabEl.IsVisibleAsync()) continue;
                await tabEl.ClickAsync(new() { Force = true });
                await Step("mail-tab-" + tab, 1500);
            }
            await page.Locator("fb-mail-view #mv-tabs sac-tab[name='list']").ClickAsync(new() { Force = true });
            await page.Locator("#mv-rules").ClickAsync();
            await Step("mail-rules", 1000);
            await page.Locator("#fb-mail-rules .wa-bar button").ClickAsync();
            await Step("mail-rule-editor", 1000);
            await Escape();
            await Escape();
            await page.Locator("#mv-accounts").ClickAsync();
            await Step("mail-accounts", 1000);
            await Escape();
            await page.EvaluateAsync("() => document.querySelectorAll('sac-window[open]').forEach(w => w.close?.())");
            await page.Locator("fb-mail-view #mv-new").ClickAsync(new() { Force = true });
            await Step("mail-compose", 1000);
            await page.GotoAsync($"{b}/#/space/{slug}/tables/projekte");
            await Step("tables", 1500);
            Assert.Contains("Neue Zeile", steps["tables"]);
            await page.GotoAsync($"{b}/#/space/{slug}/");
            await Step("space-desktop", 1500);

            // The server-rendered sign-in page, in the language the browser keeps.
            await page.GotoAsync(b + "/login");
            await Step("login", 800);

            if (note is not null) await page.APIRequest.DeleteAsync($"{b}/api/v1/notes/{note}");
            await page.APIRequest.DeleteAsync($"{b}/api/v1/spaces/{slug}?archive=false");

            var english = steps.SelectMany(kv => kv.Value.Select(text => (Step: kv.Key, Text: text)))
                .Where(x => English.Contains(x.Text.StartsWith('[') && x.Text.Contains("] ") ? x.Text[(x.Text.IndexOf("] ") + 2)..] : x.Text))
                .Select(x => $"{x.Step}: {x.Text}").ToList();
            Assert.True(english.Count == 0, "English in the German UI:\n" + string.Join("\n", english));
            Assert.Empty(pageErrors);
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "fishbowl-i18n-audit.json"),
                JsonSerializer.Serialize(steps, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            await page.APIRequest.PatchAsync(b + "/api/v1/me", new() { DataObject = new Dictionary<string, object?> { ["language"] = null } });
            await context.CloseAsync();
        }
    }
}
