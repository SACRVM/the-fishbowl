using Dapper;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// A space Reader meets no write action the server would refuse (no dead
// buttons): Notes, Todos and Calendar show but don't offer to create,
// change or delete, and the palette has no Create commands there.
[Collection(UiCollection.Name)]
public class ReaderTests
{
    private readonly PlaywrightFixture _fixture;

    public ReaderTests(PlaywrightFixture fixture) => _fixture = fixture;

    private async Task<string> SeedReaderSpaceAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = new DatabaseFactory(_fixture.DataDir);
        var system = new SystemRepository(db);
        var ownerId = "ui-owner-" + Guid.NewGuid().ToString("N")[..8];
        await system.CreateUserAsync(ownerId, "Owner", null, null, ct);
        var space = await new SpaceRepository(db).CreateAsync(ownerId, "Readers " + Guid.NewGuid().ToString("N")[..6], ct);
        await new NoteRepository(db, new TagRepository(db)).CreateAsync(Fishbowl.Core.ContextRef.Space(space.Id), ownerId,
            new Fishbowl.Core.Models.Note { Title = "House rules", Content = "House rules\nBe kind." }, Fishbowl.Core.Models.NoteSource.Human, ct);
        using var conn = db.CreateSystemConnection();
        await conn.ExecuteAsync(
            "INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@s, @u, 'reader', @t)",
            new { s = space.Id, u = "test-internal-id", t = DateTime.UtcNow.ToString("o") });
        return space.Slug;
    }

    [Fact]
    public async Task Reader_SeesNoWriteActions_Test()
    {
        var slug = await SeedReaderSpaceAsync();
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true, BypassCSP = true });
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/notes");
            var notes = page.Locator("fb-notes-view");
            await Assertions.Expect(notes).ToHaveAttributeAsync("readonly", "");
            await Assertions.Expect(notes.Locator("#new-btn")).ToBeHiddenAsync();
            var row = notes.Locator(".nv-item", new() { HasText = "House rules" });
            await row.ClickAsync();
            await Assertions.Expect(row.Locator(".nv-item-actions")).ToBeHiddenAsync();
            await Assertions.Expect(notes.Locator("#content")).ToHaveAttributeAsync("readonly", "");
            await Assertions.Expect(page.Locator("#fb-nav [title='Manage tags']")).ToHaveCountAsync(0);

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/todos");
            await Assertions.Expect(page.Locator("fb-todos-view #new-btn")).ToBeHiddenAsync();

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/calendar");
            await Assertions.Expect(page.Locator("fb-calendar-view #cv-new-btn")).ToBeHiddenAsync();

            // The palette offers no Create commands in this space.
            await page.WaitForFunctionAsync("() => sac.commands.list().some(c => c.id.startsWith('fb:desk:app:'))");
            var created = await page.EvaluateAsync<bool>("() => sac.commands.list().some(c => c.id.startsWith('fb:desk:create:'))");
            Assert.False(created);
        }
        finally { await context.CloseAsync(); }
    }
}
