using System.Text.Json;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The Tables app: a space's tables on the vendored sac-data-grid — listed on
// the left, the rows of the one in the URL in the grid, edits saved through
// the tables API.
[Collection(UiCollection.Name)]
public class TablesTests
{
    private readonly PlaywrightFixture _fixture;

    public TablesTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Tables_ListAndGrid_EditARow_Test()
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
        try
        {
            async Task<JsonElement> Post(string path, object body)
            {
                var r = await page.APIRequest.PostAsync(_fixture.BaseUrl + path, new APIRequestContextOptions { DataObject = body });
                Assert.True(r.Ok, $"{path}: {r.Status} {await r.TextAsync()}");
                return (await r.JsonAsync())!.Value;
            }
            var slug = (await Post("/api/v1/spaces", new { name = "Club " + Guid.NewGuid().ToString("N")[..6] })).GetProperty("slug").GetString()!;
            var t = $"/api/v1/spaces/{slug}/tables";
            await Post(t, new
            {
                name = "rooms",
                description = "Rooms of the club house",
                columns = new object[]
                {
                    new { name = "seats", type = "integer" },
                    new { name = "kind", type = "choice", options = new[] { "hall", "studio" } },
                    new { name = "booked", type = "yesno" },
                },
            });
            var hall = (await Post($"{t}/rooms/rows", new { title = "Hall", seats = 80, kind = "hall" })).GetProperty("id").GetString()!;
            await Post($"{t}/rooms/rows", new { title = "Studio", seats = 12, kind = "studio" });

            // The desktop of the space has the Tables tile; it lists the table.
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/");
            await page.Locator("a.tile[href$='#/space/" + slug + "/tables']").ClickAsync();
            var item = page.Locator("fb-tables-view .tb-item", new() { HasText = "rooms" });
            await Assertions.Expect(item).ToContainTextAsync("2 rows");
            await item.ClickAsync();
            await page.WaitForURLAsync(u => u.EndsWith($"#/space/{slug}/tables/rooms"));

            var grid = page.Locator("fb-tables-view sac-data-grid");
            await Assertions.Expect(grid).ToContainTextAsync("Hall", new() { Timeout = 10000 });
            await Assertions.Expect(grid).ToContainTextAsync("Studio");

            // Edit a cell and save: the server has the new value.
            await page.EvaluateAsync("id => document.querySelector('fb-tables-view sac-data-grid').focusCell(id, 'seats')", hall);
            await page.Keyboard.TypeAsync("90");
            await page.Keyboard.PressAsync("Enter");
            var saved = await page.EvaluateAsync<JsonElement>("() => document.querySelector('fb-tables-view sac-data-grid').save()");
            Assert.Equal(0, saved.GetProperty("errors").GetArrayLength());
            var row = await page.APIRequest.GetAsync($"{_fixture.BaseUrl}{t}/rooms/rows/{hall}");
            Assert.Equal(90, (await row.JsonAsync())!.Value.GetProperty("seats").GetInt64());

            // Tables exist only in spaces: the burger lists them there, not in
            // the personal workspace.
            await Assertions.Expect(page.Locator($"sac-nav a[href='#/space/{slug}/tables']")).ToHaveCountAsync(1);
            await page.GotoAsync($"{_fixture.BaseUrl}/#/");
            await Assertions.Expect(page.Locator("sac-nav a[href='#/notes']")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("sac-nav a[href$='/tables']")).ToHaveCountAsync(0);

            Assert.Empty(errors);
        }
        finally { await context.CloseAsync(); }
    }
}
