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
                "INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@t, @u, 'reader', @j)",
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

    [Fact]
    public async Task PersonsAndOrganisations_Links_Merge_Duplicates_VCardAndCsv()
    {
        var c = _factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        async Task<System.Text.Json.JsonElement> Send(HttpMethod m, string path, object? body = null, HttpStatusCode expect = HttpStatusCode.OK)
        {
            var r = await c.SendAsync(Req(m, path, UserA, body), ct);
            Assert.True(r.StatusCode == expect || (expect == HttpStatusCode.OK && r.IsSuccessStatusCode), $"{m} {path}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync(ct)}");
            var text = await r.Content.ReadAsStringAsync(ct);
            return text.Length == 0 ? default : System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        }

        // An organisation, and a person who belongs to it — name from first + last.
        var acme = await Send(HttpMethod.Post, "/api/v1/contacts", new { kind = "organisation", name = "Acme GmbH", legalForm = "GmbH", vatId = "DE123", phones = new[] { new { label = "work", value = "+49 30 1" } } });
        var acmeId = acme.GetProperty("id").GetString()!;
        var ada = await Send(HttpMethod.Post, "/api/v1/contacts", new
        {
            firstName = "Ada",
            lastName = "Lovelace",
            honorific = "Dr.",
            organisationId = acmeId,
            role = "Engineer",
            birthday = "1815-12-10",
            emails = new[] { new { label = "work", value = "ada@acme.test" }, new { label = "home", value = "ada@home.test" } },
            addresses = new[] { new { label = "work", street = "Main St 1", postalCode = "10115", city = "Berlin", country = "DE" } },
            tags = new[] { "vip" },
        });
        var adaId = ada.GetProperty("id").GetString()!;
        var got = await Send(HttpMethod.Get, $"/api/v1/contacts/{adaId}");
        Assert.Equal("Ada Lovelace", got.GetProperty("name").GetString());
        Assert.Equal("ada@acme.test", got.GetProperty("email").GetString());
        Assert.Equal(2, got.GetProperty("emails").GetArrayLength());
        Assert.Equal("Berlin", got.GetProperty("addresses")[0].GetProperty("city").GetString());

        // A person only belongs to an organisation; an organisation with people stays.
        await Send(HttpMethod.Post, "/api/v1/contacts", new { name = "X", organisationId = adaId }, HttpStatusCode.BadRequest);
        await Send(HttpMethod.Delete, $"/api/v1/contacts/{acmeId}", expect: HttpStatusCode.Conflict);
        var links = await Send(HttpMethod.Get, $"/api/v1/contacts/{acmeId}/links");
        Assert.Equal(adaId, Assert.Single(links.GetProperty("people").EnumerateArray()).GetProperty("id").GetString());

        // Search finds a person by their organisation.
        var hits = await Send(HttpMethod.Get, "/api/v1/contacts/search?q=acme");
        Assert.Contains(hits.EnumerateArray(), h => h.GetProperty("id").GetString() == adaId);

        // A duplicate (same email) is found and merged: data and people move, it goes to the trash.
        var dup = await Send(HttpMethod.Post, "/api/v1/contacts", new { name = "A. Lovelace", email = "ada@acme.test", phone = "+44 1", website = "https://ada.test" });
        var dupId = dup.GetProperty("id").GetString()!;
        var pairs = await Send(HttpMethod.Get, "/api/v1/contacts/duplicates");
        Assert.Contains(pairs.GetProperty("pairs").EnumerateArray(), p => p.GetProperty("reason").GetString() == "email");
        var merged = await Send(HttpMethod.Post, $"/api/v1/contacts/{adaId}/merge", new { from = dupId });
        Assert.Equal("https://ada.test", merged.GetProperty("website").GetString());
        Assert.Equal("+44 1", merged.GetProperty("phones")[0].GetProperty("value").GetString());
        await Send(HttpMethod.Get, $"/api/v1/contacts/{dupId}", expect: HttpStatusCode.NotFound);

        // Out and back in: vCard and CSV.
        var vcf = await (await c.SendAsync(Req(HttpMethod.Get, "/api/v1/contacts/export?format=vcf", UserA), ct)).Content.ReadAsStringAsync(ct);
        Assert.Contains("FN:Ada Lovelace", vcf);
        Assert.Contains("ORG:Acme GmbH", vcf);
        Assert.Contains("KIND:org", vcf);
        var csv = await (await c.SendAsync(Req(HttpMethod.Get, "/api/v1/contacts/export?format=csv", UserA), ct)).Content.ReadAsStringAsync(ct);
        Assert.Contains("Ada Lovelace", csv);

        // Into another account: the organisation is made once, the person points at it.
        var import = new HttpRequestMessage(HttpMethod.Post, "/api/v1/contacts/import?format=vcf") { Content = new StringContent(vcf) };
        import.Headers.Add(TestAuthHandler.UserIdHeader, UserB);
        var result = System.Text.Json.JsonDocument.Parse(await (await c.SendAsync(import, ct)).Content.ReadAsStringAsync(ct)).RootElement;
        Assert.Equal(2, result.GetProperty("created").GetInt32());
        var imported = System.Text.Json.JsonDocument.Parse(await (await c.SendAsync(Req(HttpMethod.Get, "/api/v1/contacts", UserB), ct)).Content.ReadAsStringAsync(ct)).RootElement;
        var bAcme = imported.EnumerateArray().Single(x => x.GetProperty("kind").GetString() == "organisation");
        var bAda = imported.EnumerateArray().Single(x => x.GetProperty("kind").GetString() == "person");
        Assert.Equal(bAcme.GetProperty("id").GetString(), bAda.GetProperty("organisationId").GetString());
        Assert.Equal("1815-12-10", bAda.GetProperty("birthday").GetString());
        Assert.Equal("Berlin", bAda.GetProperty("addresses")[0].GetProperty("city").GetString());

        var csvImport = new HttpRequestMessage(HttpMethod.Post, "/api/v1/contacts/import?format=csv")
        { Content = new StringContent("name,first_name,last_name,email,organisation\r\n,Grace,Hopper,grace@navy.test,Navy\r\n") };
        csvImport.Headers.Add(TestAuthHandler.UserIdHeader, UserB);
        var csvResult = System.Text.Json.JsonDocument.Parse(await (await c.SendAsync(csvImport, ct)).Content.ReadAsStringAsync(ct)).RootElement;
        Assert.Equal(1, csvResult.GetProperty("created").GetInt32());
        Assert.Equal(1, csvResult.GetProperty("organisations").GetInt32());
    }
}
