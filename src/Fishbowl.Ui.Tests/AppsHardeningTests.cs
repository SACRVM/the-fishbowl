using Dapper;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// Review fixes 2026-10-04 in the browser: context.space refuses dot segments
// before any request (a Designer's app can't reach /spaces/<slug>/ itself
// with the viewer's cookie), and the space settings and Muted dialogs close
// with the kit dialog's own button and leave nothing behind.
[Collection(UiCollection.Name)]
public class AppsHardeningTests
{
    private const string TestUser = "test-internal-id";
    private readonly PlaywrightFixture _fixture;

    public AppsHardeningTests(PlaywrightFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Fishbowl.Core.Models.Space> OwnSpaceAsync(string name) =>
        await new SpaceRepository(new DatabaseFactory(_fixture.DataDir)).CreateAsync(TestUser, name + " " + Guid.NewGuid().ToString("N")[..6], Ct);

    private async Task<(IBrowserContext Context, IPage Page)> OpenAsync()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
        });
        return (context, await context.NewPageAsync());
    }

    [Fact]
    public async Task SpaceData_RefusesDotSegments_BeforeAnyRequest_Test()
    {
        var space = await OwnSpaceAsync("Traversal");
        var (context, page) = await OpenAsync();
        try
        {
            var writes = new List<string>();
            page.Request += (_, r) => { if (r.Method != "GET" && r.Url.Contains("/api/v1/spaces/")) writes.Add($"{r.Method} {r.Url}"); };
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(page.Locator("fb-hub-view")).ToBeAttachedAsync(new() { Timeout = 15000 });

            var outcomes = await page.EvaluateAsync<string[]>(@"async (slug) => {
                const d = fb.api.spaceData(slug);
                const calls = [
                    () => d.remove('..', '..'),
                    () => d.update('..', '..', { appMessageText: true }),
                    () => d.remove('rooms', '.'),
                    () => d.get('', 'x'),
                    () => d.insert('..', { title: 'x' }),
                    () => d.query('.', {}),
                    () => d.note('..'),
                ];
                const out = [];
                for (const call of calls) {
                    try { await call(); out.push('sent'); }
                    catch (e) { out.push(`${e.status}:${JSON.parse(e.body).error}`); }
                }
                return out;
            }", space.Slug);
            Assert.All(outcomes, o => Assert.Equal("400:invalid_value", o));
            Assert.Empty(writes);

            // The space is untouched: still there, chat opt-in still off.
            var stored = await new SpaceRepository(new DatabaseFactory(_fixture.DataDir)).GetByIdAsync(space.Id, Ct);
            Assert.NotNull(stored);
            using var sys = new DatabaseFactory(_fixture.DataDir).CreateSystemConnection();
            Assert.Equal(0L, sys.ExecuteScalar<long>("SELECT COALESCE(app_message_text, 0) FROM spaces WHERE id = @id", new { id = space.Id }));
        }
        finally { await context.CloseAsync(); }
    }

    [Fact]
    public async Task SpaceSettingsAndMutedDialogs_CloseAndLeaveNothing_Test()
    {
        var space = await OwnSpaceAsync("Dialogs");
        var (context, page) = await OpenAsync();
        try
        {
            var win = await WindowApp.OpenAsync(page, _fixture.BaseUrl, "spaces");
            var settings = win.Locator($".space-row[data-slug='{space.Slug}'] .settings-btn");
            var dialog = page.Locator("#fb-space-settings");

            await settings.ClickAsync();
            await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Close" })).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
            await Assertions.Expect(dialog).ToHaveCountAsync(0);

            // Escape is a way out too; opening again never leaves two.
            await settings.ClickAsync();
            await Assertions.Expect(page.Locator("#fb-space-app-text")).ToHaveCountAsync(1);
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(dialog).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#fb-space-app-text")).ToHaveCountAsync(0);

            // Muted: something to show first.
            await page.EvaluateAsync("(id) => fb.api.messages.mute(id, null, true)", space.Id);
            await WindowApp.CloseAllAsync(page);
            var messages = await WindowApp.OpenAsync(page, _fixture.BaseUrl, "messages");
            await messages.Locator("#fb-messages-muted").ClickAsync();
            var muted = page.Locator("#fb-muted-dialog");
            await Assertions.Expect(muted.GetByRole(AriaRole.Button, new() { Name = "Close" })).ToBeVisibleAsync();
            await muted.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
            await Assertions.Expect(muted).ToHaveCountAsync(0);
            await page.EvaluateAsync("(id) => fb.api.messages.mute(id, null, false)", space.Id);
        }
        finally { await context.CloseAsync(); }
    }
}
