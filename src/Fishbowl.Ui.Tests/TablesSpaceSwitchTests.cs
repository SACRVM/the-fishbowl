using System.Text.Json;
using Dapper;
using Fishbowl.Data;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// The Tables app is a prefix route: going from one space's table to
// another space's keeps the view mounted. The new space's tables and the
// role there must take over — never the last space's columns or write mode.
[Collection(UiCollection.Name)]
public class TablesSpaceSwitchTests
{
    private readonly PlaywrightFixture _fixture;

    public TablesSpaceSwitchTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Tables_SwitchToAnotherSpace_ShowsItsTableAndRole_Test()
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
        var db = new DatabaseFactory(_fixture.DataDir);
        string? a = null, b = null;
        try
        {
            async Task<JsonElement> Post(string path, object body)
            {
                var r = await page.APIRequest.PostAsync(_fixture.BaseUrl + path, new APIRequestContextOptions { DataObject = body });
                Assert.True(r.Ok, $"{path}: {r.Status} {await r.TextAsync()}");
                return (await r.JsonAsync())!.Value;
            }
            // Two spaces, each with a table called "rooms" — different columns.
            a = (await Post("/api/v1/spaces", new { name = "Club " + Guid.NewGuid().ToString("N")[..6] })).GetProperty("slug").GetString()!;
            b = (await Post("/api/v1/spaces", new { name = "Club " + Guid.NewGuid().ToString("N")[..6] })).GetProperty("slug").GetString()!;
            await Post($"/api/v1/spaces/{a}/tables", new { name = "rooms", columns = new object[] { new { name = "seats", type = "integer" } } });
            await Post($"/api/v1/spaces/{a}/tables/rooms/rows", new { title = "Hall", seats = 80 });
            await Post($"/api/v1/spaces/{b}/tables", new { name = "rooms", columns = new object[] { new { name = "colour", type = "text" } } });
            await Post($"/api/v1/spaces/{b}/tables/rooms/rows", new { title = "Stage", colour = "red" });
            // In the second space the test user only reads.
            using (var conn = db.CreateSystemConnection())
                await conn.ExecuteAsync(
                    "UPDATE space_members SET role = 'reader' WHERE user_id = 'test-internal-id' AND space_id = (SELECT id FROM spaces WHERE slug = @b)",
                    new { b });

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{a}/tables/rooms");
            var grid = page.Locator("fb-tables-view sac-data-grid");
            await Assertions.Expect(grid).ToContainTextAsync("Hall", new() { Timeout = 10000 });
            await Assertions.Expect(grid).ToHaveAttributeAsync("mode", "sheet");
            await page.EvaluateAsync("() => { document.querySelector('fb-tables-view').dataset.mark = 'first'; }");

            await page.EvaluateAsync("slug => { location.hash = `#/space/${slug}/tables/rooms`; }", b);
            await Assertions.Expect(grid).ToContainTextAsync("Stage", new() { Timeout = 10000 });
            await Assertions.Expect(grid).ToHaveAttributeAsync("mode", "read");
            var fields = await grid.EvaluateAsync<string[]>("g => g.columns.map(c => c.field)");
            Assert.Contains("colour", fields);
            Assert.DoesNotContain("seats", fields);
            // The same view, kept mounted by the prefix route — the case under test.
            Assert.Equal("first", await page.EvaluateAsync<string>("() => document.querySelector('fb-tables-view').dataset.mark"));
            Assert.Empty(errors);
        }
        finally
        {
            if (b is not null)
            {
                using var conn = db.CreateSystemConnection();
                await conn.ExecuteAsync(
                    "UPDATE space_members SET role = 'owner' WHERE user_id = 'test-internal-id' AND space_id = (SELECT id FROM spaces WHERE slug = @b)",
                    new { b });
            }
            foreach (var slug in new[] { a, b })
                if (slug is not null) await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            await context.CloseAsync();
        }
    }
}
