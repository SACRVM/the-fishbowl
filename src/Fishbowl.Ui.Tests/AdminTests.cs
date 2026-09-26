using Dapper;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// Admin phase A1 in the browser: the envelope badge, approving a join
// request right from #/messages, the Users view — and that for anyone who
// isn't an admin, the admin view doesn't exist at all. The fixture's
// injected user is an active admin; pending accounts are seeded straight
// into the host's system.db (they need a Google sign-in otherwise).
[Collection(UiCollection.Name)]
public class AdminTests
{
    private const string TestUser = "test-internal-id";
    private readonly PlaywrightFixture _fixture;

    public AdminTests(PlaywrightFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private (DatabaseFactory db, SystemRepository system, UserAdminRepository admin, MessageRepository messages) Repos()
    {
        var db = new DatabaseFactory(_fixture.DataDir);
        return (db, new SystemRepository(db), new UserAdminRepository(db), new MessageRepository(db));
    }

    private async Task<string> SeedPendingAsync(string name, string email)
    {
        var (_, system, admin, messages) = Repos();
        var id = "ui-pending-" + Guid.NewGuid().ToString("N")[..8];
        await system.CreateUserAsync(id, name, email, null, Ct);
        await admin.SetStateAsync(id, UserStates.Pending, Ct);
        await system.CreateUserMappingAsync(id, "google", "g-" + id, Ct);
        await messages.CreateAsync(await admin.ListAdminIdsAsync(Ct), MessageKinds.UserPending, "user", id, null, Ct);
        return id;
    }

    [Fact]
    public async Task Admin_ApprovesAPendingUser_FromMessages_Test()
    {
        var pendingId = await SeedPendingAsync("Ada Lovelace", "ada@example.com");
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(_fixture.BaseUrl + "/#/messages");

            // The envelope carries the unread count.
            var envelope = page.Locator("#fb-messages-btn");
            await Assertions.Expect(envelope).ToBeVisibleAsync(new() { Timeout = 5000 });
            await Assertions.Expect(envelope.Locator(".badge-count")).ToBeVisibleAsync(new() { Timeout = 5000 });

            var row = page.Locator($"fb-messages-view .msg-row[data-kind='user.pending']")
                .Filter(new() { HasText = "Ada Lovelace (ada@example.com) wants to join" });
            await Assertions.Expect(row).ToHaveCountAsync(1, new() { Timeout = 5000 });
            await Assertions.Expect(row.Locator(".fb-quota-field input")).ToHaveValueAsync("20");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_messages.png"), FullPage = true });

            await row.Locator(".fb-quota-field input").FillAsync("2");
            await row.GetByRole(AriaRole.Button, new() { Name = "Approve" }).ClickAsync();
            await Assertions.Expect(page.Locator("#sac-toast-stack").GetByText("can use this Fishbowl now").First)
                .ToBeVisibleAsync(new() { Timeout = 10000 });
            await Assertions.Expect(row.Locator(".msg-meta")).ToContainTextAsync("approved", new() { Timeout = 5000 });

            var (_, system, _, _) = Repos();
            var user = await system.GetUserAsync(pendingId, Ct);
            Assert.Equal(UserStates.Active, user!.State);
            Assert.Equal(2L * 1024 * 1024 * 1024, user.QuotaBytes);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Admin_SeesTheUsersView_WithPendingFirst_Test()
    {
        await SeedPendingAsync("Grace Hopper", "grace@example.com");
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(_fixture.BaseUrl + "/#/admin/users");
            var view = page.Locator("fb-users-admin-view");
            await Assertions.Expect(view).ToBeVisibleAsync(new() { Timeout = 5000 });

            // The route exists for an admin, with its nav entry.
            Assert.True(await page.EvaluateAsync<bool>("() => sac.router.routes().some(r => r.hash === '#/admin/users')"));

            var pending = view.Locator("[data-section='pending'] .user-row").Filter(new() { HasText = "Grace Hopper" });
            await Assertions.Expect(pending).ToHaveCountAsync(1, new() { Timeout = 5000 });
            await Assertions.Expect(pending.GetByRole(AriaRole.Button, new() { Name = "Approve" })).ToBeVisibleAsync();

            var me = view.Locator("[data-section='accounts'] .user-row").Filter(new() { HasText = "Playwright" });
            await Assertions.Expect(me.Locator(".tag")).ToContainTextAsync(new[] { "Admin", "You" });
            // No action against yourself.
            await Assertions.Expect(me.GetByRole(AriaRole.Button)).ToHaveCountAsync(0);

            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_admin_users.png"), FullPage = true });

            // In a space the page points back to Personal.
            var slug = await page.EvaluateAsync<string>(
                "async () => (await fb.api.spaces.create({ name: 'Admin elsewhere ' + Math.random().toString(36).slice(2, 7) })).slug");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/admin/users");
            await Assertions.Expect(view).ToContainTextAsync("Open in Personal", new() { Timeout = 5000 });
            await page.EvaluateAsync("async (s) => fb.api.spaces.delete(s)", slug);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task NonAdmin_HasNoAdminView_Test()
    {
        var (db, _, _, _) = Repos();
        using (var sys = db.CreateSystemConnection())
            sys.Execute("UPDATE users SET is_admin = 0 WHERE id = @id", new { id = TestUser });
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(_fixture.BaseUrl + "/#/admin/users");
            await Assertions.Expect(page.Locator("#fb-account")).ToBeVisibleAsync(new() { Timeout = 5000 });
            // Messages are everyone's; the admin view is nobody else's.
            await Assertions.Expect(page.Locator("#fb-messages-btn")).ToBeVisibleAsync();
            Assert.False(await page.EvaluateAsync<bool>("() => sac.router.routes().some(r => r.hash === '#/admin/users')"));
            await Assertions.Expect(page.Locator("fb-users-admin-view")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("fb-hub-view")).ToBeVisibleAsync();
        }
        finally
        {
            using (var sys = db.CreateSystemConnection())
                sys.Execute("UPDATE users SET is_admin = 1 WHERE id = @id", new { id = TestUser });
            await context.CloseAsync();
        }
    }
}
