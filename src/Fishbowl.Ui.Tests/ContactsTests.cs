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

            // An address on two lines: the street, then postal code, city, region
            // and country under it, starting on the street's edge.
            await view.Locator("#cv-editor button[data-add='addresses']").ClickAsync();
            var street = (await view.Locator("#cv-addresses input[data-f='street']").BoundingBoxAsync())!;
            var postal = (await view.Locator("#cv-addresses input[data-f='postalCode']").BoundingBoxAsync())!;
            var country = (await view.Locator("#cv-addresses input[data-f='country']").BoundingBoxAsync())!;
            Assert.True(postal.Y > street.Y + street.Height, $"postal code at {postal.Y}, street ends at {street.Y + street.Height}");
            Assert.Equal(street.X, postal.X, 1);
            Assert.Equal(street.X + street.Width, country.X + country.Width, 1);
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "contacts-person.png") });

            // The server has it; the organisation lists its person.
            var list = (await (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/contacts")).JsonAsync())!.Value;
            var ada = list.EnumerateArray().Single(c => c.GetProperty("kind").GetString() == "person");
            Assert.Equal("ada@acme.test", ada.GetProperty("email").GetString());
            await view.Locator(".cv-item").Filter(new() { Has = page.Locator(".cv-item-name", new() { HasTextString = "Acme GmbH" }) }).ClickAsync();
            // The organisation lists its people at the end; a click opens the person.
            var person = view.Locator("#cv-links .cv-link-row", new() { HasText = "Ada Lovelace" });
            await Assertions.Expect(person).ToContainTextAsync("ada@acme.test");
            await person.ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "contacts-org.png") });
            await person.ClickAsync();
            await Assertions.Expect(view.Locator(".cv-item.selected")).ToContainTextAsync("Ada Lovelace");

            // A person's name on one line.
            var first = (await view.Locator("#cv-editor input[data-key='firstName']").BoundingBoxAsync())!;
            var last = (await view.Locator("#cv-editor input[data-key='lastName']").BoundingBoxAsync())!;
            Assert.Equal(first.Y, last.Y, 1);
            Assert.Empty(errors);
        }
        finally
        {
            if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }

    // Marking like Files: Ctrl+click (the open contact comes along), Shift+click
    // a range, Esc lets go; the header deletes the marked ones after asking —
    // an organisation together with its person — and a row's own trash deletes one.
    [Fact]
    public async Task Contacts_MarkSeveral_AndDelete_Test()
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
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Marks " + Guid.NewGuid().ToString("N")[..6] } });
            slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString();
            var api = $"{_fixture.BaseUrl}/api/v1/spaces/{slug}/contacts";
            var acme = (await (await page.APIRequest.PostAsync(api, new() { DataObject = new { kind = "organisation", name = "Acme" } })).JsonAsync())!.Value.GetProperty("id").GetString();
            foreach (var (first, org) in new[] { ("Ada", (string?)null), ("Bob", acme), ("Cy", null), ("Di", null) })
                await page.APIRequest.PostAsync(api, new() { DataObject = new { kind = "person", firstName = first, lastName = "Test", organisationId = org } });

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/contacts");
            var view = page.Locator("fb-contacts-view");
            ILocator Row(string name) => view.Locator(".cv-item").Filter(new() { Has = page.Locator(".cv-item-name", new() { HasTextString = name }) });
            var marked = view.Locator(".cv-item.marked");
            var title = view.Locator("#cv-head-title");
            await Assertions.Expect(view.Locator(".cv-item")).ToHaveCountAsync(5, new() { Timeout = 15000 });

            // Ada open, Ctrl+click Bob: both marked.
            await Row("Ada Test").ClickAsync();
            await Row("Bob Test").ClickAsync(new() { Modifiers = new[] { KeyboardModifier.Control } });
            await Assertions.Expect(marked).ToHaveCountAsync(2);
            await Assertions.Expect(title).ToHaveTextAsync("2 selected");
            await Assertions.Expect(view.Locator("#cv-new")).ToBeHiddenAsync();

            // Shift+click Di: the range Bob … Di instead.
            await Row("Di Test").ClickAsync(new() { Modifiers = new[] { KeyboardModifier.Shift } });
            await Assertions.Expect(marked).ToHaveCountAsync(3);
            await Assertions.Expect(Row("Ada Test")).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("marked"));
            await Row("Cy Test").HoverAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "contacts-marking.png") });

            // Esc lets go.
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(marked).ToHaveCountAsync(0);
            await Assertions.Expect(title).ToHaveTextAsync("All contacts");

            // Acme and its person Bob, deleted together from the header.
            await Row("Acme").ClickAsync();
            await Row("Bob Test").ClickAsync(new() { Modifiers = new[] { KeyboardModifier.Control } });
            await view.Locator("#cv-delete-marked").ClickAsync();
            await page.Locator("sac-dialog[title='Delete 2 contacts?']").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
            await Assertions.Expect(view.Locator(".cv-item")).ToHaveCountAsync(3, new() { Timeout = 10000 });
            await Assertions.Expect(Row("Acme")).ToHaveCountAsync(0);
            await Assertions.Expect(view.Locator("#cv-editor")).ToBeHiddenAsync();

            // A row's own trash, after asking.
            await Row("Cy Test").HoverAsync();
            await Row("Cy Test").Locator("[data-action='delete']").ClickAsync();
            await page.Locator("sac-dialog[title='Delete Cy Test?']").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
            await Assertions.Expect(view.Locator(".cv-item")).ToHaveCountAsync(2, new() { Timeout = 10000 });
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "contacts-marked.png") });

            // All three are in the trash.
            var trash = (await (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/trash")).TextAsync());
            foreach (var name in new[] { "Acme", "Bob Test", "Cy Test" }) Assert.Contains(name, trash);
            Assert.Empty(errors);
        }
        finally
        {
            if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }
}
