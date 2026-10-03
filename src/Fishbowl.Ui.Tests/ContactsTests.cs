using System.Text.Json;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The Contacts app (space-apps spec, phase 6): people and organisations in a
// space — made from the "+" menu, edited in place (saved as you type), a
// person put into an organisation, which then lists them.
[Collection(UiCollection.Name)]
public class ContactsTests
{
    private readonly PlaywrightFixture _fixture;

    public ContactsTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Contacts_PersonInOrganisation_SavedAsYouType_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        string? slug = null;
        try
        {
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "People " + Guid.NewGuid().ToString("N")[..6] } });
            slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString();
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/contacts");
            var view = page.Locator("fb-contacts-view");
            await Assertions.Expect(view.Locator("#cv-items")).ToContainTextAsync("No contacts yet.", new() { Timeout = 15000 });

            async Task New(string kind)
            {
                await view.Locator("#cv-new .icon-btn").ClickAsync();
                await view.Locator($"#cv-new button[data-action='{kind}']").ClickAsync();
                await Assertions.Expect(view.Locator("#cv-editor")).ToBeVisibleAsync();
            }

            // An organisation, renamed in place.
            await New("organisation");
            var name = view.Locator("#cv-editor input[data-key='name']");
            await name.FillAsync("Acme GmbH");
            await Assertions.Expect(view.Locator(".cv-item.selected")).ToContainTextAsync("Acme GmbH", new() { Timeout = 5000 });

            // A person in it, with an email.
            await New("person");
            await view.Locator("#cv-editor input[data-key='firstName']").FillAsync("Ada");
            await view.Locator("#cv-editor input[data-key='lastName']").FillAsync("Lovelace");
            await view.Locator("#cv-org").EvaluateAsync("(s, v) => { s.value = v; s.dispatchEvent(new Event('change', { bubbles: true })); }",
                await page.EvaluateAsync<string>("() => document.querySelector('fb-contacts-view').contacts.find(c => c.kind === 'organisation').id"));
            await view.Locator("#cv-editor button[data-add='emails']").ClickAsync();
            await view.Locator("#cv-emails input[data-f='value']").FillAsync("ada@acme.test");
            await Assertions.Expect(view.Locator(".cv-item.selected")).ToContainTextAsync("Ada Lovelace", new() { Timeout = 5000 });
            await Assertions.Expect(view.Locator(".cv-item.selected")).ToContainTextAsync("Acme GmbH");
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "contacts-person.png") });

            // The server has it; the organisation lists its person.
            var list = (await (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/contacts")).JsonAsync())!.Value;
            var ada = list.EnumerateArray().Single(c => c.GetProperty("kind").GetString() == "person");
            Assert.Equal("ada@acme.test", ada.GetProperty("email").GetString());
            await view.Locator(".cv-item").Filter(new() { Has = page.Locator(".cv-item-name", new() { HasTextString = "Acme GmbH" }) }).ClickAsync();
            await Assertions.Expect(view.Locator("#cv-links")).ToContainTextAsync("Ada Lovelace");
            Assert.Empty(errors);
        }
        finally
        {
            if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }
}
