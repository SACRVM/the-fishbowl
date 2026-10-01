using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// EN/DE, phase 3: the settings pages, the admin pages, the vault dialogs,
// server error codes and the server-rendered sign-in pages speak German when
// the user (or, before sign-in, the browser) chose it. Every test puts the
// shared test user back on automatic, so the other UI tests stay English.
[Collection(UiCollection.Name)]
public class I18nSettingsTests
{
    private const string TestUser = "test-internal-id";
    private readonly PlaywrightFixture _fixture;

    public I18nSettingsTests(PlaywrightFixture fixture) => _fixture = fixture;

    private static string Shots()
    {
        var dir = Path.Combine(Path.GetTempPath(), "i18n3-final");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task SetLanguageAsync(IPage page, string? language) =>
        Assert.True((await page.APIRequest.PatchAsync(_fixture.BaseUrl + "/api/v1/me", new APIRequestContextOptions
        {
            DataObject = new Dictionary<string, object?> { ["language"] = language },
        })).Ok);

    // A validation refusal from the server (code + arguments) reads German
    // under its row: "0 to 23" comes back as "von 0 bis 23".
    [Fact]
    public async Task SystemSettings_ValidationError_InGerman_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await context.NewPageAsync();
        try
        {
            await SetLanguageAsync(page, "de");
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "system", tab: "digest");
            var hour = page.Locator("fb-system-view .cfg-row[data-key='Digest:Hour']");
            await Assertions.Expect(hour).ToBeVisibleAsync(new() { Timeout = 5000 });
            await hour.Locator("input").FillAsync("30");
            await hour.GetByRole(AriaRole.Button, new() { Name = "Speichern" }).ClickAsync();
            await Assertions.Expect(hour.Locator(".cfg-error"))
                .ToHaveTextAsync("Gib eine ganze Zahl von 0 bis 23 ein.", new() { Timeout = 5000 });

            await page.Locator("fb-system-view sac-tab[name='apps']").ClickAsync();
            var origins = page.Locator("fb-system-view .cfg-row[data-key='Apps:AllowedOrigins']");
            await origins.Locator("input").FillAsync("ftp://nope");
            await origins.GetByRole(AriaRole.Button, new() { Name = "Speichern" }).ClickAsync();
            await Assertions.Expect(origins.Locator(".cfg-error"))
                .ToHaveTextAsync("Kein https-Ursprung (nur Schema und Host): ftp://nope", new() { Timeout = 5000 });
            var dir = Path.Combine(Path.GetTempPath(), "err-final");
            Directory.CreateDirectory(dir);
            await origins.ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "desk-system-settings-error.png") });
        }
        finally
        {
            await SetLanguageAsync(page, null);
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task SettingsAndAdminPages_InGerman_Test()
    {
        foreach (var (w, h, tag) in new[] { (1400, 900, "desk"), (390, 800, "phone") })
        {
            var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new ViewportSize { Width = w, Height = h },
                IsMobile = tag == "phone",
                HasTouch = tag == "phone",
            });
            var page = await context.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, e) => errors.Add(e);
            try
            {
                await SetLanguageAsync(page, "de");
                var pages = new (string App, string Title, string Name)[]
                {
                    ("spaces", "Spaces", "spaces"),
                    ("keys", "API-Schlüssel", "keys"),
                    ("secrets", "Geheimnisse", "secrets"),
                    ("apps", "Apps", "apps"),
                    ("users", "Benutzer", "users"),
                    ("system", "System", "system"),
                };
                foreach (var (app, title, name) in pages)
                {
                    var win = await WindowApp.OpenAsync(page, _fixture.BaseUrl, app);
                    await Assertions.Expect(win).ToHaveAttributeAsync("title", title, new() { Timeout = 8000 });
                    await page.WaitForTimeoutAsync(600);
                    await page.ScreenshotAsync(new() { Path = Path.Combine(Shots(), $"{tag}-{name}.png"), FullPage = tag == "desk" });
                }

                // Samples beyond the headings.
                await WindowApp.OpenAsync(page, _fixture.BaseUrl, "spaces");
                await Assertions.Expect(page.Locator("#fb-win-spaces #create-btn")).ToHaveTextAsync("Space anlegen", new() { Timeout = 5000 });
                await WindowApp.OpenAsync(page, _fixture.BaseUrl, "keys");
                await Assertions.Expect(page.Locator("#fb-win-keys #create-btn")).ToHaveTextAsync("Schlüssel anlegen", new() { Timeout = 5000 });
                await page.EvaluateAsync("() => fb.systemApp.open('info')");
                await Assertions.Expect(page.Locator("#fb-win-system")).ToHaveAttributeAsync("title", "System");
                await Assertions.Expect(page.Locator("[data-key='version'] .fb-row-name")).ToHaveTextAsync("Version", new() { Timeout = 5000 });
                await Assertions.Expect(page.Locator("[data-key='maintenance'] .fb-row-name")).ToHaveTextAsync("Tägliche Wartung");
                await Assertions.Expect(page.Locator("fb-system-view sac-tab[name='sign-in']")).ToHaveTextAsync("Anmeldung");
                await page.Locator("fb-system-view sac-tab[name='sign-in']").ClickAsync();
                await Assertions.Expect(page.Locator("[data-key='Auth:SignUp'] .cfg-label")).ToHaveTextAsync("Neue Konten", new() { Timeout = 5000 });
                await Assertions.Expect(page.Locator("[data-key='Auth:SignUp'] .cfg-key")).ToHaveTextAsync("Auth:SignUp");

                // Users: the "…" menu and the delete dialog of another account.
                if (tag == "desk")
                {
                    var other = "i18n-" + Guid.NewGuid().ToString("N")[..6];
                    var created = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/admin/users", new APIRequestContextOptions
                    {
                        DataObject = new { username = other },
                    });
                    Assert.True(created.Ok, await created.TextAsync());
                    var otherId = (await created.JsonAsync())!.Value.GetProperty("id").GetString()!;
                    try
                    {
                        await WindowApp.OpenAsync(page, _fixture.BaseUrl, "users");
                        var row = page.Locator($".user-row[data-user-id='{otherId}']");
                        await row.Locator("sac-menu button[slot='trigger']").ClickAsync(new() { Timeout = 8000 });
                        await Assertions.Expect(row.Locator("sac-menu [data-action='delete']")).ToContainTextAsync("Löschen");
                        await page.ScreenshotAsync(new() { Path = Path.Combine(Shots(), "desk-users-menu.png") });
                        await row.Locator("sac-menu [data-action='delete']").ClickAsync();
                        var dlg = page.Locator("sac-dialog .fb-user-delete");
                        await Assertions.Expect(dlg).ToContainTextAsync("Daten vorher archivieren", new() { Timeout = 5000 });
                        await page.WaitForTimeoutAsync(400);
                        await page.ScreenshotAsync(new() { Path = Path.Combine(Shots(), "desk-users-delete.png") });
                        await page.Keyboard.PressAsync("Escape");
                    }
                    finally
                    {
                        await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/admin/users/{otherId}?archive=false");
                    }

                    // A known 400 from the server, in German: blocking yourself.
                    await WindowApp.OpenAsync(page, _fixture.BaseUrl, "users");
                    await Assertions.Expect(page.Locator("#fb-win-users")).ToHaveAttributeAsync("title", "Benutzer", new() { Timeout = 5000 });
                    var text = await page.EvaluateAsync<string>(@"async (id) => {
                        try { await fb.api.admin.block(id); return 'no error'; }
                        catch (err) { return fb.errors.text(err, 'fallback'); }
                    }", TestUser);
                    Assert.Equal("Du kannst dich nicht selbst sperren.", text);
                    // A code with no sentence: last-admin, straight from the table.
                    var code = await page.EvaluateAsync<string>(
                        "() => fb.errors.text({ status: 409, body: JSON.stringify({ error: 'last-admin' }) }, 'x')");
                    Assert.StartsWith("Das ist der letzte Admin", code);
                }
                Assert.Empty(errors);
            }
            finally
            {
                await SetLanguageAsync(page, null);
                await context.CloseAsync();
            }
        }
    }

    [Fact]
    public async Task VaultDialogs_InGerman_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await context.NewPageAsync();
        try
        {
            await SetLanguageAsync(page, "de");
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "secrets");
            await Assertions.Expect(page.Locator("#fb-win-secrets")).ToHaveAttributeAsync("title", "Geheimnisse", new() { Timeout = 8000 });
            // The unlock dialog, drawn without a vault behind it: the dialog
            // builder alone, never submitted.
            var vaultState = await page.EvaluateAsync<bool>("async () => (await fb.vault.status()).initialized");
            if (!vaultState)
            {
                await page.Locator("#setup").ClickAsync();
                var setup = page.Locator("sac-dialog[title='Geheimnisse einrichten']");
                await Assertions.Expect(setup).ToBeVisibleAsync(new() { Timeout = 5000 });
                await Assertions.Expect(setup).ToContainTextAsync("Wiederholen");
                await page.WaitForTimeoutAsync(400);
                await page.ScreenshotAsync(new() { Path = Path.Combine(Shots(), "desk-vault-setup.png") });
                await setup.GetByRole(AriaRole.Button, new() { Name = "Abbrechen" }).ClickAsync();
            }
        }
        finally
        {
            await SetLanguageAsync(page, null);
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task SignInPages_FollowTheStoredLanguage_Test()
    {
        foreach (var (w, h, tag) in new[] { (1400, 900, "desk"), (390, 800, "phone") })
        {
            var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new ViewportSize { Width = w, Height = h },
            });
            // The SPA mirrors the choice into localStorage; before sign-in
            // that is all these pages have.
            await context.AddInitScriptAsync("try { localStorage.setItem('sac-lang', 'de'); } catch {}");
            var page = await context.NewPageAsync();
            try
            {
                await page.GotoAsync(_fixture.BaseUrl + "/login");
                await Assertions.Expect(page.Locator(".tagline").First).ToHaveTextAsync("Dein Gedächtnis lebt hier. Du nicht.", new() { Timeout = 5000 });
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "de");
                await page.ScreenshotAsync(new() { Path = Path.Combine(Shots(), $"{tag}-login.png") });

                await page.GotoAsync(_fixture.BaseUrl + "/login?authError=" + Uri.EscapeDataString("This account was deleted."));
                await Assertions.Expect(page.Locator("#error")).ToHaveTextAsync("Dieses Konto wurde gelöscht.");

                // The pending page itself (the injected test user is active,
                // so /me is answered as a pending account would see it).
                await page.RouteAsync("**/api/v1/me", r => r.FulfillAsync(new RouteFulfillOptions
                {
                    ContentType = "application/json",
                    Body = "{\"state\":\"pending\",\"email\":\"ada@example.com\"}",
                }));
                await page.GotoAsync(_fixture.BaseUrl + "/pending.html");
                await Assertions.Expect(page.Locator("#pending-who")).ToHaveTextAsync("Angemeldet als ada@example.com.", new() { Timeout = 5000 });
                await Assertions.Expect(page.Locator("#pending-check")).ToHaveTextAsync("Erneut prüfen", new() { Timeout = 5000 });
                await page.ScreenshotAsync(new() { Path = Path.Combine(Shots(), $"{tag}-pending.png") });
            }
            finally
            {
                await context.CloseAsync();
            }
        }

        // Without a stored choice an English browser stays English.
        var en = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        try
        {
            var page = await en.NewPageAsync();
            await page.GotoAsync(_fixture.BaseUrl + "/login");
            await Assertions.Expect(page.Locator(".tagline").First).ToHaveTextAsync("Your memory lives here. You don't.", new() { Timeout = 5000 });
        }
        finally
        {
            await en.CloseAsync();
        }
    }
}
