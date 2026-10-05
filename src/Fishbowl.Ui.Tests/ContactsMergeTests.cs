using System.Text.Json;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// Contacts saves send the whole contact, so the editor must hold what the
// server has: after merging a duplicate in, the next keystroke keeps the
// merged emails and phones. And an edit made just before leaving for
// another workspace is saved where it was made.
[Collection(UiCollection.Name)]
public class ContactsMergeTests
{
    private readonly PlaywrightFixture _fixture;

    public ContactsMergeTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Contacts_MergeThenEdit_KeepsMergedData_EditBeforeLeavingIsSaved_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true, BypassCSP = true });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        string? slug = null;
        try
        {
            async Task<JsonElement> Post(string path, object body)
            {
                var r = await page.APIRequest.PostAsync(_fixture.BaseUrl + path, new APIRequestContextOptions { DataObject = body });
                Assert.True(r.Ok, $"{path}: {r.Status} {await r.TextAsync()}");
                return (await r.JsonAsync())!.Value;
            }
            async Task<JsonElement> Contact(string id) =>
                (await (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/contacts/{id}")).JsonAsync())!.Value;
            async Task<JsonElement> WaitForRole(string id, string role)
            {
                for (var i = 0; i < 50; i++)
                {
                    var c = await Contact(id);
                    if (c.TryGetProperty("role", out var r) && r.GetString() == role) return c;
                    await Task.Delay(100, TestContext.Current.CancellationToken);
                }
                Assert.Fail($"role never became {role}");
                return default;
            }

            slug = (await Post("/api/v1/spaces", new { name = "People " + Guid.NewGuid().ToString("N")[..6] })).GetProperty("slug").GetString()!;
            await Post($"/api/v1/spaces/{slug}/contacts", new { kind = "person", firstName = "Ada", lastName = "Lovelace", emails = new[] { new { label = "work", value = "ada@one.test" } } });
            await Post($"/api/v1/spaces/{slug}/contacts", new { kind = "person", firstName = "Ada", lastName = "Lovelace", phones = new[] { new { label = "mobile", value = "+44 1" } } });

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/contacts");
            var view = page.Locator("fb-contacts-view");
            await Assertions.Expect(view.Locator(".cv-item")).ToHaveCountAsync(2, new() { Timeout = 15000 });
            await view.Locator(".cv-item").First.ClickAsync();
            await Assertions.Expect(view.Locator("#cv-editor")).ToBeVisibleAsync();

            // Merge the pair while one of them is open.
            await page.Locator("#fb-view-toolbar button[title='Find duplicates']").ClickAsync();
            var dialog = page.Locator("#cv-duplicates-dialog");
            await dialog.Locator("button[data-action='merge']").ClickAsync();
            await Assertions.Expect(view.Locator(".cv-item")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
            await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = 5000 });

            // The kept one is open; a keystroke there saves it with the merged lists.
            await Assertions.Expect(view.Locator("#cv-editor")).ToBeVisibleAsync();
            var kept = await view.EvaluateAsync<string>("v => v.selectedId");
            await view.Locator("#cv-editor input[data-key='role']").FillAsync("Mathematician");
            var saved = await WaitForRole(kept, "Mathematician");
            Assert.Contains("ada@one.test", saved.GetProperty("emails").ToString());
            Assert.Contains("+44 1", saved.GetProperty("phones").ToString());

            // An edit, then straight to the personal workspace: saved in the space.
            await view.Locator("#cv-editor input[data-key='role']").FillAsync("Countess");
            await page.EvaluateAsync("() => { location.hash = '#/contacts'; }");
            await WaitForRole(kept, "Countess");
            Assert.Empty(errors);
        }
        finally
        {
            if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }
}
