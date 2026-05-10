using System.Net;
using System.Net.Http.Json;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Fishbowl.Host.Tests;

// Cold DB import: operator drops a `users/<folder>/personal.db` onto the
// data dir, then POSTs /api/v1/admin/users/import to register it. These
// tests use TestAuthHandler (X-Test-User-Id header) for cookie-style auth
// and seed the user as is_admin=1 directly in system.db.
public class AdminImportTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _testDataDir;

    public AdminImportTests(WebApplicationFactory<Program> factory)
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "fishbowl_admin_import_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_testDataDir);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (dbDescriptor != null) services.Remove(dbDescriptor);
                services.AddSingleton<DatabaseFactory>(new DatabaseFactory(_testDataDir));

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.AuthenticationScheme, _ => { });
            });
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    private async Task<string> SeedAdminUserAsync(string userId, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        await repo.CreateUserAsync(userId, "Admin", "admin@local", null, ct);
        await repo.SetAdminAsync(userId, isAdmin: true, ct);
        return userId;
    }

    private async Task<string> SeedNonAdminUserAsync(string userId, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        await repo.CreateUserAsync(userId, "User", "user@local", null, ct);
        return userId;
    }

    private void SeedFolderOnDisk(string folderName)
    {
        var dir = Path.Combine(_testDataDir, "users", folderName);
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "personal.db");

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        // Minimal v1 schema — the factory will roll forward on import.
        conn.Execute(@"
            CREATE TABLE notes (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, content TEXT,
                content_secret BLOB, type TEXT NOT NULL DEFAULT 'note', tags TEXT,
                created_by TEXT NOT NULL, created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL, pinned INTEGER DEFAULT 0,
                archived INTEGER DEFAULT 0);
            CREATE VIRTUAL TABLE notes_fts USING fts5(title, content, tags);
            PRAGMA user_version = 1;");
        SqliteConnection.ClearAllPools();
    }

    private HttpClient ClientAs(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", userId);
        return client;
    }

    [Fact]
    public async Task NonAdmin_GetsForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        var bob = await SeedNonAdminUserAsync("bob", ct);
        SeedFolderOnDisk("incoming-1");

        var resp = await ClientAs(bob).PostAsJsonAsync("/api/v1/admin/users/import",
            new { FolderName = "incoming-1", Username = "alice", Password = "supersecret-password" },
            ct);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_HappyPath_RegistersUserAndCreatesLocalLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await SeedAdminUserAsync("admin-1", ct);
        SeedFolderOnDisk("incoming-1");

        var resp = await ClientAs(admin).PostAsJsonAsync("/api/v1/admin/users/import",
            new { FolderName = "incoming-1", Username = "alice", Password = "supersecret-password" },
            ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ImportResult>(ct);
        Assert.NotNull(body);
        Assert.Equal("incoming-1", body!.UserId);
        Assert.Equal("alice", body.Username);

        // The user can sign in.
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        var imported = await repo.GetUserByLocalUsernameAsync("alice", ct);
        Assert.NotNull(imported);
        Assert.Equal("incoming-1", imported!.Id);
        Assert.False(string.IsNullOrEmpty(imported.PasswordHash));
    }

    [Fact]
    public async Task Admin_FolderDoesntExist_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await SeedAdminUserAsync("admin-1", ct);

        var resp = await ClientAs(admin).PostAsJsonAsync("/api/v1/admin/users/import",
            new { FolderName = "ghost", Username = "alice", Password = "supersecret-password" },
            ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_FolderNameWithTraversal_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await SeedAdminUserAsync("admin-1", ct);

        var resp = await ClientAs(admin).PostAsJsonAsync("/api/v1/admin/users/import",
            new { FolderName = "../system", Username = "alice", Password = "supersecret-password" },
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_UserIdAlreadyRegistered_ReturnsConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await SeedAdminUserAsync("admin-1", ct);
        await SeedAdminUserAsync("incoming-1", ct);  // already registered
        SeedFolderOnDisk("incoming-1");

        var resp = await ClientAs(admin).PostAsJsonAsync("/api/v1/admin/users/import",
            new { FolderName = "incoming-1", Username = "alice", Password = "supersecret-password" },
            ct);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_UsernameAlreadyTaken_ReturnsConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await SeedAdminUserAsync("admin-1", ct);

        // Squat on the username via local auth.
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
            await repo.CreateUserAsync("squatter", "Squatter", null, null, ct);
            await repo.CreateUserMappingAsync("squatter", "local", "alice", ct);
        }

        SeedFolderOnDisk("incoming-1");

        var resp = await ClientAs(admin).PostAsJsonAsync("/api/v1/admin/users/import",
            new { FolderName = "incoming-1", Username = "alice", Password = "supersecret-password" },
            ct);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task ListImportable_OnlyShowsUnregisteredFolders()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await SeedAdminUserAsync("admin-1", ct);

        SeedFolderOnDisk("incoming-1");
        SeedFolderOnDisk("incoming-2");
        // Pretend incoming-2 is already registered.
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
            await repo.CreateUserAsync("incoming-2", "Already", null, null, ct);
        }

        var resp = await ClientAs(admin).GetAsync("/api/v1/admin/users/importable", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var rows = await resp.Content.ReadFromJsonAsync<List<ImportableRow>>(ct);
        Assert.NotNull(rows);
        Assert.Single(rows!);
        Assert.Equal("incoming-1", rows![0].FolderName);
    }

    private sealed record ImportResult(string UserId, string Username, string Message);
    private sealed record ImportableRow(string FolderName, long DbBytes);
}
