using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Auth;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// Admin phase A3 over HTTP: deleting an account (archive first by default,
// blocked by spaces it alone owns, never yourself or the last admin), what
// goes with it, the tombstone that ends a session outliving the delete, and
// the System page's numbers.
public class AdminLifecycleApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dir;
    private readonly DatabaseFactory _db;
    private readonly SystemRepository _system;
    private readonly ApiKeyRepository _keys;

    public AdminLifecycleApiTests(WebApplicationFactory<Program> factory)
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_admin_lifecycle_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _db = new DatabaseFactory(_dir);
        _system = new SystemRepository(_db);
        _keys = new ApiKeyRepository(_db);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton(_db);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.AuthenticationScheme, _ => { });
            });
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient As(string userId)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
        return client;
    }

    private async Task SeedAdminAsync(string id)
    {
        await _system.CreateUserAsync(id, "Admin " + id, id + "@admins.example", null, Ct);
        await _system.SetAdminAsync(id, true, Ct);
    }

    // An account with a personal DB (one note), a file, a sign-in mapping,
    // an API key, a space membership and a message.
    private async Task<string> SeedMemberWithDataAsync(string id)
    {
        await _system.CreateUserAsync(id, "Member " + id, id + "@example.com", null, Ct);
        await _system.CreateUserMappingAsync(id, "google", "g-" + id, Ct);
        using (var conn = _db.CreateContextConnection(ContextRef.User(id)))
            conn.Execute("INSERT INTO notes (id, title, created_by, created_at, updated_at) VALUES ('n1', 'private', @u, @t, @t)",
                new { u = id, t = DateTime.UtcNow.ToString("o") });
        var files = Path.Combine(_db.ResolveContextFolder(ContextRef.User(id)), "files", "docs");
        Directory.CreateDirectory(files);
        await File.WriteAllTextAsync(Path.Combine(files, "a.txt"), "hello", Ct);
        await _keys.IssueAsync(id, ContextRef.User(id), "k", new[] { "read:notes" }, Ct);
        using (var sys = _db.CreateSystemConnection())
        {
            sys.Execute("INSERT INTO messages (id, recipient_id, kind, created_at) VALUES (@m, @u, 'user.approved', @t)",
                new { m = "msg-" + id, u = id, t = DateTime.UtcNow.ToString("o") });
            sys.Execute("INSERT INTO system_config (key, value, updated_at) VALUES (@k, '2026-09-27', @t)",
                new { k = "Digest:LastSent:" + id, t = DateTime.UtcNow.ToString("o") });
        }
        return id;
    }

    private string SeedSpace(string slug, params (string User, string Role)[] members)
    {
        var id = "space-" + slug;
        var now = DateTime.UtcNow.ToString("o");
        using var sys = _db.CreateSystemConnection();
        sys.Execute("INSERT INTO spaces (id, slug, name, created_by, created_at) VALUES (@id, @slug, @name, @by, @now)",
            new { id, slug, name = "Space " + slug, by = members[0].User, now });
        foreach (var (user, role) in members)
            sys.Execute("INSERT INTO space_members (space_id, user_id, role, joined_at) VALUES (@id, @user, @role, @now)",
                new { id, user, role, now });
        return id;
    }

    private long Count(string sql, object? args = null)
    {
        using var sys = _db.CreateSystemConnection();
        return sys.ExecuteScalar<long>(sql, args);
    }

    private string UserArchiveRoot => Path.Combine(_dir, "archive", "users");

    [Fact]
    public async Task Delete_ArchivesFirst_ThenRemovesEverything_AndAudits()
    {
        await SeedAdminAsync("boss");
        await SeedMemberWithDataAsync("ann");
        var shared = SeedSpace("shared", ("boss", "owner"), ("ann", "member"));

        var resp = await As("boss").DeleteAsync("/api/v1/admin/users/ann", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var archiveId = body.GetProperty("archive").GetProperty("id").GetString()!;
        Assert.StartsWith("ann-", archiveId);

        // The ZIP: manifest, personal.db, the file — and it reads back.
        var zipPath = Path.Combine(UserArchiveRoot, archiveId + ".zip");
        Assert.True(File.Exists(zipPath));
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("manifest.json", names);
            Assert.Contains("personal.db", names);
            Assert.Contains("files/docs/a.txt", names);
            using var m = zip.GetEntry("manifest.json")!.Open();
            var manifest = await JsonSerializer.DeserializeAsync<JsonElement>(m, cancellationToken: Ct);
            Assert.Equal("ann", manifest.GetProperty("userId").GetString());
            Assert.Equal("boss", manifest.GetProperty("archivedBy").GetString());
        }

        // Rows, keys, membership, messages, latches and the folder are gone.
        Assert.Null(await _system.GetUserAsync("ann", Ct));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM user_mappings WHERE user_id = 'ann'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM api_keys WHERE user_id = 'ann'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM space_members WHERE user_id = 'ann'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM space_members WHERE space_id = @shared", new { shared }));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM messages WHERE recipient_id = 'ann'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM system_config WHERE key = 'Digest:LastSent:ann'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM deleted_users WHERE id = 'ann'"));
        Assert.False(Directory.Exists(_db.ResolveContextFolder(ContextRef.User("ann"))));

        // One audit row, ids only.
        Assert.Equal(1, Count("SELECT COUNT(*) FROM admin_audit WHERE action = 'user.delete' AND actor_id = 'boss' AND target_id = 'ann'"));
    }

    [Fact]
    public async Task Delete_WithoutArchive_KeepsNothing()
    {
        await SeedAdminAsync("boss");
        await SeedMemberWithDataAsync("bob");

        var resp = await As("boss").DeleteAsync("/api/v1/admin/users/bob?archive=false", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("archive").ValueKind);
        Assert.False(Directory.Exists(UserArchiveRoot) && Directory.EnumerateFiles(UserArchiveRoot, "bob-*.zip").Any());
        Assert.Null(await _system.GetUserAsync("bob", Ct));
        Assert.False(Directory.Exists(_db.ResolveContextFolder(ContextRef.User("bob"))));
    }

    [Fact]
    public async Task Delete_IsBlocked_BySpacesTheAccountAloneOwns()
    {
        await SeedAdminAsync("boss");
        await SeedMemberWithDataAsync("cat");
        SeedSpace("mine", ("cat", "owner"));
        SeedSpace("co-owned", ("cat", "owner"), ("boss", "owner"));

        var check = await (await As("boss").GetAsync("/api/v1/admin/users/cat/delete-check", Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        var owned = check.GetProperty("ownedSpaces").EnumerateArray().Select(s => s.GetProperty("slug").GetString()).ToList();
        Assert.Equal(new[] { "mine" }, owned);
        Assert.True(check.GetProperty("hasData").GetBoolean());

        var resp = await As("boss").DeleteAsync("/api/v1/admin/users/cat", Ct);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("owns-spaces", body.GetProperty("error").GetString());
        // Nothing happened: the account, its folder and no archive.
        Assert.NotNull(await _system.GetUserAsync("cat", Ct));
        Assert.True(Directory.Exists(_db.ResolveContextFolder(ContextRef.User("cat"))));
        Assert.False(Directory.Exists(UserArchiveRoot) && Directory.EnumerateFiles(UserArchiveRoot).Any());
    }

    [Fact]
    public async Task Delete_RefusesYourself_NonAdmins_AndUnknownIds()
    {
        await SeedAdminAsync("boss");
        await SeedAdminAsync("deputy");
        await SeedMemberWithDataAsync("dan");

        Assert.Equal(HttpStatusCode.BadRequest, (await As("boss").DeleteAsync("/api/v1/admin/users/boss", Ct)).StatusCode);
        var check = await (await As("boss").GetAsync("/api/v1/admin/users/boss/delete-check", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(check.GetProperty("self").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await As("dan").DeleteAsync("/api/v1/admin/users/deputy", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As("boss").DeleteAsync("/api/v1/admin/users/nobody", Ct)).StatusCode);

        // Another admin may go while one active admin remains. (The last
        // active admin can only be the caller, who can't delete themselves.)
        Assert.Equal(HttpStatusCode.OK, (await As("boss").DeleteAsync("/api/v1/admin/users/deputy?archive=false", Ct)).StatusCode);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM users WHERE is_admin = 1 AND state = 'active'"));
    }

    [Fact]
    public async Task DeletedAccount_SessionEnds_AndItsFolderNeverComesBack()
    {
        await SeedAdminAsync("boss");
        await SeedMemberWithDataAsync("gus");
        Assert.Equal(HttpStatusCode.OK, (await As("gus").GetAsync("/api/v1/notes", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await As("boss").DeleteAsync("/api/v1/admin/users/gus?archive=false", Ct)).StatusCode);

        // The session that outlived the delete is refused at its next request…
        var resp = await As("gus").GetAsync("/api/v1/notes", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("deleted", (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        // …and nothing recreates the personal DB.
        Assert.Throws<InactiveAccountException>(() => _db.CreateContextConnection(ContextRef.User("gus")));
        Assert.False(File.Exists(Path.Combine(_db.ResolveContextFolder(ContextRef.User("gus")), DatabaseFactory.PersonalDbFileName)));
    }

    [Fact]
    public async Task System_ReportsVersion_Sizes_Archives_AndStatus()
    {
        await SeedAdminAsync("boss");
        await SeedMemberWithDataAsync("hal");
        Assert.Equal(HttpStatusCode.OK, (await As("boss").DeleteAsync("/api/v1/admin/users/hal", Ct)).StatusCode);

        var resp = await As("boss").GetAsync("/api/v1/admin/system", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var s = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.False(string.IsNullOrEmpty(s.GetProperty("version").GetString()));
        Assert.Equal(1, s.GetProperty("data").GetProperty("users").GetProperty("count").GetInt32());
        Assert.True(s.GetProperty("data").GetProperty("systemBytes").GetInt64() > 0);
        Assert.Equal(1, s.GetProperty("archives").GetProperty("users").GetProperty("count").GetInt32());
        Assert.True(s.GetProperty("archives").GetProperty("users").GetProperty("bytes").GetInt64() > 0);
        Assert.Equal(30, s.GetProperty("archives").GetProperty("retentionDays").GetInt32());
        Assert.Contains(s.GetProperty("embedding").GetProperty("status").GetString(), new[] { "ready", "downloading", "off" });
        Assert.True(s.TryGetProperty("scheduler", out _));

        // Non-admins don't see it.
        await _system.CreateUserAsync("ivy", "Ivy", null, null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("ivy").GetAsync("/api/v1/admin/system", Ct)).StatusCode);
    }
}
