using System.Net;
using System.Net.Http.Json;
using Dapper;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

public class SpacesApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dataDir;
    private const string UserA = "spaces_user_a";
    private const string UserB = "spaces_user_b";

    public SpacesApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_spaces_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        var testFactory = new DatabaseFactory(_dataDir);

        // spaces.created_by / space_members.user_id both FK to users(id), but
        // TestAuthHandler only synthesises claims — it never writes the user
        // row. Pre-seed both test users so the FKs are satisfied.
        using (var db = testFactory.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute(
                "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[]
                {
                    new { id = UserA, n = "A", e = "a@a", now },
                    new { id = UserB, n = "B", e = "b@b", now },
                });
        }

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (dbDescriptor != null) services.Remove(dbDescriptor);
                services.AddSingleton<DatabaseFactory>(testFactory);

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

    private HttpRequestMessage Req(HttpMethod method, string path, string user, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(TestAuthHandler.UserIdHeader, user);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    [Fact]
    public async Task CreateSpace_Succeeds_AndSlugIsDerivedFromName()
    {
        var client = _factory.CreateClient();

        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Fishbowl Dev" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var space = await resp.Content.ReadFromJsonAsync<Space>(TestContext.Current.CancellationToken);
        Assert.NotNull(space);
        Assert.Equal("fishbowl-dev", space!.Slug);
        Assert.Equal("Fishbowl Dev", space.Name);
    }

    [Fact]
    public async Task ListSpaces_ReturnsOnlyMySpaces()
    {
        var client = _factory.CreateClient();

        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Mine" }),
            TestContext.Current.CancellationToken);
        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces", UserB, new { name = "Theirs" }),
            TestContext.Current.CancellationToken);

        var resp = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/spaces", UserA),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var list = await resp.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(list);
        Assert.Single(list!);
        Assert.Equal("mine", list![0]["slug"].ToString());
        Assert.Equal("owner", list[0]["role"].ToString());
    }

    [Fact]
    public async Task GetSpace_NonMember_403()
    {
        var client = _factory.CreateClient();

        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Private" }),
            TestContext.Current.CancellationToken);

        var resp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/spaces/private", UserB),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task GetSpace_UnknownSlug_404()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/spaces/nope", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteSpace_NonOwner_403()
    {
        var client = _factory.CreateClient();

        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Shared" }),
            TestContext.Current.CancellationToken);

        var del = await client.SendAsync(Req(HttpMethod.Delete, "/api/v1/spaces/shared", UserB),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
    }

    [Fact]
    public async Task DeleteSpace_Owner_204()
    {
        var client = _factory.CreateClient();

        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Doomed" }),
            TestContext.Current.CancellationToken);

        var del = await client.SendAsync(Req(HttpMethod.Delete, "/api/v1/spaces/doomed", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
    }

    [Fact]
    public async Task SpaceNotes_AreIsolatedFromPersonalNotes()
    {
        var client = _factory.CreateClient();

        // Personal note for UserA.
        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/notes", UserA,
            new Note { Title = "personal-only" }),
            TestContext.Current.CancellationToken);

        // Create space + space note.
        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Project X" }),
            TestContext.Current.CancellationToken);
        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces/project-x/notes", UserA,
            new Note { Title = "space-only" }),
            TestContext.Current.CancellationToken);

        // Personal list must not include the space note.
        var personalResp = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/notes", UserA),
            TestContext.Current.CancellationToken);
        var personal = await personalResp.Content.ReadFromJsonAsync<List<Note>>(
            TestContext.Current.CancellationToken);
        Assert.Single(personal!);
        Assert.Equal("personal-only", personal![0].Title);

        // Space list must not include the personal note.
        var spaceResp = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/spaces/project-x/notes", UserA),
            TestContext.Current.CancellationToken);
        var spaceNotes = await spaceResp.Content.ReadFromJsonAsync<List<Note>>(
            TestContext.Current.CancellationToken);
        Assert.Single(spaceNotes!);
        Assert.Equal("space-only", spaceNotes![0].Title);
    }

    [Fact]
    public async Task SpaceNotes_NonMemberCannotRead()
    {
        var client = _factory.CreateClient();

        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Secret Club" }),
            TestContext.Current.CancellationToken);
        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces/secret-club/notes", UserA,
            new Note { Title = "hidden" }),
            TestContext.Current.CancellationToken);

        var resp = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/spaces/secret-club/notes", UserB),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task SpacesApi_Unauthenticated_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/spaces",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }
}
