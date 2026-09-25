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
            // The account menu itself stays hidden in this fixture (it waits for
            // /me, which the injected test user has no profile for), so fire
            // the menu's selection the way a click on the item would.
            await page.EvaluateAsync(
                "() => document.getElementById('fb-account').dispatchEvent(new CustomEvent('sac:select', { detail: { action: 'unlock-secrets' } }))");
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
