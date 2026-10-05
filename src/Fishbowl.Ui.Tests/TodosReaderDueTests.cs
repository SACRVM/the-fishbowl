using Dapper;
using Fishbowl.Data;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// A space Reader can't change a todo's due date — the date and its time
// field are one control, both disabled.
[Collection(UiCollection.Name)]
public class TodosReaderDueTests
{
    private readonly PlaywrightFixture _fixture;

    public TodosReaderDueTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Todos_Reader_DueDateAndTimeBothDisabled_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true, BypassCSP = true });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        var db = new DatabaseFactory(_fixture.DataDir);
        string? slug = null;
        try
        {
            var res = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new() { DataObject = new { name = "Chores " + Guid.NewGuid().ToString("N")[..6] } });
            slug = (await res.JsonAsync())!.Value.GetProperty("slug").GetString()!;
            var todo = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/todos",
                new() { DataObject = new { title = "Water the plants", dueAt = DateTime.UtcNow.AddDays(2).ToString("o") } });
            Assert.True(todo.Ok, await todo.TextAsync());
            using (var conn = db.CreateSystemConnection())
                await conn.ExecuteAsync(
                    "UPDATE space_members SET role = 'reader' WHERE user_id = 'test-internal-id' AND space_id = (SELECT id FROM spaces WHERE slug = @slug)",
                    new { slug });

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/todos");
            var view = page.Locator("fb-todos-view");
            await view.Locator(".tv-item", new() { HasText = "Water the plants" }).ClickAsync();
            await Assertions.Expect(view.Locator("sac-date-field#due-at")).ToHaveAttributeAsync("disabled", "");
            await Assertions.Expect(view.Locator("sac-time-field.fb-time-field")).ToHaveAttributeAsync("disabled", "");
            Assert.Empty(errors);
        }
        finally
        {
            if (slug is not null)
            {
                using (var conn = db.CreateSystemConnection())
                    await conn.ExecuteAsync(
                        "UPDATE space_members SET role = 'owner' WHERE user_id = 'test-internal-id' AND space_id = (SELECT id FROM spaces WHERE slug = @slug)",
                        new { slug });
                await page.APIRequest.DeleteAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}?archive=false");
            }
            await context.CloseAsync();
        }
    }
}
