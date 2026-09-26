using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

/// <summary>
/// Secret vault v2 end to end, in a real browser against the real host:
/// setup (passphrase + recovery words), the note on the server holding only
/// markers and ciphertext, unlocking after a reload with a wrong and a right
/// passphrase, unlocking with the recovery key, and ciphertext moved into
/// another note failing to decrypt (AAD binds it to its note).
/// One test on purpose: the steps share one vault and must run in order.
/// </summary>
[Collection(UiCollection.Name)]
public class VaultTests
{
    private const string Passphrase = "correct-horse-battery-staple-test";
    private const string Secret = "hunter2-vault-smoke";

    private readonly PlaywrightFixture _fixture;

    public VaultTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Vault_SetupEncryptUnlockRecover_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var api = page.APIRequest;
        var baseUrl = _fixture.BaseUrl;

        // The UI classes share one host: start from no vault, and leave none
        // behind — encrypted notes would pop the unlock dialog in whichever
        // test opens #/notes next.
        await ResetVaultAsync(api, baseUrl);
        string? noteId = null, otherId = null;
        try
        {
            var created = await api.PostAsync($"{baseUrl}/api/v1/notes", new APIRequestContextOptions
            {
                DataObject = new { title = "Vault smoke", content = "" },
            });
            noteId = (await created.JsonAsync())!.Value.GetProperty("id").GetString()!;
            var other = await api.PostAsync($"{baseUrl}/api/v1/notes", new APIRequestContextOptions
            {
                DataObject = new { title = "Vault victim", content = "" },
            });
            otherId = (await other.JsonAsync())!.Value.GetProperty("id").GetString()!;

            // ── Setup: writing the first secret opens the setup dialog. ──
            await OpenNoteAsync(page, baseUrl, "Vault smoke");
            await page.Locator("fb-notes-view #content").EvaluateAsync(
                $"e => {{ e.value = '# Vault smoke\\n\\n:::secret\\n{Secret}\\n:::end\\n'; e.dispatchEvent(new Event('change')); }}");

            var setup = page.Locator("sac-dialog[title='Set up secrets']");
            await setup.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await setup.Locator("input[name='pass']").FillAsync(Passphrase);
            await setup.Locator("input[name='confirm']").FillAsync(Passphrase);
            await setup.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();

            var recovery = page.Locator("sac-dialog[title='Your recovery key']");
            await recovery.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            var words = (await recovery.Locator(".fb-vault-words li").AllTextContentsAsync()).Select(w => w.Trim()).ToArray();
            Assert.Equal(24, words.Length);
            foreach (var name in new[] { "a", "b" })
            {
                var label = await recovery.Locator($"input[name='{name}']").EvaluateAsync<string>("i => i.closest('label').textContent");
                var position = int.Parse(Regex.Match(label, @"\d+").Value);
                await recovery.Locator($"input[name='{name}']").FillAsync(words[position - 1]);
            }
            await recovery.GetByRole(AriaRole.Button, new() { Name = "I wrote them down" }).ClickAsync();

            // Where the browser reports passkey (PRF) support, setup offers
            // one — not this test's business.
            var offer = page.Locator("sac-dialog[title='Unlock with a passkey too?']");
            try
            {
                await offer.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });
                await offer.GetByRole(AriaRole.Button, new() { Name = "Not now" }).ClickAsync();
            }
            catch (TimeoutException) { /* no passkey offer here */ }

            // ── On the server: markers and ciphertext only. ──
            var stored = await WaitForAsync(async () =>
            {
                var n = await GetNoteAsync(api, baseUrl, noteId);
                return n.GetProperty("content").GetString()!.Contains(":::secret#0:::end") ? n : (JsonElement?)null;
            });
            var raw = stored.GetRawText();
            Assert.DoesNotContain(Secret, raw);
            var contentSecret = stored.GetProperty("contentSecret").GetString()!;
            var envelope = JsonDocument.Parse(Convert.FromBase64String(contentSecret)).RootElement;
            Assert.Equal(2, envelope.GetProperty("v").GetInt32());

            // Move the ciphertext into another note — AAD must make it useless there.
            var put = await api.PutAsync($"{baseUrl}/api/v1/notes/{otherId}", new APIRequestContextOptions
            {
                DataObject = new { id = otherId, title = "Vault victim", content = "# Vault victim\n\n:::secret#0:::end\n", contentSecret },
            });
            Assert.True(put.Ok, $"planting ciphertext failed: {put.Status}");

            // ── Reload: the key is gone (memory only); a wrong passphrase is refused. ──
            await page.ReloadAsync();
            var unlock = page.Locator("sac-dialog[title='Unlock secrets']");
            await unlock.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await unlock.Locator("input[name='pass']").FillAsync("not-the-passphrase");
            await unlock.GetByRole(AriaRole.Button, new() { Name = "Unlock" }).ClickAsync();
            await Assertions.Expect(unlock.Locator(".fb-vault-error")).ToHaveTextAsync("Wrong passphrase.");
            await unlock.Locator("input[name='pass']").FillAsync(Passphrase);
            await unlock.GetByRole(AriaRole.Button, new() { Name = "Unlock" }).ClickAsync();
            await unlock.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 10000 });

            await OpenNoteAsync(page, baseUrl, "Vault smoke", navigate: false);
            Assert.Contains(Secret, await EditorValueAsync(page));

            await OpenNoteAsync(page, baseUrl, "Vault victim", navigate: false);
            var victim = await EditorValueAsync(page);
            Assert.DoesNotContain(Secret, victim);
            Assert.Contains("[decryption failed]", victim);

            // The list preview never shows a decrypted secret.
            var row = page.Locator(".nv-item", new PageLocatorOptions { HasText = "Vault smoke" }).First;
            Assert.DoesNotContain(Secret, await row.Locator(".nv-item-snippet").TextContentAsync() ?? "");

            // ── Lock: decrypted secrets leave the page at once, no reload. ──
            await OpenNoteAsync(page, baseUrl, "Vault smoke", navigate: false);
            await page.EvaluateAsync("() => fb.vault.lock()");
            await page.WaitForFunctionAsync(
                "() => !document.querySelector('fb-notes-view #content').value.includes('" + Secret + "')");
            var pill = page.Locator("#locked-pill");
            Assert.True(await pill.IsVisibleAsync());
            Assert.Equal("", await page.Locator("fb-notes-view #content").GetAttributeAsync("readonly"));

            // Locked, the block reads as an ordinary secret block — never the
            // raw storage marker.
            var lockedText = await EditorValueAsync(page);
            Assert.Contains(":::secret\nlocked — unlock to show\n:::end", lockedText);
            Assert.DoesNotContain("secret#", lockedText);

            // A tag change while locked saves the tag only: the placeholder
            // must never replace the real secret.
            var before = (await GetNoteAsync(api, baseUrl, noteId)).GetProperty("contentSecret").GetString();
            await page.EvaluateAsync(@"async () => {
                const v = document.querySelector('fb-notes-view');
                v.querySelector('#tag-input').value = ['locked-edit'];
                await v.flushSave();
            }");
            var afterTag = await WaitForAsync(async () =>
            {
                var n = await GetNoteAsync(api, baseUrl, noteId);
                return n.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == "locked-edit") ? n : (JsonElement?)null;
            });
            Assert.Equal(before, afterTag.GetProperty("contentSecret").GetString());
            Assert.Contains(":::secret#0:::end", afterTag.GetProperty("content").GetString());
            Assert.DoesNotContain("locked — unlock", afterTag.GetProperty("content").GetString());

            // The account menu now offers unlock, not lock — and unlocking
            // there shows the open note's secret without switching notes.
            await page.WaitForFunctionAsync(
                "() => document.querySelectorAll('#fb-account [data-action$=\"lock-secrets\"]').length === 1 && document.getElementById('fb-vault-item')?.dataset.action === 'unlock-secrets'");
            await page.Locator("#fb-account button[slot='trigger']").ClickAsync();
            await page.Locator("#fb-vault-item").ClickAsync();
            await unlock.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await unlock.Locator("input[name='pass']").FillAsync(Passphrase);
            await unlock.GetByRole(AriaRole.Button, new() { Name = "Unlock" }).ClickAsync();
            await page.WaitForFunctionAsync(
                "() => document.querySelector('fb-notes-view #content').value.includes('" + Secret + "')");
            await page.WaitForFunctionAsync(
                "() => document.querySelectorAll('#fb-account [data-action$=\"lock-secrets\"]').length === 1 && document.getElementById('fb-vault-item')?.dataset.action === 'lock-secrets'");

            // Lock again; this time the pill unlocks and brings the secret back.
            await page.EvaluateAsync("() => fb.vault.lock()");
            await Assertions.Expect(pill).ToBeVisibleAsync();
            await pill.ClickAsync();
            await unlock.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await unlock.Locator("input[name='pass']").FillAsync(Passphrase);
            await unlock.GetByRole(AriaRole.Button, new() { Name = "Unlock" }).ClickAsync();
            await page.WaitForFunctionAsync(
                "() => document.querySelector('fb-notes-view #content').value.includes('" + Secret + "')");
            Assert.False(await pill.IsVisibleAsync());

            // ── Lock + reload, then unlock with the recovery key. ──
            await page.EvaluateAsync("() => fb.vault.lock()");
            await page.ReloadAsync();
            await unlock.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await unlock.GetByRole(AriaRole.Button, new() { Name = "Use the recovery key instead" }).ClickAsync();
            await unlock.Locator("textarea[name='words']").FillAsync(string.Join(' ', words));
            await unlock.GetByRole(AriaRole.Button, new() { Name = "Unlock" }).ClickAsync();
            await unlock.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 10000 });

            await OpenNoteAsync(page, baseUrl, "Vault smoke", navigate: false);
            Assert.Contains(Secret, await EditorValueAsync(page));
        }
        finally
        {
            foreach (var id in new[] { noteId, otherId })
                if (id is not null) await api.DeleteAsync($"{baseUrl}/api/v1/notes/{id}");
            await ResetVaultAsync(api, baseUrl);
            await context.CloseAsync();
        }
    }

    /// <summary>
    /// Phases 2 + 3: set up from Settings → Secrets with a passkey (Chromium's
    /// virtual authenticator with PRF), unlock with it after a reload, use it
    /// to confirm a passphrase change, the last-recoverable-slot guard, and
    /// the auto-lock setting.
    /// </summary>
    [Fact]
    public async Task Vault_PasskeyAndSecretsSettings_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var api = page.APIRequest;
        // WebAuthn refuses an IP address as its domain; localhost is fine.
        var baseUrl = _fixture.BaseUrl.Replace("127.0.0.1", "localhost");
        const string NewPassphrase = "a-brand-new-passphrase-test";

        var cdp = await page.Context.NewCDPSessionAsync(page);
        await cdp.SendAsync("WebAuthn.enable", new Dictionary<string, object> { ["enableUI"] = false });
        await cdp.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["protocol"] = "ctap2",
                ["ctap2Version"] = "ctap2_1",
                ["transport"] = "internal",
                ["hasResidentKey"] = true,
                ["hasUserVerification"] = true,
                ["isUserVerified"] = true,
                ["automaticPresenceSimulation"] = true,
                ["hasPrf"] = true,
            },
        });

        await ResetVaultAsync(api, baseUrl);
        try
        {
            // ── Set up from the settings page, adding a passkey on the way. ──
            await page.GotoAsync(baseUrl + "/#/secrets");
            await page.GetByRole(AriaRole.Button, new() { Name = "Set up secrets" }).ClickAsync();
            var setup = page.Locator("sac-dialog[title='Set up secrets']");
            await setup.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await setup.Locator("input[name='pass']").FillAsync(Passphrase);
            await setup.Locator("input[name='confirm']").FillAsync(Passphrase);
            await setup.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
            await ConfirmRecoveryWordsAsync(page);

            var offer = page.Locator("sac-dialog[title='Unlock with a passkey too?']");
            await offer.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            await offer.GetByRole(AriaRole.Button, new() { Name = "Add a passkey" }).ClickAsync();
            await offer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 15000 });

            var rows = page.Locator("fb-secrets-settings-view .slot-row");
            await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 10000 });
            await Assertions.Expect(page.Locator(".slot-row[data-kind='passkey']")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("fb-secrets-settings-view .status-text")).ToContainTextAsync("Unlocked");

            // ── Reload: locked; the passkey unlocks. ──
            await page.ReloadAsync();
            await Assertions.Expect(page.Locator("fb-secrets-settings-view .status-text")).ToContainTextAsync("Locked");
            await page.GetByRole(AriaRole.Button, new() { Name = "Unlock", Exact = true }).First.ClickAsync();
            var unlock = page.Locator("sac-dialog[title='Unlock secrets']");
            await unlock.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await unlock.GetByRole(AriaRole.Button, new() { Name = "Unlock with a passkey" }).ClickAsync();
            await unlock.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 15000 });
            await Assertions.Expect(page.Locator("fb-secrets-settings-view .status-text")).ToContainTextAsync("Unlocked");
            Assert.True(await page.EvaluateAsync<bool>("() => fb.vault.isUnlocked()"));

            // ── Change the passphrase, confirming with the passkey. ──
            await page.Locator(".slot-row[data-kind='passphrase']").GetByRole(AriaRole.Button, new() { Name = "Change" }).ClickAsync();
            var confirm = page.Locator("sac-dialog[title=\"Confirm it's you\"]");
            await confirm.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await confirm.GetByRole(AriaRole.Button, new() { Name = "Unlock with a passkey" }).ClickAsync();
            var newPass = page.Locator("sac-dialog[title='New passphrase']");
            await newPass.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            await newPass.Locator("input[name='pass']").FillAsync(NewPassphrase);
            await newPass.Locator("input[name='confirm']").FillAsync(NewPassphrase);
            await newPass.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
            await newPass.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 15000 });
            // The new passphrase is derived and saved after the dialog closes.
            await Assertions.Expect(page.Locator("#sac-toast-stack").GetByText("Passphrase changed.").First).ToBeVisibleAsync(new() { Timeout = 15000 });

            // The old passphrase is refused now, the new one opens the vault.
            await page.EvaluateAsync("() => fb.vault.lock()");
            await page.EvaluateAsync("() => { fb.vault.ensureUnlocked().catch(() => {}); }");
            await unlock.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await unlock.Locator("input[name='pass']").FillAsync(Passphrase);
            await unlock.GetByRole(AriaRole.Button, new() { Name = "Unlock", Exact = true }).ClickAsync();
            await Assertions.Expect(unlock.Locator(".fb-vault-error")).ToHaveTextAsync("Wrong passphrase.", new() { Timeout = 10000 });
            await unlock.Locator("input[name='pass']").FillAsync(NewPassphrase);
            await unlock.GetByRole(AriaRole.Button, new() { Name = "Unlock", Exact = true }).ClickAsync();
            await unlock.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 10000 });

            // ── Remove: recovery is fine, then the passphrase is the last
            // recoverable slot and must stay (a passkey alone is no way in).
            foreach (var kind in new[] { "recovery", "passphrase" })
            {
                await page.Locator($".slot-row[data-kind='{kind}'] button.remove").ClickAsync();
                var ask = page.Locator("sac-dialog[open]");
                await ask.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
                await ask.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 5000 });
            }
            await Assertions.Expect(page.Locator(".slot-row[data-kind='recovery']")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".slot-row[data-kind='passphrase']")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#sac-toast-stack").GetByText("Keep at least a passphrase").First).ToBeVisibleAsync();

            // With the recovery key gone, the page offers a new one.
            await page.GetByRole(AriaRole.Button, new() { Name = "Create a recovery key" }).ClickAsync();
            var again = page.Locator("sac-dialog[title=\"Confirm it's you\"]");
            await again.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await again.GetByRole(AriaRole.Button, new() { Name = "Unlock with a passkey" }).ClickAsync();
            await ConfirmRecoveryWordsAsync(page);
            await Assertions.Expect(page.Locator(".slot-row[data-kind='recovery']")).ToHaveCountAsync(1, new() { Timeout = 15000 });

            // ── Auto-lock: saved on the user, applied to the session. ──
            await page.Locator("#autolock").SelectOptionAsync("60");
            await page.WaitForFunctionAsync("() => fb.vault.autoLockMinutes() === 60");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_secrets_settings.png"), FullPage = true });
            var me = (await (await api.GetAsync($"{baseUrl}/api/v1/me")).JsonAsync())!.Value;
            Assert.Equal(60, me.GetProperty("vaultAutoLockMinutes").GetInt32());
            await api.PatchAsync($"{baseUrl}/api/v1/me", new APIRequestContextOptions
            {
                DataObject = new Dictionary<string, object?> { ["vaultAutoLockMinutes"] = null },
            });
        }
        finally
        {
            await ResetVaultAsync(api, baseUrl);
            await context.CloseAsync();
        }
    }

    /// <summary>
    /// The slot-management edges: rename and remove a passkey, "Replace"
    /// retires the old recovery words, auto-lock really locks when its time
    /// is up (Playwright's clock, no real wait), the page in a space, and a
    /// browser without WebAuthn gets a hint instead of an "Add a passkey"
    /// button.
    /// </summary>
    [Fact]
    public async Task Vault_SlotManagementEdges_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Clock.InstallAsync(new ClockInstallOptions());
        var page = await context.NewPageAsync();
        var api = page.APIRequest;
        var baseUrl = _fixture.BaseUrl.Replace("127.0.0.1", "localhost");
        await AddVirtualAuthenticatorAsync(page);

        await ResetVaultAsync(api, baseUrl);
        try
        {
            await page.GotoAsync(baseUrl + "/#/secrets");
            await page.GetByRole(AriaRole.Button, new() { Name = "Set up secrets" }).ClickAsync();
            var setup = page.Locator("sac-dialog[title='Set up secrets']");
            await setup.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await setup.Locator("input[name='pass']").FillAsync(Passphrase);
            await setup.Locator("input[name='confirm']").FillAsync(Passphrase);
            await setup.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
            var oldWords = await ConfirmRecoveryWordsAsync(page);
            var offer = page.Locator("sac-dialog[title='Unlock with a passkey too?']");
            await offer.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            await offer.GetByRole(AriaRole.Button, new() { Name = "Add a passkey" }).ClickAsync();
            var passkeyRow = page.Locator(".slot-row[data-kind='passkey']");
            await Assertions.Expect(passkeyRow).ToHaveCountAsync(1, new() { Timeout = 15000 });

            // ── Rename the passkey in place. ──
            await passkeyRow.Locator("button.rename").ClickAsync();
            await passkeyRow.Locator(".slot-name input").FillAsync("Work laptop");
            await passkeyRow.Locator(".slot-name input").PressAsync("Enter");
            await Assertions.Expect(passkeyRow.Locator(".slot-name")).ToHaveTextAsync("Work laptop");

            // ── Replace the recovery key: the old words stop working. ──
            await page.Locator(".slot-row[data-kind='recovery']").GetByRole(AriaRole.Button, new() { Name = "Replace" }).ClickAsync();
            var confirm = page.Locator("sac-dialog[title=\"Confirm it's you\"]");
            await confirm.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await confirm.GetByRole(AriaRole.Button, new() { Name = "Unlock with a passkey" }).ClickAsync();
            var newWords = await ConfirmRecoveryWordsAsync(page);
            Assert.NotEqual(oldWords, newWords);
            await Assertions.Expect(page.Locator("#sac-toast-stack").GetByText("New recovery key saved").First).ToBeVisibleAsync(new() { Timeout = 15000 });
            await Assertions.Expect(page.Locator(".slot-row[data-kind='recovery']")).ToHaveCountAsync(1);

            await page.EvaluateAsync("() => fb.vault.lock()");
            await page.EvaluateAsync("() => { fb.vault.ensureUnlocked().catch(() => {}); }");
            var unlock = page.Locator("sac-dialog[title='Unlock secrets']");
            await unlock.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await unlock.GetByRole(AriaRole.Button, new() { Name = "Use the recovery key instead" }).ClickAsync();
            await unlock.Locator("textarea[name='words']").FillAsync(string.Join(' ', oldWords));
            await unlock.GetByRole(AriaRole.Button, new() { Name = "Unlock", Exact = true }).ClickAsync();
            await Assertions.Expect(unlock.Locator(".fb-vault-error")).ToHaveTextAsync("This recovery key doesn't open your secrets.", new() { Timeout = 10000 });
            await unlock.Locator("textarea[name='words']").FillAsync(string.Join(' ', newWords));
            await unlock.GetByRole(AriaRole.Button, new() { Name = "Unlock", Exact = true }).ClickAsync();
            await unlock.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 10000 });

            // ── Auto-lock: after its time without a secret operation, locked. ──
            Assert.True(await page.EvaluateAsync<bool>("() => fb.vault.isUnlocked()"));
            await page.Clock.FastForwardAsync("14:00");
            Assert.True(await page.EvaluateAsync<bool>("() => fb.vault.isUnlocked()"));
            await page.Clock.FastForwardAsync("01:05");
            await page.WaitForFunctionAsync("() => !fb.vault.isUnlocked()");
            await Assertions.Expect(page.Locator("fb-secrets-settings-view .status-text")).ToContainTextAsync("Locked");

            // ── Remove the passkey: passphrase and recovery key remain. ──
            await passkeyRow.Locator("button.remove").ClickAsync();
            var ask = page.Locator("sac-dialog[open]");
            await ask.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
            await Assertions.Expect(passkeyRow).ToHaveCountAsync(0, new() { Timeout = 10000 });
            await Assertions.Expect(page.Locator("fb-secrets-settings-view .slot-row")).ToHaveCountAsync(2);

            // ── In a space the page only points back to Personal. ──
            var space = await api.PostAsync($"{baseUrl}/api/v1/spaces", new APIRequestContextOptions
            {
                DataObject = new { name = "Secrets elsewhere " + Guid.NewGuid().ToString("N")[..6] },
            });
            var slug = (await space.JsonAsync())!.Value.GetProperty("slug").GetString()!;
            await page.GotoAsync($"{baseUrl}/#/space/{slug}/secrets");
            await Assertions.Expect(page.Locator("fb-secrets-settings-view")).ToContainTextAsync("Secrets are personal");
            await Assertions.Expect(page.Locator("fb-secrets-settings-view .slot-row")).ToHaveCountAsync(0);

            // ── No WebAuthn in the browser: a hint, never a dead button. ──
            var bare = await context.NewPageAsync();
            await bare.AddInitScriptAsync("delete window.PublicKeyCredential;");
            await bare.GotoAsync(baseUrl + "/#/secrets");
            await Assertions.Expect(bare.Locator("fb-secrets-settings-view")).ToContainTextAsync("Passkeys aren't available");
            await Assertions.Expect(bare.GetByRole(AriaRole.Button, new() { Name = "Add a passkey" })).ToHaveCountAsync(0);
        }
        finally
        {
            await ResetVaultAsync(api, baseUrl);
            await context.CloseAsync();
        }
    }

    private static async Task AddVirtualAuthenticatorAsync(IPage page)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        await cdp.SendAsync("WebAuthn.enable", new Dictionary<string, object> { ["enableUI"] = false });
        await cdp.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["protocol"] = "ctap2",
                ["ctap2Version"] = "ctap2_1",
                ["transport"] = "internal",
                ["hasResidentKey"] = true,
                ["hasUserVerification"] = true,
                ["isUserVerified"] = true,
                ["automaticPresenceSimulation"] = true,
                ["hasPrf"] = true,
            },
        });
    }

    private static async Task<string[]> ConfirmRecoveryWordsAsync(IPage page)
    {
        var recovery = page.Locator("sac-dialog[title='Your recovery key']");
        await recovery.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        var words = (await recovery.Locator(".fb-vault-words li").AllTextContentsAsync()).Select(w => w.Trim()).ToArray();
        Assert.Equal(24, words.Length);
        foreach (var name in new[] { "a", "b" })
        {
            var label = await recovery.Locator($"input[name='{name}']").EvaluateAsync<string>("i => i.closest('label').textContent");
            var position = int.Parse(Regex.Match(label, @"\d+").Value);
            await recovery.Locator($"input[name='{name}']").FillAsync(words[position - 1]);
        }
        await recovery.GetByRole(AriaRole.Button, new() { Name = "I wrote them down" }).ClickAsync();
        return words;
    }

    private static async Task OpenNoteAsync(IPage page, string baseUrl, string title, bool navigate = true)
    {
        if (navigate) await page.GotoAsync(baseUrl + "/#/notes");
        await page.Locator(".nv-item", new PageLocatorOptions { HasText = title }).First.ClickAsync();
        await page.WaitForFunctionAsync(
            $"() => (document.querySelector('fb-notes-view #content')?.value || '').startsWith('# {title}')");
    }

    private static Task<string> EditorValueAsync(IPage page)
        => page.Locator("fb-notes-view #content").EvaluateAsync<string>("e => e.value");

    private static async Task<JsonElement> GetNoteAsync(IAPIRequestContext api, string baseUrl, string id)
        => (await (await api.GetAsync($"{baseUrl}/api/v1/notes/{id}")).JsonAsync())!.Value;

    private static async Task ResetVaultAsync(IAPIRequestContext api, string baseUrl)
    {
        var vault = (await (await api.GetAsync($"{baseUrl}/api/v1/vault/")).JsonAsync())!.Value;
        // Passkeys first, then recovery, then the passphrase — the server
        // refuses to drop the last recoverable slot while others remain.
        var order = new[] { "passkey", "recovery", "passphrase" };
        foreach (var slot in vault.GetProperty("slots").EnumerateArray()
                     .OrderBy(s => Array.IndexOf(order, s.GetProperty("kind").GetString())))
        {
            var resp = await api.DeleteAsync($"{baseUrl}/api/v1/vault/slots/{slot.GetProperty("id").GetString()}");
            Assert.True(resp.Ok, $"vault reset failed: {resp.Status}");
        }
    }

    private static async Task<JsonElement> WaitForAsync(Func<Task<JsonElement?>> probe)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await probe() is { } hit) return hit;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("condition not met within 10s");
    }
}
