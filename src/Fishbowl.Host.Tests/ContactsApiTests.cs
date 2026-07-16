using System.Net;
using System.Net.Http.Json;
using Dapper;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

public class ContactsApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dataDir;
    private const string UserA = "contacts_user_a";
    private const string UserB = "contacts_user_b";

    public ContactsApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(),
            "fishbowl_contacts_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        var testFactory = new DatabaseFactory(_dataDir);

        // Seed users so space-creation FKs are satisfied by the space tests.
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

    private HttpRequestMessage Req(HttpMethod method, string path, string? user, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (user is not null) req.Headers.Add(TestAuthHandler.UserIdHeader, user);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private async Task<Contact> CreateAsync(HttpClient client, string user, string name, string? email = null)
    {
        var resp = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/contacts", user,
            new Contact { Name = name, Email = email }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var contact = await resp.Content.ReadFromJsonAsync<Contact>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(contact);
        return contact!;
    }

    [Fact]
    public async Task ContactsApi_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/contacts", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsEmptyName_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/contacts", UserA, new Contact { Name = "  " }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_Returns201_WithLocation()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/contacts", UserA,
                new Contact { Name = "Alice", Email = "alice@example.com" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.NotNull(resp.Headers.Location);
        Assert.StartsWith("/api/v1/contacts/", resp.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task List_HidesArchived_ByDefault()
    {
        var client = _factory.CreateClient();
        await CreateAsync(client, UserA, "Active");
        var archived = await CreateAsync(client, UserA, "Gone");

        archived.Archived = true;
        var upd = await client.SendAsync(
            Req(HttpMethod.Put, $"/api/v1/contacts/{archived.Id}", UserA, archived),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, upd.StatusCode);

        var defResp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/contacts", UserA),
            TestContext.Current.CancellationToken);
        defResp.EnsureSuccessStatusCode();
        var @default = await defResp.Content.ReadFromJsonAsync<List<Contact>>(
            TestContext.Current.CancellationToken);
        Assert.Single(@default!);
        Assert.Equal("Active", @default![0].Name);

        var allResp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/contacts?includeArchived=true", UserA),
            TestContext.Current.CancellationToken);
        var all = await allResp.Content.ReadFromJsonAsync<List<Contact>>(
            TestContext.Current.CancellationToken);
        Assert.Equal(2, all!.Count);
    }

    [Fact]
    public async Task Get_Returns200_WhenPresent()
    {
        var client = _factory.CreateClient();
        var created = await CreateAsync(client, UserA, "Bob", "b@b");

        var resp = await client.SendAsync(
            Req(HttpMethod.Get, $"/api/v1/contacts/{created.Id}", UserA),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var fetched = await resp.Content.ReadFromJsonAsync<Contact>(
            TestContext.Current.CancellationToken);
        Assert.Equal(created.Id, fetched!.Id);
        Assert.Equal("Bob", fetched.Name);
        Assert.Equal("b@b", fetched.Email);
    }

    [Fact]
    public async Task Get_Returns404_WhenMissing()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/contacts/01HZ_MISSING", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Update_PersistsAcrossGet()
    {
        var client = _factory.CreateClient();
        var created = await CreateAsync(client, UserA, "Before");

        created.Name = "After";
        created.Phone = "+49";
        var upd = await client.SendAsync(
            Req(HttpMethod.Put, $"/api/v1/contacts/{created.Id}", UserA, created),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, upd.StatusCode);

        var fetched = await client.SendAsync(
            Req(HttpMethod.Get, $"/api/v1/contacts/{created.Id}", UserA),
            TestContext.Current.CancellationToken);
        var item = await fetched.Content.ReadFromJsonAsync<Contact>(
            TestContext.Current.CancellationToken);
        Assert.Equal("After", item!.Name);
        Assert.Equal("+49", item.Phone);
    }

    [Fact]
    public async Task Delete_Returns204_AndItemGone()
    {
        var client = _factory.CreateClient();
        var created = await CreateAsync(client, UserA, "Temporary");

        var del = await client.SendAsync(
            Req(HttpMethod.Delete, $"/api/v1/contacts/{created.Id}", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await client.SendAsync(
            Req(HttpMethod.Get, $"/api/v1/contacts/{created.Id}", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Isolation_BetweenUsers()
    {
        var client = _factory.CreateClient();
        await CreateAsync(client, UserA, "alice");
        await CreateAsync(client, UserB, "bob");

        var aResp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/contacts", UserA),
            TestContext.Current.CancellationToken);
        var aList = await aResp.Content.ReadFromJsonAsync<List<Contact>>(
            TestContext.Current.CancellationToken);
        Assert.Single(aList!);
        Assert.Equal("alice", aList![0].Name);

        var bResp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/contacts", UserB),
            TestContext.Current.CancellationToken);
        var bList = await bResp.Content.ReadFromJsonAsync<List<Contact>>(
            TestContext.Current.CancellationToken);
        Assert.Single(bList!);
        Assert.Equal("bob", bList![0].Name);
    }

    [Fact]
    public async Task SpaceContacts_AreIsolatedFromPersonal()
    {
        var client = _factory.CreateClient();

        await CreateAsync(client, UserA, "personal-only");
        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Contacts Space" }),
            TestContext.Current.CancellationToken);
        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces/contacts-space/contacts", UserA,
            new Contact { Name = "space-only" }),
            TestContext.Current.CancellationToken);

        var personal = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/contacts", UserA),
            TestContext.Current.CancellationToken);
        var personalList = await personal.Content.ReadFromJsonAsync<List<Contact>>(
            TestContext.Current.CancellationToken);
        Assert.Single(personalList!);
        Assert.Equal("personal-only", personalList![0].Name);

        var space = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/spaces/contacts-space/contacts", UserA),
            TestContext.Current.CancellationToken);
        var spaceList = await space.Content.ReadFromJsonAsync<List<Contact>>(
            TestContext.Current.CancellationToken);
        Assert.Single(spaceList!);
        Assert.Equal("space-only", spaceList![0].Name);
    }

    [Fact]
    public async Task SpaceContacts_NonMember_Returns403()
    {
        var client = _factory.CreateClient();
        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Private C" }),
            TestContext.Current.CancellationToken);

        var resp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/spaces/private-c/contacts", UserB),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Search_ReturnsHits_FromAnyIndexedField()
    {
        var client = _factory.CreateClient();
        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/contacts", UserA,
            new Contact { Name = "Alice", Email = "alice@studio.example", Notes = "venue manager" }),
            TestContext.Current.CancellationToken);
        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/contacts", UserA,
            new Contact { Name = "Bob", Email = "bob@home.example", Notes = "friend" }),
            TestContext.Current.CancellationToken);

        var resp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/contacts/search?q=venue", UserA),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var hits = await resp.Content.ReadFromJsonAsync<List<Contact>>(
            TestContext.Current.CancellationToken);
        Assert.Single(hits!);
        Assert.Equal("Alice", hits![0].Name);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmpty()
    {
        var client = _factory.CreateClient();
        await CreateAsync(client, UserA, "Anyone");

        var resp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/contacts/search?q=", UserA),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var hits = await resp.Content.ReadFromJsonAsync<List<Contact>>(
            TestContext.Current.CancellationToken);
        Assert.Empty(hits!);
    }

    [Fact]
    public async Task SpaceSearch_NonMember_Returns403()
    {
        var client = _factory.CreateClient();
        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Search Private" }),
            TestContext.Current.CancellationToken);

        var resp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/spaces/search-private/contacts/search?q=anything", UserB),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task SpaceContacts_Readonly_CannotWrite()
    {
        var client = _factory.CreateClient();

        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/spaces", UserA, new { name = "Readonly Zone" }),
            TestContext.Current.CancellationToken);

        // Promote UserB as readonly.
        using (var sys = new DatabaseFactory(_dataDir).CreateSystemConnection())
        {
            var spaceId = sys.ExecuteScalar<string>(
                "SELECT id FROM spaces WHERE slug = 'readonly-zone'");
            var now = DateTime.UtcNow.ToString("o");
            sys.Execute(
                "INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@t, @u, 'readonly', @j)",
                new { t = spaceId, u = UserB, j = now });
        }

        var readResp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/spaces/readonly-zone/contacts", UserB),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, readResp.StatusCode);

        var writeResp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/spaces/readonly-zone/contacts", UserB,
                new Contact { Name = "should-not-land" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, writeResp.StatusCode);
    }

    [Fact]
    public async Task CreateContact_OversizedNotes_413()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/contacts", UserA,
            new Contact
            {
                Name = "ok",
                Notes = new string('x', Fishbowl.Core.Util.ContactLimits.MaxNotesLength + 1),
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("notes", body);
        Assert.Contains("contact", body);
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
