using Dapper;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// Admin phases A1 + A2 in the browser: the envelope badge, approving a join
// request right from #/messages, the Users view with its per-account menu
// (add a local user, admin, disable), System settings — and that for anyone
// who isn't an admin, neither admin view exists at all. The fixture's
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
            // Nothing in your own menu locks you out.
            await Assertions.Expect(me.Locator("button[data-action='block'], button[data-action='disable']")).ToHaveCountAsync(0);

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
            Assert.False(await page.EvaluateAsync<bool>("() => sac.router.routes().some(r => r.hash === '#/admin/settings')"));
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[href*='admin']")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("fb-users-admin-view")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("fb-hub-view")).ToBeVisibleAsync();
            await page.GotoAsync(_fixture.BaseUrl + "/#/admin/settings");
            await Assertions.Expect(page.Locator("fb-system-settings-view")).ToHaveCountAsync(0);
        }
        finally
        {
            using (var sys = db.CreateSystemConnection())
                sys.Execute("UPDATE users SET is_admin = 1 WHERE id = @id", new { id = TestUser });
            await context.CloseAsync();
        }
    }

    private static ILocator Row(IPage page, string name) =>
        page.Locator("fb-users-admin-view [data-section='accounts'] .user-row").Filter(new() { HasText = name });

    private static async Task ChooseAsync(IPage page, ILocator row, string action)
    {
        await row.Locator("sac-menu button[slot='trigger']").ClickAsync();
        await row.Locator($"sac-menu button[data-action='{action}']").ClickAsync();
    }

    [Fact]
    public async Task Admin_AddsALocalUser_AndSeesItsPasswordOnce_Test()
    {
        var username = "ui-local-" + Guid.NewGuid().ToString("N")[..6];
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(_fixture.BaseUrl + "/#/admin/users");
            await page.GetByRole(AriaRole.Button, new() { Name = "Add local user" }).ClickAsync(new() { Timeout = 5000 });
            var add = page.Locator("sac-dialog[title='Add a local user']");
            await add.Locator("#fb-add-username").FillAsync(username);
            await add.Locator("#fb-add-name").FillAsync("Local Person");
            await add.GetByRole(AriaRole.Button, new() { Name = "Add user" }).ClickAsync();

            var reveal = page.Locator("sac-dialog[title='User added']");
            await reveal.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            var password = (await reveal.Locator(".fb-temp-pw").InnerTextAsync()).Trim();
            Assert.Equal(12, password.Length);
            // Escape doesn't lose it: the dialog comes back until it's acknowledged.
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page.Locator("sac-dialog[title='User added'] .fb-temp-pw")).ToHaveTextAsync(password, new() { Timeout = 5000 });
            await page.Locator("sac-dialog[title='User added']").GetByRole(AriaRole.Button, new() { Name = "I've passed it on" }).ClickAsync();
            await Assertions.Expect(page.Locator("sac-dialog[title='User added']")).ToHaveCountAsync(0, new() { Timeout = 5000 });

            var row = Row(page, "Local Person");
            await Assertions.Expect(row).ToHaveCountAsync(1, new() { Timeout = 5000 });
            await Assertions.Expect(row).ToContainTextAsync("Signs in with Password");
            Assert.DoesNotContain(password, await page.Locator("fb-users-admin-view").InnerTextAsync());

            var (_, system, _, _) = Repos();
            var user = await system.GetUserByLocalUsernameAsync(username, Ct);
            Assert.Equal(UserStates.Active, user!.State);
            Assert.True(user.MustChangePassword);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Admin_TogglesAdmin_AndDisablesAndEnablesAnAccount_Test()
    {
        var (_, system, _, _) = Repos();
        var id = "ui-member-" + Guid.NewGuid().ToString("N")[..8];
        var name = "Member " + id[^4..];
        await system.CreateUserAsync(id, name, id + "@example.com", null, Ct);
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(_fixture.BaseUrl + "/#/admin/users");
            var row = Row(page, name);
            await Assertions.Expect(row).ToHaveCountAsync(1, new() { Timeout = 5000 });

            await ChooseAsync(page, row, "admin-on");
            await page.Locator("sac-dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Make admin" }).ClickAsync();
            await Assertions.Expect(row.Locator(".tag")).ToContainTextAsync(new[] { "Admin" }, new() { Timeout = 5000 });
            Assert.True((await system.GetUserAsync(id, Ct))!.IsAdmin);

            await ChooseAsync(page, row, "admin-off");
            await page.Locator("sac-dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Remove admin" }).ClickAsync();
            await Assertions.Expect(row.Locator(".tag")).ToHaveCountAsync(0, new() { Timeout = 5000 });

            await ChooseAsync(page, row, "disable");
            await page.Locator("sac-dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Disable" }).ClickAsync();
            await Assertions.Expect(row.Locator(".tag")).ToContainTextAsync(new[] { "Disabled" }, new() { Timeout = 5000 });
            Assert.Equal(UserStates.Disabled, (await system.GetUserAsync(id, Ct))!.State);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_admin_users.png"), FullPage = true });

            await ChooseAsync(page, row, "enable");
            await Assertions.Expect(row.Locator(".tag")).ToHaveCountAsync(0, new() { Timeout = 5000 });
            Assert.Equal(UserStates.Active, (await system.GetUserAsync(id, Ct))!.State);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Admin_SavesSystemSettings_AndASecretStaysHidden_Test()
    {
        // Two dots, ~70 characters — the shape the server validates.
        const string Token = "MTAwMDAwMDAwMDAwMDAwMDAw.GhTest.abcdefghijklmnopqrstuvwxyz0123456789abcdefgh";
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(_fixture.BaseUrl + "/#/admin/settings");
            var view = page.Locator("fb-system-settings-view");
            var hour = view.Locator(".cfg-row[data-key='Digest:Hour']");
            await Assertions.Expect(hour).ToBeVisibleAsync(new() { Timeout = 5000 });
            await Assertions.Expect(view.Locator("section[data-section='Daily digest']")).ToContainTextAsync("Digest hour");

            // A bad value is refused under its row; a good one saves.
            await hour.Locator("input").FillAsync("30");
            await hour.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await Assertions.Expect(hour.Locator(".cfg-error")).ToContainTextAsync("0 to 23", new() { Timeout = 5000 });
            await hour.Locator("input").FillAsync("9");
            await hour.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await Assertions.Expect(page.Locator("#sac-toast-stack").GetByText("Digest hour saved").First).ToBeVisibleAsync(new() { Timeout = 5000 });

            // A choice list for a fixed set of values.
            var signUp = view.Locator(".cfg-row[data-key='Auth:SignUp'] select");
            await Assertions.Expect(signUp).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("approval|open|closed"));

            // The secret: set it, then it's only ever "Set".
            var token = view.Locator(".cfg-row[data-key='Discord:BotToken']");
            await token.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^(Set|Replace)$") }).ClickAsync();
            await token.Locator("input[type='password']").FillAsync(Token);
            await token.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await Assertions.Expect(token.Locator(".cfg-state")).ToHaveTextAsync("Set", new() { Timeout = 5000 });
            await Assertions.Expect(view.Locator(".restart-note")).ToBeVisibleAsync();

            await page.ReloadAsync();
            await Assertions.Expect(view.Locator(".cfg-row[data-key='Digest:Hour'] input")).ToHaveValueAsync("9", new() { Timeout = 5000 });
            Assert.DoesNotContain(Token, await page.ContentAsync());
            Assert.DoesNotContain("abcdefghijklmnop", await view.InnerTextAsync());
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_system_settings.png"), FullPage = true });

            // In a space the page points back to Personal.
            var slug = await page.EvaluateAsync<string>(
                "async () => (await fb.api.spaces.create({ name: 'Settings elsewhere ' + Math.random().toString(36).slice(2, 7) })).slug");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/admin/settings");
            await Assertions.Expect(view).ToContainTextAsync("Open in Personal", new() { Timeout = 5000 });
            await page.EvaluateAsync("async (s) => fb.api.spaces.delete(s)", slug);
        }
        finally
        {
            await page.EvaluateAsync("async () => { await fb.api.admin.clearConfig('Digest:Hour'); await fb.api.admin.clearConfig('Discord:BotToken'); }");
            await context.CloseAsync();
        }
    }
}
