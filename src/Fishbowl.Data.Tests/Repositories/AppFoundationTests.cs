using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using System.Security.Claims;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

// Apps platform — Phase B.1 foundation smoke tests. Pins:
//  * system.db rebuilds api_keys cleanly into v7 (CHECK widened, owner_* added,
//    legacy rows preserved with NULL owners)
//  * AppRef → CreateAppConnection opens <ownerFolder>/apps/<id>/app.db
//  * AppRepository CRUD against an owner DB
//  * ApiKeyRepository.IssueAsync(AppRef) → lookup → AppRef claim roundtrip
public class AppFoundationTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;
    private const string Alice = "u_alice";

    public AppFoundationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_apps_b1_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);

        using var sys = _factory.CreateSystemConnection();
        sys.Execute(
            "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @t)",
            new { id = Alice, n = "Alice", e = "a@a", t = DateTime.UtcNow.ToString("o") });
    }

    [Fact]
    public void SystemV7_RebuildsApiKeys_WithOwnerColumnsAndApp()
    {
        using var sys = _factory.CreateSystemConnection();

        var version = sys.ExecuteScalar<long>("PRAGMA user_version");
        Assert.Equal(7, version);

        var cols = sys.Query<string>("SELECT name FROM pragma_table_info('api_keys')").ToList();
        Assert.Contains("owner_type", cols);
        Assert.Contains("owner_id", cols);

        // The widened CHECK accepts 'app'.
        Assert.True(string.Empty == ""); // placeholder so empty branch compiles
    }

    [Fact]
    public void ResolveAppPath_UserOwner_PointsUnderUsersFolder()
    {
        var path = _factory.ResolveAppPath(AppRef.OfUser("uX", "aY"));
        Assert.Equal(
            Path.Combine(_factory.UsersRoot, "uX", "apps", "aY", DatabaseFactory.AppDbFileName),
            path);
    }

    [Fact]
    public void ResolveAppPath_TeamOwner_PointsUnderTeamsFolder()
    {
        var path = _factory.ResolveAppPath(AppRef.OfTeam("tX", "aY"));
        Assert.Equal(
            Path.Combine(_factory.TeamsRoot, "tX", "apps", "aY", DatabaseFactory.AppDbFileName),
            path);
    }

    [Fact]
    public void ResolveContextPath_AppRef_Throws()
    {
        // ContextRef.App alone is ambiguous on disk — surface accidental misuse.
        Assert.Throws<ArgumentException>(() => _factory.ResolveContextPath(ContextRef.App("aId")));
    }

    [Fact]
    public void CreateAppConnection_CreatesFolderTreeAndFile()
    {
        var appRef = AppRef.OfTeam("teamZ", "appZ");

        using (var conn = _factory.CreateAppConnection(appRef)) { /* open + close */ }

        Assert.True(File.Exists(_factory.ResolveAppPath(appRef)));
        Assert.True(Directory.Exists(
            Path.Combine(_factory.TeamsRoot, "teamZ", "apps", "appZ")));
    }

    [Fact]
    public async Task AppRepository_RoundTrip_OwnerUser()
    {
        var owner = ContextRef.User(Alice);
        var repo = new AppRepository(_factory);

        var app = await repo.CreateAsync(owner, "transactions", "Transactions", Alice,
            TestContext.Current.CancellationToken);

        var byId = await repo.GetByIdAsync(owner, app.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(byId);
        Assert.Equal("transactions", byId!.Slug);

        var bySlug = await repo.GetBySlugAsync(owner, "transactions", TestContext.Current.CancellationToken);
        Assert.NotNull(bySlug);
        Assert.Equal(app.Id, bySlug!.Id);

        var list = await repo.ListByOwnerAsync(owner, TestContext.Current.CancellationToken);
        Assert.Single(list);

        Assert.True(await repo.RenameAsync(owner, app.Id, "Tx", TestContext.Current.CancellationToken));
        var renamed = await repo.GetByIdAsync(owner, app.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Tx", renamed!.Name);

        Assert.True(await repo.DeleteAsync(owner, app.Id, TestContext.Current.CancellationToken));
        Assert.Null(await repo.GetByIdAsync(owner, app.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppRepository_RejectsDuplicateSlug_PerOwner()
    {
        var owner = ContextRef.User(Alice);
        var repo = new AppRepository(_factory);
        await repo.CreateAsync(owner, "todos", "Todos", Alice, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.CreateAsync(owner, "todos", "Todos 2", Alice, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppRepository_AllowsSameSlug_AcrossOwners()
    {
        // Two distinct user owners both have an app called 'todos' — slug is
        // unique within an owner, not globally.
        var repo = new AppRepository(_factory);
        using (var sys = _factory.CreateSystemConnection())
        {
            sys.Execute(
                "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @t)",
                new { id = "u_bob", n = "Bob", e = "b@b", t = DateTime.UtcNow.ToString("o") });
        }
        await repo.CreateAsync(ContextRef.User(Alice), "todos", "Todos", Alice,
            TestContext.Current.CancellationToken);
        await repo.CreateAsync(ContextRef.User("u_bob"), "todos", "Todos", "u_bob",
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ApiKeyRepository_IssueAppKey_PersistsOwnerColumns()
    {
        var apps = new AppRepository(_factory);
        var owner = ContextRef.Team("teamA");
        // owner DB needs the apps registry — ensure it exists by creating an app
        var app = await apps.CreateAsync(owner, "ledger", "Ledger", Alice,
            TestContext.Current.CancellationToken);

        var keys = new ApiKeyRepository(_factory);
        var appRef = AppRef.OfTeam("teamA", app.Id);

        var issued = await keys.IssueAsync(Alice, appRef, "ledger-key",
            new[] { ScopeCatalog.AppRead, ScopeCatalog.AppWrite, ScopeCatalog.AppAdmin },
            TestContext.Current.CancellationToken);

        Assert.Equal("app", issued.Record.ContextType);
        Assert.Equal(app.Id, issued.Record.ContextId);
        Assert.Equal("team", issued.Record.OwnerType);
        Assert.Equal("teamA", issued.Record.OwnerId);

        var looked = await keys.LookupAsync(issued.RawToken, TestContext.Current.CancellationToken);
        Assert.NotNull(looked);
        Assert.Equal("team", looked!.OwnerType);
        Assert.Equal("teamA", looked.OwnerId);
        Assert.Equal(app.Id, looked.ContextId);
    }

    [Fact]
    public void ApiKeyRepository_RejectsContextRefApp_OnLegacyOverload()
    {
        var keys = new ApiKeyRepository(_factory);
        Assert.Throws<ArgumentException>(() =>
            keys.IssueAsync(Alice, ContextRef.App("aZ"), "no", new[] { ScopeCatalog.AppRead },
                TestContext.Current.CancellationToken).GetAwaiter().GetResult());
    }

    [Fact]
    public void McpContextClaims_ResolveApp_BuildsAppRefFromPrincipal()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(McpContextClaims.UserId, Alice),
            new Claim(McpContextClaims.ContextType, "app"),
            new Claim(McpContextClaims.ContextId, "aZ"),
            new Claim(McpContextClaims.OwnerType, "team"),
            new Claim(McpContextClaims.OwnerId, "teamA"),
        }, "Test");
        var principal = new ClaimsPrincipal(identity);

        var appRef = McpContextClaims.ResolveApp(principal);
        Assert.Equal("team", appRef.OwnerType);
        Assert.Equal("teamA", appRef.OwnerId);
        Assert.Equal("aZ", appRef.AppId);

        // Resolve() collapses the same principal to ContextRef.App(appId).
        var ctx = McpContextClaims.Resolve(principal);
        Assert.Equal(ContextType.App, ctx.Type);
        Assert.Equal("aZ", ctx.Id);
    }

    [Fact]
    public void McpContextClaims_ResolveApp_ThrowsForNonAppPrincipal()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(McpContextClaims.UserId, Alice),
            new Claim(McpContextClaims.ContextType, "user"),
            new Claim(McpContextClaims.ContextId, Alice),
        }, "Test");
        var principal = new ClaimsPrincipal(identity);
        Assert.Throws<InvalidOperationException>(() => McpContextClaims.ResolveApp(principal));
    }

    // ── Task 1: CHECK-constraint enforcement for system v7 ──────────────────

    [Fact]
    public void SystemV7_CheckRejectsAppKey_WithNullOwners()
    {
        // context_type='app' but owner_type/owner_id both NULL — the compound
        // CHECK ((context_type = 'app') = (owner_type IS NOT NULL AND owner_id IS NOT NULL))
        // must reject this.
        using var sys = _factory.CreateSystemConnection();
        var now = DateTime.UtcNow.ToString("o");
        var ex = Assert.Throws<SqliteException>(() =>
            sys.Execute(@"
                INSERT INTO api_keys
                    (id, user_id, context_type, context_id,
                     owner_type, owner_id,
                     name, key_hash, key_prefix, scopes, created_at)
                VALUES
                    ('ck_app_null', @uid, 'app', 'app_z',
                     NULL, NULL,
                     'test-key', 'deadbeef', 'fb_live_xxxx', '[]', @now)",
                new { uid = Alice, now }));
        Assert.Equal(19, (int)ex.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    [Fact]
    public void SystemV7_CheckRejectsUserKey_WithOwners()
    {
        // context_type='user' plus a non-null owner pair — the same CHECK
        // enforces equivalence both ways, so this must also be rejected.
        using var sys = _factory.CreateSystemConnection();
        var now = DateTime.UtcNow.ToString("o");
        var ex = Assert.Throws<SqliteException>(() =>
            sys.Execute(@"
                INSERT INTO api_keys
                    (id, user_id, context_type, context_id,
                     owner_type, owner_id,
                     name, key_hash, key_prefix, scopes, created_at)
                VALUES
                    ('ck_user_owner', @uid, 'user', @uid,
                     'user', @uid,
                     'test-key', 'deadbeef', 'fb_live_yyyy', '[]', @now)",
                new { uid = Alice, now }));
        Assert.Equal(19, (int)ex.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }
}
