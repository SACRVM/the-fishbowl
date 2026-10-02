using Dapper;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// Admin phases A1 + A2 in the browser: the envelope badge, approving a join
// request right from the Messages window, the Users view with its per-account menu
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
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "messages");

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
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "users");
            var view = page.Locator("fb-users-admin-view");
            await Assertions.Expect(view).ToBeVisibleAsync(new() { Timeout = 5000 });

            // A window app: no route, no burger entry.
            Assert.False(await page.EvaluateAsync<bool>("() => sac.router.routes().some(r => r.hash === '#/admin/users')"));

            var pending = view.Locator("[data-section='pending'] .user-row").Filter(new() { HasText = "Grace Hopper" });
            await Assertions.Expect(pending).ToHaveCountAsync(1, new() { Timeout = 5000 });
            await Assertions.Expect(pending.GetByRole(AriaRole.Button, new() { Name = "Approve" })).ToBeVisibleAsync();

            var me = view.Locator("[data-section='accounts'] .user-row").Filter(new() { HasText = "Playwright" });
            await Assertions.Expect(me.Locator(".tag[label='Global admin']")).ToHaveCountAsync(1);
            await Assertions.Expect(me.Locator(".tag[label='You']")).ToHaveCountAsync(1);
            // Nothing in your own menu locks you out.
            await Assertions.Expect(me.Locator("button[data-action='block'], button[data-action='disable']")).ToHaveCountAsync(0);

            // In a space the page does not exist.
            var slug = await page.EvaluateAsync<string>(
                "async () => (await fb.api.spaces.create({ name: 'Admin elsewhere ' + Math.random().toString(36).slice(2, 7) })).slug");
            // Personal only: switching to a space closes the window, and the
            // space's desktop has no Users tile.
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/");
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[data-key='builtin:notes']")).ToBeVisibleAsync(new() { Timeout = 15000 });
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[data-key='builtin:users']")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#fb-win-users[open]")).ToHaveCountAsync(0);
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
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(page.Locator("#fb-account")).ToBeVisibleAsync(new() { Timeout = 5000 });
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[data-key='builtin:notes']")).ToBeVisibleAsync(new() { Timeout = 15000 });
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[data-key='builtin:users']")).ToHaveCountAsync(0);
            // Messages are everyone's; the admin view is nobody else's.
            await Assertions.Expect(page.Locator("#fb-messages-btn")).ToBeVisibleAsync();
            Assert.False(await page.EvaluateAsync<bool>("() => sac.router.routes().some(r => r.hash === '#/admin/users')"));
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[data-key='builtin:system']")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[href*='admin']")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("fb-users-admin-view")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("fb-hub-view")).ToBeVisibleAsync();
            Assert.False(await page.EvaluateAsync<bool>("() => !!window.fb?.desktop && fb.desktop.builtins().then(b => b.some(a => a.key === 'builtin:system'))"));
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
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "users");
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
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "users");
            var row = Row(page, name);
            await Assertions.Expect(row).ToHaveCountAsync(1, new() { Timeout = 5000 });

            await ChooseAsync(page, row, "admin-on");
            await page.Locator("sac-dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Make global admin" }).ClickAsync();
            await Assertions.Expect(row.Locator(".tag[label='Global admin']")).ToHaveCountAsync(1, new() { Timeout = 5000 });
            Assert.True((await system.GetUserAsync(id, Ct))!.IsAdmin);

            await ChooseAsync(page, row, "admin-off");
            await page.Locator("sac-dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Remove global admin" }).ClickAsync();
            await Assertions.Expect(row.Locator(".tag")).ToHaveCountAsync(0, new() { Timeout = 5000 });

            await ChooseAsync(page, row, "disable");
            await page.Locator("sac-dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Disable" }).ClickAsync();
            await Assertions.Expect(row.Locator(".tag[label='Disabled']")).ToHaveCountAsync(1, new() { Timeout = 5000 });
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

    // A3: Delete… lists the spaces the account alone owns and offers no
    // Delete then; otherwise "Archive their data first" is checked and the
    // account, its folder and its row go.
    [Fact]
    public async Task Admin_DeletesAnAccount_ArchiveFirst_BlockedByOwnedSpaces_Test()
    {
        var (db, system, _, _) = Repos();
        var shot = Path.Combine(Path.GetTempPath(), "a3-final");
        Directory.CreateDirectory(shot);

        var owner = "ui-owner-" + Guid.NewGuid().ToString("N")[..8];
        var ownerName = "Owner " + owner[^4..];
        await system.CreateUserAsync(owner, ownerName, owner + "@example.com", null, Ct);
        var spaceName = "Solo " + owner[^4..];
        using (var sys = db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            sys.Execute("INSERT INTO spaces (id, slug, name, created_by, created_at) VALUES (@id, @slug, @name, @owner, @now)",
                new { id = "sp-" + owner, slug = "solo-" + owner[^8..], name = spaceName, owner, now });
            sys.Execute("INSERT INTO space_members (space_id, user_id, role, joined_at) VALUES (@id, @owner, 'owner', @now)",
                new { id = "sp-" + owner, owner, now });
        }

        var gone = "ui-gone-" + Guid.NewGuid().ToString("N")[..8];
        var goneName = "Leaving " + gone[^4..];
        await system.CreateUserAsync(gone, goneName, gone + "@example.com", null, Ct);
        using (var conn = db.CreateContextConnection(Fishbowl.Core.ContextRef.User(gone)))
            conn.Execute("INSERT INTO notes (id, title, created_by, created_at, updated_at) VALUES ('n', 't', @u, @now, @now)",
                new { u = gone, now = DateTime.UtcNow.ToString("o") });
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        try
        {
            await WindowApp.OpenAsync(page, _fixture.BaseUrl, "users");

            // Blocked: the dialog lists the space and only closes.
            var ownerRow = Row(page, ownerName);
            await Assertions.Expect(ownerRow).ToHaveCountAsync(1, new() { Timeout = 5000 });
            await ChooseAsync(page, ownerRow, "delete");
            var blocked = page.Locator("sac-dialog[open]");
            await Assertions.Expect(blocked.Locator(".fb-user-delete-spaces li")).ToHaveTextAsync(spaceName, new() { Timeout = 5000 });
            await Assertions.Expect(blocked.GetByRole(AriaRole.Button, new() { Name = "Delete account" })).ToHaveCountAsync(0);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(shot, "desk-delete-blocked.png") });
            await blocked.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
            Assert.NotNull(await system.GetUserAsync(owner, Ct));

            // Allowed: archive is checked by default.
            var row = Row(page, goneName);
            await ChooseAsync(page, row, "delete");
            var dlg = page.Locator("sac-dialog[open]");
            await Assertions.Expect(dlg.Locator("input[name=archive]")).ToBeCheckedAsync(new() { Timeout = 5000 });
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(shot, "desk-delete-dialog.png") });
            await dlg.GetByRole(AriaRole.Button, new() { Name = "Delete account" }).ClickAsync();
            await Assertions.Expect(page.Locator("#sac-toast-stack").GetByText("was archived and deleted").First)
                .ToBeVisibleAsync(new() { Timeout = 10000 });
            await Assertions.Expect(row).ToHaveCountAsync(0, new() { Timeout = 5000 });
            Assert.Null(await system.GetUserAsync(gone, Ct));
            Assert.False(Directory.Exists(db.ResolveContextFolder(Fishbowl.Core.ContextRef.User(gone))));
            Assert.Contains(Directory.EnumerateFiles(Path.Combine(_fixture.DataDir, "archive", "users"), "*.zip"),
                f => Path.GetFileName(f).StartsWith(gone + "-", StringComparison.Ordinal));

            await ChooseAsync(page, ownerRow, "quota");   // the menu still works after a refresh
            await page.Keyboard.PressAsync("Escape");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(shot, "desk-users.png"), FullPage = true });
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task Admin_SeesTheSystemPage_WithVersionSizesAndArchives_Test()
    {
        var shot = Path.Combine(Path.GetTempPath(), "a3-final");
        Directory.CreateDirectory(shot);
        foreach (var (w, h, tag, mobile) in new[] { (1400, 900, "desk", false), (390, 800, "phone", true) })
        {
            var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new ViewportSize { Width = w, Height = h },
                IsMobile = mobile,
                HasTouch = mobile,
            });
            var page = await context.NewPageAsync();
            try
            {
                // The desktop tile opens the System window.
                await page.GotoAsync(_fixture.BaseUrl + "/#/");
                await page.Locator("fb-hub-view a.tile[data-key='builtin:system']").ClickAsync(new() { Timeout = 5000 });
                var view = page.Locator("#fb-win-system fb-system-view");
                await view.Locator("sac-tab[name='info']").ClickAsync();
                await Assertions.Expect(view.Locator(".fb-row[data-key='version'] .fb-row-meta")).Not.ToBeEmptyAsync(new() { Timeout = 5000 });
                await Assertions.Expect(view.Locator(".fb-row[data-key='users'] .fb-row-meta")).ToContainTextAsync("account");
                await Assertions.Expect(view.Locator(".fb-row[data-key='archived-users']")).ToBeVisibleAsync();
                await Assertions.Expect(view.Locator(".fb-row[data-key='embedding'] .fb-row-meta")).Not.ToBeEmptyAsync();
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(shot, $"{tag}-system.png"), FullPage = true });
            }
            finally
            {
                await context.CloseAsync();
            }
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
            // The System window: Info first, then one tab per settings
            // group — all in one row; the last tab is remembered.
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await page.EvaluateAsync("() => fb.systemApp.open('info')");
            var view = page.Locator("#fb-win-system fb-system-view");
            await Assertions.Expect(view.Locator(".fb-row[data-key='version']")).ToBeVisibleAsync(new() { Timeout = 5000 });
            var first = (await view.Locator("sac-tab").First.BoundingBoxAsync())!;
            var last = (await view.Locator("sac-tab").Last.BoundingBoxAsync())!;
            Assert.True(Math.Abs(first.Y - last.Y) < 2, "every tab in one row");
            await view.Locator("sac-tab[name='digest']").ClickAsync();
            var hour = view.Locator(".cfg-row[data-key='Digest:Hour']");
            await Assertions.Expect(hour).ToBeVisibleAsync(new() { Timeout = 5000 });
            await Assertions.Expect(view.Locator("[data-section='digest']")).ToContainTextAsync("Digest hour");
            await Assertions.Expect(view.Locator(".fb-row[data-key='version']")).ToBeHiddenAsync();

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
            await view.Locator("sac-tab[name='integrations']").ClickAsync();
            var token = view.Locator(".cfg-row[data-key='Discord:BotToken']");
            await token.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^(Set|Replace)$") }).ClickAsync();
            await token.Locator("input[type='password']").FillAsync(Token);
            await token.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await Assertions.Expect(token.Locator(".cfg-state")).ToHaveTextAsync("Set", new() { Timeout = 5000 });
            await Assertions.Expect(view.Locator(".restart-note")).ToBeVisibleAsync();

            await page.ReloadAsync();
            await page.EvaluateAsync("() => fb.systemApp.open()");
            await Assertions.Expect(view.Locator("sac-tab[name='integrations'][active]")).ToHaveCountAsync(1, new() { Timeout = 5000 });
            await Assertions.Expect(view.Locator(".cfg-row[data-key='Digest:Hour'] input")).ToHaveValueAsync("9", new() { Timeout = 5000 });
            Assert.DoesNotContain(Token, await page.ContentAsync());
            Assert.DoesNotContain("abcdefghijklmnop", await view.InnerTextAsync());
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "fishbowl_ui_system_settings.png"), FullPage = true });

            // In a space there is no System tile.
            var slug = await page.EvaluateAsync<string>(
                "async () => (await fb.api.spaces.create({ name: 'Settings elsewhere ' + Math.random().toString(36).slice(2, 7) })).slug");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/");
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[data-key='builtin:notes']")).ToBeVisibleAsync(new() { Timeout = 15000 });
            await Assertions.Expect(page.Locator("fb-hub-view a.tile[data-key='builtin:system']")).ToHaveCountAsync(0);
            await page.EvaluateAsync("async (s) => fb.api.spaces.delete(s)", slug);
        }
        finally
        {
            await page.EvaluateAsync("async () => { await fb.api.admin.clearConfig('Digest:Hour'); await fb.api.admin.clearConfig('Discord:BotToken'); }");
            await context.CloseAsync();
        }
    }
}
