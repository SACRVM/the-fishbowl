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

public class EventsApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dataDir;
    private const string UserA = "events_user_a";
    private const string UserB = "events_user_b";

    public EventsApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(),
            "fishbowl_events_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        var testFactory = new DatabaseFactory(_dataDir);
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

    private async Task<Event> CreateAsync(HttpClient client, string user, string title, DateTime startAt)
    {
        var resp = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/events", user,
            new Event { Title = title, StartAt = startAt }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var evt = await resp.Content.ReadFromJsonAsync<Event>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(evt);
        return evt!;
    }

    [Fact]
    public async Task EventsApi_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/events",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Create_Returns201_WithLocation()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/events", UserA,
            new Event
            {
                Title = "Standup",
                StartAt = new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc),
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.NotNull(resp.Headers.Location);
        Assert.StartsWith("/api/v1/events/", resp.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Create_RejectsEmptyTitle_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/events", UserA,
            new Event { Title = "  ", StartAt = DateTime.UtcNow }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsInvertedWindow_400()
    {
        var client = _factory.CreateClient();
        var start = new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);
        var resp = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/events", UserA,
            new Event { Title = "bad", StartAt = start, EndAt = start.AddMinutes(-1) }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task List_Range_ReturnsOnlyMatchingEvents()
    {
        var client = _factory.CreateClient();
        var day = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await CreateAsync(client, UserA, "before", day.AddHours(-2));
        await CreateAsync(client, UserA, "inside",  day.AddHours(9));
        await CreateAsync(client, UserA, "after",  day.AddDays(3));

        var from = Uri.EscapeDataString(day.ToString("o"));
        var to   = Uri.EscapeDataString(day.AddDays(1).ToString("o"));
        var resp = await client.SendAsync(Req(HttpMethod.Get,
            $"/api/v1/events?from={from}&to={to}", UserA),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var list = await resp.Content.ReadFromJsonAsync<List<Event>>(
            TestContext.Current.CancellationToken);
        Assert.Single(list!);
        Assert.Equal("inside", list![0].Title);
    }

    [Fact]
    public async Task List_Range_HalfOpenBadRequest()
    {
        var client = _factory.CreateClient();
        var from = Uri.EscapeDataString(DateTime.UtcNow.ToString("o"));
        var resp = await client.SendAsync(Req(HttpMethod.Get,
            $"/api/v1/events?from={from}", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Get_Returns200_WhenPresent()
    {
        var client = _factory.CreateClient();
        var created = await CreateAsync(client, UserA, "present", DateTime.UtcNow);

        var resp = await client.SendAsync(Req(HttpMethod.Get,
            $"/api/v1/events/{created.Id}", UserA),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var fetched = await resp.Content.ReadFromJsonAsync<Event>(
            TestContext.Current.CancellationToken);
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task Get_Returns404_WhenMissing()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(Req(HttpMethod.Get,
            "/api/v1/events/01HZ_MISSING", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Update_PersistsAcrossGet()
    {
        var client = _factory.CreateClient();
        var created = await CreateAsync(client, UserA, "before", DateTime.UtcNow);

        created.Title = "after";
        created.Location = "home office";
        var upd = await client.SendAsync(Req(HttpMethod.Put,
            $"/api/v1/events/{created.Id}", UserA, created),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, upd.StatusCode);

        var fetched = await client.SendAsync(Req(HttpMethod.Get,
            $"/api/v1/events/{created.Id}", UserA),
            TestContext.Current.CancellationToken);
        var item = await fetched.Content.ReadFromJsonAsync<Event>(
            TestContext.Current.CancellationToken);
        Assert.Equal("after", item!.Title);
        Assert.Equal("home office", item.Location);
    }

    [Fact]
    public async Task Delete_Returns204_AndItemGone()
    {
        var client = _factory.CreateClient();
        var created = await CreateAsync(client, UserA, "short-lived", DateTime.UtcNow);

        var del = await client.SendAsync(Req(HttpMethod.Delete,
            $"/api/v1/events/{created.Id}", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await client.SendAsync(Req(HttpMethod.Get,
            $"/api/v1/events/{created.Id}", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Isolation_BetweenUsers()
    {
        var client = _factory.CreateClient();
        await CreateAsync(client, UserA, "a-event", DateTime.UtcNow);
        await CreateAsync(client, UserB, "b-event", DateTime.UtcNow);

        var aResp = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/events", UserA),
            TestContext.Current.CancellationToken);
        var aList = await aResp.Content.ReadFromJsonAsync<List<Event>>(
            TestContext.Current.CancellationToken);
        Assert.Single(aList!);
        Assert.Equal("a-event", aList![0].Title);

        var bResp = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/events", UserB),
            TestContext.Current.CancellationToken);
        var bList = await bResp.Content.ReadFromJsonAsync<List<Event>>(
            TestContext.Current.CancellationToken);
        Assert.Single(bList!);
        Assert.Equal("b-event", bList![0].Title);
    }

    [Fact]
    public async Task TeamEvents_Isolated_AndNonMember403()
    {
        var client = _factory.CreateClient();
        await CreateAsync(client, UserA, "personal-event", DateTime.UtcNow);

        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/teams", UserA,
            new { name = "Events Team" }),
            TestContext.Current.CancellationToken);

        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/teams/events-team/events", UserA,
            new Event { Title = "team-event", StartAt = DateTime.UtcNow }),
            TestContext.Current.CancellationToken);

        var teamResp = await client.SendAsync(Req(HttpMethod.Get,
            "/api/v1/teams/events-team/events", UserA),
            TestContext.Current.CancellationToken);
        var teamList = await teamResp.Content.ReadFromJsonAsync<List<Event>>(
            TestContext.Current.CancellationToken);
        Assert.Single(teamList!);
        Assert.Equal("team-event", teamList![0].Title);

        var personalResp = await client.SendAsync(Req(HttpMethod.Get,
            "/api/v1/events", UserA),
            TestContext.Current.CancellationToken);
        var personalList = await personalResp.Content.ReadFromJsonAsync<List<Event>>(
            TestContext.Current.CancellationToken);
        Assert.Single(personalList!);
        Assert.Equal("personal-event", personalList![0].Title);

        var forbid = await client.SendAsync(Req(HttpMethod.Get,
            "/api/v1/teams/events-team/events", UserB),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbid.StatusCode);
    }

    [Fact]
    public async Task TeamEvents_Readonly_CannotWrite()
    {
        var client = _factory.CreateClient();

        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/teams", UserA, new { name = "RO Events" }),
            TestContext.Current.CancellationToken);

        using (var sys = new DatabaseFactory(_dataDir).CreateSystemConnection())
        {
            var teamId = sys.ExecuteScalar<string>(
                "SELECT id FROM teams WHERE slug = 'ro-events'");
            var now = DateTime.UtcNow.ToString("o");
            sys.Execute(
                "INSERT INTO team_members(team_id, user_id, role, joined_at) VALUES (@t, @u, 'readonly', @j)",
                new { t = teamId, u = UserB, j = now });
        }

        // readonly can read
        var readResp = await client.SendAsync(Req(HttpMethod.Get,
            "/api/v1/teams/ro-events/events", UserB),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, readResp.StatusCode);

        // readonly cannot write
        var writeResp = await client.SendAsync(Req(HttpMethod.Post,
            "/api/v1/teams/ro-events/events", UserB,
            new Event { Title = "sneaky", StartAt = DateTime.UtcNow }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, writeResp.StatusCode);
    }

    [Fact]
    public async Task CreateEvent_OversizedDescription_413()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/events", UserA,
            new Event
            {
                Title = "ok",
                StartAt = DateTime.UtcNow,
                Description = new string('d', Fishbowl.Core.Util.EventLimits.MaxDescriptionLength + 1),
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("event", body);
        Assert.Contains("description", body);
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
