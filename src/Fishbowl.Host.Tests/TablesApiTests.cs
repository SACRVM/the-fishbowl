using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// Space tables (space-apps spec, phase 3): typed columns, links as foreign
// keys, the role staircase, own rows, the trash, aggregates.
public class TablesApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string Owner = "tables_owner";
    private const string Designer = "tables_designer";
    private const string Member = "tables_member";
    private const string Reader = "tables_reader";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TablesApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_tables_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _db = new DatabaseFactory(_dataDir);
        using (var db = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[] { Owner, Designer, Member, Reader }.Select(id => new { id, n = "Name " + id, e = id + "@example.com", now }));
        }
        var testFactory = _db;
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
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.AuthenticationScheme, _ => { });
            });
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private HttpClient As(string user)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, user);
        return c;
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement;

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(Ct)}");
        return r.StatusCode == HttpStatusCode.NoContent ? default : await Json(r);
    }

    private static async Task<string?> Error(HttpResponseMessage r)
    {
        Assert.False(r.IsSuccessStatusCode, $"expected a refusal, got {(int)r.StatusCode}");
        return (await Json(r)).GetProperty("error").GetString();
    }

    private async Task<Fishbowl.Core.Models.Space> ClubAsync()
    {
        var spaces = new SpaceRepository(_db);
        var space = await spaces.CreateAsync(Owner, "Club " + Guid.NewGuid().ToString("N")[..6], Ct);
        await spaces.AddMemberAsync(space.Id, Designer, SpaceRole.Designer, Ct);
        await spaces.AddMemberAsync(space.Id, Member, SpaceRole.Member, Ct);
        await spaces.AddMemberAsync(space.Id, Reader, SpaceRole.Reader, Ct);
        return space;
    }

    private const string Rooms = """
        { "name": "rooms", "description": "Rooms of the club house",
          "columns": [
            { "name": "code", "type": "text", "required": true, "unique": true },
            { "name": "seats", "type": "integer", "default": 10 },
            { "name": "features", "type": "choice", "options": ["beamer", "kitchen", "piano"], "multiple": true }
          ] }
        """;

    private const string Bookings = """
        { "name": "bookings", "ownRows": true,
          "columns": [
            { "name": "room", "type": "link", "link": "rooms", "required": true },
            { "name": "day", "type": "date", "required": true },
            { "name": "hours", "type": "decimal" },
            { "name": "paid", "type": "yesno", "default": false },
            { "name": "booked_by", "type": "member" },
            { "name": "guests", "type": "link", "link": "contacts", "multiple": true },
            { "name": "status", "type": "choice", "options": ["asked", "confirmed"], "default": "asked" }
          ] }
        """;

    [Fact]
    public async Task Tables_Staircase_Types_Links_OwnRows()
    {
        var space = await ClubAsync();
        var t = $"/api/v1/spaces/{space.Slug}/tables";

        // Schema: Designer and up. A member can't; a reserved or bad name can't.
        Assert.Equal("role_design", await Error(await As(Member).PostAsync(t, Body(Rooms), Ct)));
        Assert.Equal("table_name", await Error(await As(Designer).PostAsync(t, Body("""{ "name": "notes", "columns": [] }"""), Ct)));
        Assert.Equal("column_link", await Error(await As(Designer).PostAsync(t, Body("""{ "name": "x", "columns": [ { "name": "a", "type": "link", "link": "nope" } ] }"""), Ct)));
        await Ok(await As(Designer).PostAsync(t, Body(Rooms), Ct));
        await Ok(await As(Designer).PostAsync(t, Body(Bookings), Ct));
        var list = await Ok(await As(Reader).GetAsync(t, Ct));
        Assert.Equal(new[] { "bookings", "rooms" }, list.EnumerateArray().Select(x => x.GetProperty("name").GetString()));

        // Rows: members write, readers don't; values are checked.
        Assert.Equal("role_write", await Error(await As(Reader).PostAsync($"{t}/rooms/rows", Body("""{ "title": "Hall", "code": "H1" }"""), Ct)));
        Assert.Equal("value_title", await Error(await As(Member).PostAsync($"{t}/rooms/rows", Body("""{ "code": "H1" }"""), Ct)));
        Assert.Equal("value_required", await Error(await As(Member).PostAsync($"{t}/rooms/rows", Body("""{ "title": "Hall" }"""), Ct)));
        Assert.Equal("value_invalid", await Error(await As(Member).PostAsync($"{t}/rooms/rows", Body("""{ "title": "Hall", "code": "H1", "seats": "many" }"""), Ct)));
        Assert.Equal("value_invalid", await Error(await As(Member).PostAsync($"{t}/rooms/rows", Body("""{ "title": "Hall", "code": "H1", "features": ["pool"] }"""), Ct)));
        var hall = await Ok(await As(Member).PostAsync($"{t}/rooms/rows", Body("""{ "title": "Hall", "code": "H1", "features": ["piano", "beamer"] }"""), Ct));
        Assert.Equal(10, hall.GetProperty("seats").GetInt64());   // the default
        Assert.Equal(Member, hall.GetProperty("author").GetString());
        Assert.Equal(2, hall.GetProperty("features").GetArrayLength());
        Assert.Equal("value_unique", await Error(await As(Member).PostAsync($"{t}/rooms/rows", Body("""{ "title": "Other", "code": "H1" }"""), Ct)));
        var roomId = hall.GetProperty("id").GetString();

        // Links must point at something that exists — rows and built-ins alike.
        var ada = (await Ok(await As(Member).PostAsJsonAsync($"/api/v1/spaces/{space.Slug}/contacts", new { name = "Ada" }, Ct))).GetProperty("id").GetString();
        Assert.Equal("link_missing", await Error(await As(Member).PostAsync($"{t}/bookings/rows",
            Body($$"""{ "title": "Choir", "room": "nope", "day": "2026-10-01" }"""), Ct)));
        Assert.Equal("value_invalid", await Error(await As(Member).PostAsync($"{t}/bookings/rows",
            Body($$"""{ "title": "Choir", "room": "{{roomId}}", "day": "2026-10-01", "booked_by": "stranger" }"""), Ct)));
        var booking = await Ok(await As(Member).PostAsync($"{t}/bookings/rows",
            Body($$"""{ "title": "Choir", "room": "{{roomId}}", "day": "2026-10-01", "hours": 2.5, "booked_by": "{{Member}}", "guests": ["{{ada}}"] }"""), Ct));
        Assert.False(booking.GetProperty("paid").GetBoolean());
        Assert.Equal("asked", booking.GetProperty("status").GetString());
        Assert.Equal(ada, booking.GetProperty("guests")[0].GetString());
        var bookingId = booking.GetProperty("id").GetString();

        // Nothing that is linked to can be deleted: the room, the contact.
        Assert.Equal("still_linked", await Error(await As(Member).DeleteAsync($"{t}/rooms/rows/{roomId}", Ct)));
        Assert.Equal("still_linked", await Error(await As(Member).DeleteAsync($"/api/v1/spaces/{space.Slug}/contacts/{ada}", Ct)));
        Assert.Equal("still_linked", await Error(await As(Designer).DeleteAsync($"{t}/rooms", Ct)));

        // Own rows: another member can't touch the booking; a designer can.
        await new SpaceRepository(_db).AddMemberAsync(space.Id, Reader, SpaceRole.Member, Ct);
        Assert.Equal("not_your_row", await Error(await As(Reader).PatchAsync($"{t}/bookings/rows/{bookingId}", Body("""{ "paid": true }"""), Ct)));
        var paid = await Ok(await As(Designer).PatchAsync($"{t}/bookings/rows/{bookingId}?rowVersion=1", Body("""{ "paid": true, "guests": [] }"""), Ct));
        Assert.True(paid.GetProperty("paid").GetBoolean());
        Assert.Equal(2, paid.GetProperty("row_version").GetInt64());
        Assert.Equal("version_conflict", await Error(await As(Designer).PatchAsync($"{t}/bookings/rows/{bookingId}?rowVersion=1", Body("""{ "hours": 3 }"""), Ct)));

        // The contact is free again; the booking goes to the trash and back.
        await Ok(await As(Member).DeleteAsync($"/api/v1/spaces/{space.Slug}/contacts/{ada}", Ct));
        await Ok(await As(Member).DeleteAsync($"{t}/bookings/rows/{bookingId}", Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await As(Member).GetAsync($"{t}/bookings/rows/{bookingId}", Ct)).StatusCode);
        var trash = await Ok(await As(Member).GetAsync($"/api/v1/spaces/{space.Slug}/trash", Ct));
        var item = trash.EnumerateArray().Single(i => i.GetProperty("kind").GetString() == "row");
        Assert.Equal("Choir", item.GetProperty("title").GetString());
        await Ok(await As(Member).PostAsync($"/api/v1/spaces/{space.Slug}/trash/{item.GetProperty("id").GetString()}/restore", null, Ct));
        Assert.Equal("Choir", (await Ok(await As(Member).GetAsync($"{t}/bookings/rows/{bookingId}", Ct))).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Query_Count_Aggregate_Alter_Drop()
    {
        var space = await ClubAsync();
        var t = $"/api/v1/spaces/{space.Slug}/tables";
        await Ok(await As(Designer).PostAsync(t, Body(Rooms), Ct));
        foreach (var (title, code, seats) in new[] { ("Hall", "H1", 80), ("Small", "S1", 8), ("Studio", "S2", 12) })
            await Ok(await As(Member).PostAsync($"{t}/rooms/rows", Body($$"""{ "title": "{{title}}", "code": "{{code}}", "seats": {{seats}} }"""), Ct));

        var big = await Ok(await As(Reader).PostAsync($"{t}/rooms/query", Body("""{ "where": { "seats": { "$gte": 10 } }, "orderBy": [ { "field": "seats", "direction": "asc" } ] }"""), Ct));
        Assert.Equal(new[] { "Studio", "Hall" }, big.EnumerateArray().Select(r => r.GetProperty("title").GetString()));
        Assert.Equal(2, (await Ok(await As(Reader).PostAsync($"{t}/rooms/count", Body("""{ "where": { "code": { "$like": "S%" } } }"""), Ct))).GetProperty("count").GetInt64());
        var sum = await Ok(await As(Reader).PostAsync($"{t}/rooms/aggregate", Body("""{ "fn": "sum", "column": "seats" }"""), Ct));
        Assert.Equal(100, sum[0].GetProperty("value").GetDouble());
        Assert.Equal("aggregate_column", await Error(await As(Reader).PostAsync($"{t}/rooms/aggregate", Body("""{ "fn": "sum", "column": "code" }"""), Ct)));

        // Alter: a required column needs a default on a table with rows.
        Assert.Equal("column_required", await Error(await As(Designer).PatchAsync($"{t}/rooms", Body("""{ "addColumns": [ { "name": "floor", "type": "integer", "required": true } ] }"""), Ct)));
        var altered = await Ok(await As(Designer).PatchAsync($"{t}/rooms", Body("""
            { "description": "Where we meet", "addColumns": [ { "name": "floor", "type": "integer", "required": true, "default": 0 } ],
              "renameColumns": { "seats": "capacity" }, "updateColumns": { "features": { "addOptions": ["stage"] } } }
            """), Ct));
        Assert.Equal("Where we meet", altered.GetProperty("description").GetString());
        Assert.Contains("capacity", altered.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        var byCap = await Ok(await As(Reader).PostAsync($"{t}/rooms/aggregate", Body("""{ "fn": "count", "groupBy": "floor" }"""), Ct));
        Assert.Equal(3, byCap[0].GetProperty("value").GetDouble());
        await Ok(await As(Designer).PatchAsync($"{t}/rooms", Body("""{ "dropColumns": ["floor"] }"""), Ct));

        var md = await As(Reader).GetAsync($"{t}/rooms?format=markdown", Ct);
        Assert.Contains("| capacity | integer |", await md.Content.ReadAsStringAsync(Ct));

        // A dropped table goes to the trash with its rows and comes back.
        await Ok(await As(Designer).DeleteAsync($"{t}/rooms", Ct));
        Assert.Equal("table_missing", await Error(await As(Reader).GetAsync($"{t}/rooms", Ct)));
        var item = (await Ok(await As(Designer).GetAsync($"/api/v1/spaces/{space.Slug}/trash", Ct))).EnumerateArray().Single(i => i.GetProperty("kind").GetString() == "table");
        await Ok(await As(Designer).PostAsync($"/api/v1/spaces/{space.Slug}/trash/{item.GetProperty("id").GetString()}/restore", null, Ct));
        Assert.Equal(3, (await Ok(await As(Reader).GetAsync($"{t}/rooms", Ct))).GetProperty("rows").GetInt64());
    }

    [Fact]
    public async Task PersonalWorkspace_HasNoTables()
    {
        var r = await As(Owner).GetAsync("/api/v1/tables", Ct);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
