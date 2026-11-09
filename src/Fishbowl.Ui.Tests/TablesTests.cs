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
            var studio = (await Post($"{t}/rooms/rows", new { title = "Studio", seats = 12, kind = "studio" })).GetProperty("id").GetString()!;
            async Task<long> Seats(string id)
            {
                var r = await context.APIRequest.GetAsync($"{_fixture.BaseUrl}{t}/rooms/rows/{id}");
                return (await r.JsonAsync())!.Value.GetProperty("seats").GetInt64();
            }
            async Task<bool> Until(Func<Task<bool>> ok)
            {
                for (var i = 0; i < 50; i++) { if (await ok()) return true; await Task.Delay(100, TestContext.Current.CancellationToken); }
                return false;
            }

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

            // A new row: what was typed into it reaches the server.
            await page.EvaluateAsync("() => document.querySelector('fb-tables-view sac-data-grid').addRow()");
            await page.Keyboard.TypeAsync("Attic");
            await page.Keyboard.PressAsync("Enter");
            saved = await page.EvaluateAsync<JsonElement>("() => document.querySelector('fb-tables-view sac-data-grid').save()");
            Assert.True(saved.GetProperty("errors").GetArrayLength() == 0, saved.ToString());
            var attic = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}{t}/rooms/count", new() { DataObject = new { where = new { title = "Attic" } } });
            Assert.Equal(1, (await attic.JsonAsync())!.Value.GetProperty("count").GetInt32());

            // An edit left unsaved — the cursor still in its row — is saved
            // when the grid leaves the page with the view (sac-data-grid 1.2).
            await page.EvaluateAsync("id => document.querySelector('fb-tables-view sac-data-grid').focusCell(id, 'seats')", hall);
            await page.Keyboard.TypeAsync("95");
            await page.Keyboard.PressAsync("Tab");
            Assert.Equal(1, await grid.EvaluateAsync<int>("g => g.dirty"));
            await page.EvaluateAsync("slug => { location.hash = `#/space/${slug}/notes`; }", slug);
            await Assertions.Expect(page.Locator("fb-notes-view")).ToHaveCountAsync(1);
            Assert.True(await Until(async () => await Seats(hall) == 95), "the unsaved row wasn't saved on leaving the view");

            // Tables exist only in spaces: the burger lists them there, not in
            // the personal workspace.
            await Assertions.Expect(page.Locator($"sac-nav a[href='#/space/{slug}/tables']")).ToHaveCountAsync(1);
            await page.GotoAsync($"{_fixture.BaseUrl}/#/");
            await Assertions.Expect(page.Locator("sac-nav a[href='#/notes']")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("sac-nav a[href$='/tables']")).ToHaveCountAsync(0);
            // The window apps are in the burger too, and open their window.
            await page.Locator("#fb-nav").EvaluateAsync("n => n.open?.()");
            var keys = page.Locator("sac-nav .nav-item", new() { HasText = "API keys" });
            await keys.ClickAsync();
            await Assertions.Expect(page.Locator("#fb-win-keys[open]")).ToHaveCountAsync(1);

            // Closing the page with a row unsaved asks once — the grid's
            // question, Fishbowl asks none of its own — and the grid saves
            // the row meanwhile.
            var tab = await context.NewPageAsync();
            tab.PageError += (_, e) => errors.Add(e);
            await tab.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/tables/rooms");
            var tabGrid = tab.Locator("fb-tables-view sac-data-grid");
            await Assertions.Expect(tabGrid).ToContainTextAsync("Studio", new() { Timeout = 10000 });
            await tab.EvaluateAsync("id => document.querySelector('fb-tables-view sac-data-grid').focusCell(id, 'seats')", studio);
            await tab.Keyboard.TypeAsync("14");
            await tab.Keyboard.PressAsync("Tab");
            Assert.Equal(1, await tabGrid.EvaluateAsync<int>("g => g.dirty"));
            // The grid arms its question with the status line, a frame later.
            await tab.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))");
            var asked = new List<string>();
            var savedWhileAsking = false;
            tab.Dialog += async (_, d) =>
            {
                asked.Add(d.Type);
                savedWhileAsking = await Until(async () => await Seats(studio) == 14);
                await d.AcceptAsync();
            };
            var closed = new TaskCompletionSource();
            tab.Close += (_, _) => closed.TrySetResult();
            await tab.CloseAsync(new() { RunBeforeUnload = true });
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "beforeunload" }, asked);
            Assert.True(savedWhileAsking, "the unsaved row wasn't saved while the page asked");

            Assert.Empty(errors);
        }
        finally { await context.CloseAsync(); }
    }
}
