using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

public class ApiKeyAuthTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _dbFactory;
    private readonly ApiKeyRepository _keys;
    private readonly NoteRepository _notes;
    private readonly string _dataDir;
    private const string AliceId = "apikey_alice";
    private const string BobId = "apikey_bob";

    public ApiKeyAuthTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_apikey_auth_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        _dbFactory = new DatabaseFactory(_dataDir);
        _keys = new ApiKeyRepository(_dbFactory);
        _notes = new NoteRepository(_dbFactory, new TagRepository(_dbFactory));

        // FK: api_keys.user_id references users(id). TestAuthHandler isn't in
        // play here — seed the user row directly. Also seed the profile so
        // /api/v1/me works for endpoints that consume the user record.
        using (var db = _dbFactory.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute(
                "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[]
                {
                    new { id = AliceId, n = "Alice", e = "a@a", now },
                    new { id = BobId,   n = "Bob",   e = "b@b", now },
                });
        }

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton<DatabaseFactory>(_dbFactory);
            });
        });
    }

    private HttpClient ClientWithToken(string? rawToken)
    {
        var client = _factory.CreateClient();
        if (rawToken is not null)
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", rawToken);
        return client;
    }

    [Fact]
    public async Task NoAuthorizationHeader_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/auth/whoami",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task InvalidBearerToken_Returns401()
    {
        var client = ClientWithToken("fb_live_this_is_not_a_real_token_xyz");
        var resp = await client.GetAsync("/api/v1/auth/whoami",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task NonFishbowlBearerToken_Returns401()
    {
        // Doesn't match our prefix — handler returns NoResult, cookie has nothing,
        // so the request falls through to 401.
        var client = ClientWithToken("not-ours-at-all");
        var resp = await client.GetAsync("/api/v1/auth/whoami",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task RevokedBearerToken_Returns401()
    {
        var issued = await _keys.IssueAsync(AliceId, ContextRef.User(AliceId), "to-be-revoked",
            new[] { "read:notes" }, TestContext.Current.CancellationToken);
        await _keys.RevokeAsync(issued.Record.Id, AliceId, TestContext.Current.CancellationToken);

        var client = ClientWithToken(issued.RawToken);
        var resp = await client.GetAsync("/api/v1/auth/whoami",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ValidTokenUserContext_PrincipalHasUserClaims()
    {
        var issued = await _keys.IssueAsync(AliceId, ContextRef.User(AliceId), "personal-key",
            new[] { "read:notes", "write:notes" }, TestContext.Current.CancellationToken);

        var client = ClientWithToken(issued.RawToken);
        var resp = await client.GetAsync("/api/v1/auth/whoami",
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync<WhoAmIDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        Assert.Equal(AliceId, payload!.UserId);
        Assert.Equal("user", payload.Context.Type);
        Assert.Equal(AliceId, payload.Context.Id);
        Assert.Contains("read:notes", payload.Scopes);
        Assert.Contains("write:notes", payload.Scopes);
        Assert.Equal("ApiKey", payload.AuthType);
    }

    [Fact]
    public async Task ValidTokenSpaceContext_PrincipalHasSpaceClaims()
    {
        var issued = await _keys.IssueAsync(AliceId, ContextRef.Space("fishbowl-dev"),
            "space-key", new[] { "read:notes" }, TestContext.Current.CancellationToken);

        var client = ClientWithToken(issued.RawToken);
        var resp = await client.GetAsync("/api/v1/auth/whoami",
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync<WhoAmIDto>(
            TestContext.Current.CancellationToken);
        Assert.Equal("space", payload!.Context.Type);
        Assert.Equal("fishbowl-dev", payload.Context.Id);
        Assert.Equal(AliceId, payload.UserId);
        Assert.Single(payload.Scopes, "read:notes");
    }

    [Fact]
    public async Task ValidBearer_ReadsOwnNotesThroughRealEndpoint()
    {
        // End-to-end proof that a Bearer-authenticated request reaches a
        // real repository-backed endpoint and sees the caller's data. whoami
        // only proves the claims are set; this proves the principal actually
        // flows into the request pipeline the way cookie auth does.
        await _notes.CreateAsync(AliceId, new Note { Title = "alice-only" },
            TestContext.Current.CancellationToken);

        var issued = await _keys.IssueAsync(AliceId, ContextRef.User(AliceId), "rw",
            new[] { "read:notes", "write:notes" }, TestContext.Current.CancellationToken);

        var client = ClientWithToken(issued.RawToken);
        var resp = await client.GetAsync("/api/v1/notes", TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var list = await resp.Content.ReadFromJsonAsync<List<Note>>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(list);
        Assert.Single(list!);
        Assert.Equal("alice-only", list![0].Title);
    }

    [Fact]
    public async Task BearerBoundToAlice_CannotSeeBobsNotes()
    {
        // The hardest isolation property — a token MUST only reach the data
        // of the user it was issued against. Failure here would be a silent
        // cross-tenant leak.
        await _notes.CreateAsync(AliceId, new Note { Title = "alice-secret" },
            TestContext.Current.CancellationToken);
        await _notes.CreateAsync(BobId, new Note { Title = "bob-secret" },
            TestContext.Current.CancellationToken);

        var alicesKey = await _keys.IssueAsync(AliceId, ContextRef.User(AliceId), "rw",
            new[] { "read:notes" }, TestContext.Current.CancellationToken);

        var client = ClientWithToken(alicesKey.RawToken);
        var resp = await client.GetAsync("/api/v1/notes", TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("alice-secret", raw);
        Assert.DoesNotContain("bob-secret", raw);
    }

    [Fact]
    public async Task RevokedBearer_DeniedAtNotesEndpoint()
    {
        // Revocation must take effect immediately — no cached-principal
        // window where a revoked token still reads data.
        await _notes.CreateAsync(AliceId, new Note { Title = "timestamped" },
            TestContext.Current.CancellationToken);

        var issued = await _keys.IssueAsync(AliceId, ContextRef.User(AliceId), "short-lived",
            new[] { "read:notes" }, TestContext.Current.CancellationToken);

        var client = ClientWithToken(issued.RawToken);

        // Works before revocation.
        var ok = await client.GetAsync("/api/v1/notes", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        await _keys.RevokeAsync(issued.Record.Id, AliceId, TestContext.Current.CancellationToken);

        // Same token, same client — blocked now.
        var denied = await client.GetAsync("/api/v1/notes", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task ValidBearer_CanWriteNotes_AndWrittenNoteComesBackInList()
    {
        var issued = await _keys.IssueAsync(AliceId, ContextRef.User(AliceId), "writer",
            new[] { "read:notes", "write:notes" }, TestContext.Current.CancellationToken);

        var client = ClientWithToken(issued.RawToken);

        var post = await client.PostAsJsonAsync("/api/v1/notes",
            new { title = "written-via-bearer" },
            TestContext.Current.CancellationToken);
        post.EnsureSuccessStatusCode();

        var list = await client.GetFromJsonAsync<List<Note>>("/api/v1/notes",
            TestContext.Current.CancellationToken);
        Assert.Contains(list!, n => n.Title == "written-via-bearer");

        // Note must land in Alice's user DB, not a space DB.
        var aliceDbPath = Path.Combine(_dataDir, "users", AliceId, "personal.db");
        Assert.True(File.Exists(aliceDbPath), $"expected {aliceDbPath} to exist");
    }

    [Fact]
    public async Task BearerWithReadOnlyScope_CanRead_But403OnWrite()
    {
        var issued = await _keys.IssueAsync(AliceId, ContextRef.User(AliceId), "ro",
            new[] { "read:notes" }, TestContext.Current.CancellationToken);

        var client = ClientWithToken(issued.RawToken);

        var getResp = await client.GetAsync("/api/v1/notes", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var postResp = await client.PostAsJsonAsync("/api/v1/notes",
            new { title = "should-be-rejected" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, postResp.StatusCode);
    }

    [Fact]
    public async Task BearerSpaceScope_GetNotes_ReturnsSpaceNotes_NotPersonal()
    {
        // Seed Alice's PERSONAL note and a SPACE note. Key is space-scoped, so
        // /api/v1/notes must resolve to the space context, not personal.
        await _notes.CreateAsync(ContextRef.User(AliceId), AliceId,
            new Note { Title = "alice-personal" }, TestContext.Current.CancellationToken);

        var spaceRepo = new SpaceRepository(_dbFactory);
        var space = await spaceRepo.CreateAsync(AliceId, "Space Context Test",
            TestContext.Current.CancellationToken);
        await _notes.CreateAsync(ContextRef.Space(space.Slug), AliceId,
            new Note { Title = "space-shared" }, TestContext.Current.CancellationToken);

        var issued = await _keys.IssueAsync(AliceId, ContextRef.Space(space.Slug), "space-key",
            new[] { "read:notes" }, TestContext.Current.CancellationToken);

        var client = ClientWithToken(issued.RawToken);
        var resp = await client.GetAsync("/api/v1/notes", TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("space-shared", raw);
        Assert.DoesNotContain("alice-personal", raw);
    }

    [Fact]
    public async Task PersonalBearer_CannotAccessSpaceNotesUrl()
    {
        var spaceRepo = new SpaceRepository(_dbFactory);
        var space = await spaceRepo.CreateAsync(AliceId, "Alice Only",
            TestContext.Current.CancellationToken);

        // Alice owns the space but mints a PERSONAL-scoped key. The
        // authoritative scope is the token's, not the human behind it —
        // hitting the space URL must 403 even though the user is the owner.
        var issued = await _keys.IssueAsync(AliceId, ContextRef.User(AliceId), "personal",
            new[] { "read:notes" }, TestContext.Current.CancellationToken);

        var client = ClientWithToken(issued.RawToken);
        var resp = await client.GetAsync($"/api/v1/spaces/{space.Slug}/notes",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task SpaceBearerForSpaceA_CannotAccessSpaceBNotes()
    {
        var spaceRepo = new SpaceRepository(_dbFactory);
        var spaceA = await spaceRepo.CreateAsync(AliceId, "Space A",
            TestContext.Current.CancellationToken);
        var spaceB = await spaceRepo.CreateAsync(AliceId, "Space B",
            TestContext.Current.CancellationToken);

        var keyA = await _keys.IssueAsync(AliceId, ContextRef.Space(spaceA.Slug), "a-key",
            new[] { "read:notes" }, TestContext.Current.CancellationToken);

        var client = ClientWithToken(keyA.RawToken);

        var okResp = await client.GetAsync($"/api/v1/spaces/{spaceA.Slug}/notes",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);

        var forbidResp = await client.GetAsync($"/api/v1/spaces/{spaceB.Slug}/notes",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidResp.StatusCode);
    }

    [Fact]
    public async Task BearerWithoutTagsScope_CannotListTags()
    {
        var issued = await _keys.IssueAsync(AliceId, ContextRef.User(AliceId), "notes-only",
            new[] { "read:notes" }, TestContext.Current.CancellationToken);

        var client = ClientWithToken(issued.RawToken);
        var resp = await client.GetAsync("/api/v1/tags", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // Export endpoint is cookie-only (same rule as /api/v1/search/reindex).
    // A valid Bearer token with full scopes still gets 403.
    [Fact]
    public async Task Export_BearerToken_Returns403()
    {
        var issued = await _keys.IssueAsync(AliceId, ContextRef.User(AliceId), "export-bearer",
            new[] { "read:notes" }, TestContext.Current.CancellationToken);

        var client = ClientWithToken(issued.RawToken);
        var resp = await client.GetAsync("/api/v1/export/db",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ValidToken_UpdatesLastUsedAt()
    {
        var issued = await _keys.IssueAsync(BobId, ContextRef.User(BobId), "touched",
            new[] { "read:notes" }, TestContext.Current.CancellationToken);

        var client = ClientWithToken(issued.RawToken);
        var resp = await client.GetAsync("/api/v1/auth/whoami",
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        // Fire-and-forget — give it a tick to land before querying.
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var after = (await _keys.ListByUserAsync(BobId, TestContext.Current.CancellationToken))
            .Single(k => k.Id == issued.Record.Id);
        Assert.NotNull(after.LastUsedAt);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }

    private record ContextDto(string Type, string Id);
    private record WhoAmIDto(string UserId, ContextDto Context, List<string> Scopes, string? AuthType);
}
