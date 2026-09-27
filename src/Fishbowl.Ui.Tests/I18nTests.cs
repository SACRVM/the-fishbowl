using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// EN/DE, phase 1: the language is a per-user setting in the profile window
// (users.language, null = automatic), independent of the date format. The
// shell, the desktop, the palette and Messages speak it; a reload keeps it,
// and another browser of the same user gets it from the server. Every test
// here puts the shared test user back on automatic — the fixture's browser
// runs English, so the other UI tests stay English.
[Collection(UiCollection.Name)]
public class I18nTests
{
    private readonly PlaywrightFixture _fixture;

    public I18nTests(PlaywrightFixture fixture) => _fixture = fixture;

    private async Task<(IBrowserContext Context, IPage Page, List<string> Errors)> OpenAsync()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        page.Console += (_, m) => { if (m.Type == "error" && !m.Text.StartsWith("Failed to load resource")) errors.Add(m.Text); };
        return (context, page, errors);
    }

    [Fact]
    public async Task Language_SwitchToGerman_ShellDesktopPalette_PersistsAcrossReloadAndBrowsers_Test()
    {
        var (context, page, errors) = await OpenAsync();
        IBrowserContext? other = null;
        try
        {
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            // English to start with (automatic, English browser).
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[data-key='builtin:notes'] h2")).ToHaveTextAsync("Notes", new() { Timeout = 5000 });

            // Profile → Language → Deutsch.
            await page.Locator("#fb-account button[slot='trigger']").ClickAsync();
            await page.Locator("#fb-account [data-action='profile']").ClickAsync();
            var language = page.Locator("#fb-language");
            await Assertions.Expect(language).ToHaveValueAsync("auto");
            await language.SelectOptionAsync("de");

            // The desktop remounts in German: tiles, the tagline.
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[data-key='builtin:notes'] h2")).ToHaveTextAsync("Notizen", new() { Timeout = 5000 });
            await Assertions.Expect(page.Locator("fb-hub-view .intro-text")).ToHaveTextAsync("Dein Gedächtnis lebt hier. Du nicht.");
            // The profile window re-rendered in German, the date format untouched.
            await Assertions.Expect(page.Locator("label[for='fb-language']")).ToHaveTextAsync("Sprache");
            await Assertions.Expect(page.Locator("#fb-date-format")).ToHaveValueAsync("iso");
            // The shell's own strings.
            await Assertions.Expect(page.Locator("#fb-account [data-action='profile']")).ToContainTextAsync("Profil");
            await Assertions.Expect(page.Locator("#fb-context .fb-context-label")).ToHaveTextAsync("Persönlich");
            await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "de");

            // The palette: groups and commands in German.
            await page.Keyboard.PressAsync("Escape");
            await page.Keyboard.PressAsync("Control+k");
            var palette = page.Locator("sac-command-palette");
            await palette.Locator("input").FillAsync("Neue Notiz");
            await Assertions.Expect(palette.Locator("[role='option']").First).ToContainTextAsync("Neue Notiz", new() { Timeout = 5000 });
            await page.Keyboard.PressAsync("Escape");

            // The burger: route labels, Home is "Start".
            await page.GotoAsync(_fixture.BaseUrl + "/#/messages");
            await Assertions.Expect(page.Locator("fb-messages-view h1")).ToHaveTextAsync("Nachrichten", new() { Timeout = 5000 });
            var navText = await page.Locator("#fb-nav").EvaluateAsync<string>("n => n.shadowRoot.textContent");
            Assert.Contains("Notizen", navText);
            Assert.Contains("Kalender", navText);
            await Assertions.Expect(page.Locator("#fb-home-link span")).ToHaveTextAsync("Start");

            // A reload keeps it (the server's setting).
            await page.ReloadAsync();
            await Assertions.Expect(page.Locator("fb-messages-view h1")).ToHaveTextAsync("Nachrichten", new() { Timeout = 5000 });

            // Another browser of the same user: German from the server, not
            // from this browser's localStorage.
            other = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
            var page2 = await other.NewPageAsync();
            await page2.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(page2.Locator("fb-hub-view a.tile[data-key='builtin:todos'] h2")).ToHaveTextAsync("Aufgaben", new() { Timeout = 5000 });

            // Back to automatic: English again, in place.
            await page2.Locator("#fb-account button[slot='trigger']").ClickAsync();
            await page2.Locator("#fb-account [data-action='profile']").ClickAsync();
            await page2.Locator("#fb-language").SelectOptionAsync("auto");
            await Assertions.Expect(page2.Locator("fb-hub-view a.tile[data-key='builtin:todos'] h2")).ToHaveTextAsync("Todos", new() { Timeout = 5000 });
            Assert.Empty(errors);
        }
        finally
        {
            await page.APIRequest.PatchAsync(_fixture.BaseUrl + "/api/v1/me", new APIRequestContextOptions
            {
                DataObject = new Dictionary<string, object?> { ["language"] = null },
            });
            if (other is not null) await other.CloseAsync();
            await context.CloseAsync();
        }
    }
}

// Every fb.t / data-t key the translated files use has a German entry — a
// missing one would show English in the middle of a German page. Static:
// reads the sources, no browser.
public class I18nKeyTableTests
{
    // Phase 1 of EN/DE; later phases add their files here.
    private static readonly string[] Translated =
    {
        "index.html",
        "js/lib/shell.js",
        "js/lib/desktop.js",
        "js/lib/desktop-apps.js",
        "js/lib/accounts.js",
        "js/views/fb-hub-view.js",
        "js/views/fb-messages-view.js",
    };

    private static string Resources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Fishbowl.Data", "Resources")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Fishbowl.Data", "Resources");
    }

    [Fact]
    public void EveryUsedKey_HasAGermanEntry()
    {
        var root = Resources();
        var de = File.ReadAllText(Path.Combine(root, "js", "i18n", "de.js"));
        var table = Regex.Matches(de, "^\\s*\"([^\"]+)\"\\s*:", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();

        var used = new SortedSet<string>();
        foreach (var rel in Translated)
        {
            var text = File.ReadAllText(Path.Combine(root, rel));
            foreach (Match m in Regex.Matches(text, "fb\\.t\\(\"(fb\\.[^\"]+)\"")) used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, "data-t(?:-[a-z]+)?=\"(fb\\.[^\"$]+)\"")) used.Add(m.Groups[1].Value);
        }
        // Keys built at run time: the built-in apps, Go entries, routes.
        foreach (var app in new[] { "notes", "todos", "calendar", "files", "messages", "users", "settings", "system" })
        {
            used.Add($"fb.app.{app}.name");
            used.Add($"fb.app.{app}.desc");
        }
        foreach (var go in new[] { "desktop", "spaces", "apps", "keys", "secrets" }) used.Add($"fb.go.{go}");
        foreach (var route in new[] { "home", "notes", "todos", "calendar", "files", "messages", "spaces", "apps", "keys", "secrets", "data", "admin-users", "admin-settings", "admin-system" })
            used.Add($"fb.route.{route}");
        used.Add("fb.shell.lock-secrets");
        used.Add("fb.shell.unlock-secrets");

        var missing = used.Where(k => !table.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "no German entry for: " + string.Join(", ", missing));
    }
}
