using System.Net;
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

// The calendar's tools (EventToolsApi): .ics export and import (once per UID,
// a Reader can't import, a file that isn't iCalendar is refused), the
// contacts' birthdays by date, and subscription links — they open the
// workspace's events without signing in, stop when revoked or when their maker
// leaves the space, and only a person (cookie) makes them.
public class EventToolsApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string Owner = "evtools_owner";
    private const string Member = "evtools_member";
    private const string Reader = "evtools_reader";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public EventToolsApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_event_tools_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _db = new DatabaseFactory(_dataDir);
        using (var db = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[] { Owner, Member, Reader }.Select(id => new { id, n = "Name " + id, e = id + "@example.com", now }));
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

    private HttpClient As(string? user)
    {
        var c = _factory.CreateClient();
        if (user is not null) c.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, user);
        return c;
    }

    private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    private static StringContent Ics(string text) => new(text, Encoding.UTF8, "text/calendar");

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private async Task<Space> SpaceAsync()
    {
        var spaces = new SpaceRepository(_db);
        var space = await spaces.CreateAsync(Owner, "Club " + Guid.NewGuid().ToString("N")[..6], Ct);
        await spaces.AddMemberAsync(space.Id, Member, SpaceRole.Member, Ct);
        await spaces.AddMemberAsync(space.Id, Reader, SpaceRole.Reader, Ct);
        return space;
    }

    [Fact]
    public async Task ExportThenImport_CopiesEveryEvent_OncePerUid()
    {
        var a = await SpaceAsync();
        var b = await SpaceAsync();
        var ev = $"/api/v1/spaces/{a.Slug}/events";
        await Ok(await As(Owner).PostAsync(ev, Json(new
        {
            title = "Rehearsal",
            startAt = "2026-10-12T16:00:00Z",
            endAt = "2026-10-12T18:00:00Z",
            timeZone = "Europe/Berlin",
            rRule = "FREQ=WEEKLY;BYDAY=MO",
            reminderMinutes = 30
        }), Ct));
        await Ok(await As(Owner).PostAsync(ev, Json(new { title = "Summer fest", allDay = true, startDate = "2026-07-04", endDate = "2026-07-06", startAt = "2026-07-04T00:00:00Z" }), Ct));

        var export = await As(Member).GetAsync($"{ev}/export", Ct);
        Assert.Equal("text/calendar", export.Content.Headers.ContentType?.MediaType);
        var ics = await export.Content.ReadAsStringAsync(Ct);
        Assert.Contains("SUMMARY:Rehearsal", ics);
        Assert.Contains($"X-WR-CALNAME:{a.Name}", ics);

        var target = $"/api/v1/spaces/{b.Slug}/events";
        var first = await Ok(await As(Owner).PostAsync($"{target}/import", Ics(ics), Ct));
        Assert.Equal(2, first.GetProperty("created").GetInt32());
        var again = await Ok(await As(Owner).PostAsync($"{target}/import", Ics(ics), Ct));
        Assert.Equal(0, again.GetProperty("created").GetInt32());
        Assert.Equal(2, again.GetProperty("skipped").GetInt32());

        var copied = (await Ok(await As(Owner).GetAsync(target, Ct))).EnumerateArray().ToList();
        var rehearsal = copied.Single(e => e.GetProperty("title").GetString() == "Rehearsal");
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", rehearsal.GetProperty("rRule").GetString());
        Assert.Equal("Europe/Berlin", rehearsal.GetProperty("timeZone").GetString());
        Assert.Equal(30, rehearsal.GetProperty("reminderMinutes").GetInt32());
        var fest = copied.Single(e => e.GetProperty("title").GetString() == "Summer fest");
        Assert.Equal("2026-07-06", fest.GetProperty("endDate").GetString());
    }

    [Fact]
    public async Task Import_RefusesNonCalendars_AndReaders()
    {
        var s = await SpaceAsync();
        var import = $"/api/v1/spaces/{s.Slug}/events/import";
        var bad = await As(Owner).PostAsync(import, Ics("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Ada\r\nEND:VCARD\r\n"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("invalid_file", await bad.Content.ReadAsStringAsync(Ct));

        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nBEGIN:VEVENT\r\nUID:r1\r\nDTSTAMP:20261001T000000Z\r\nSUMMARY:X\r\n"
            + "DTSTART:20261101T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Reader).PostAsync(import, Ics(ics), Ct)).StatusCode);
    }

    [Fact]
    public async Task Birthdays_ComeFromContacts_ByDate()
    {
        var s = await SpaceAsync();
        var contacts = $"/api/v1/spaces/{s.Slug}/contacts";
        await Ok(await As(Owner).PostAsync(contacts, Json(new { kind = "person", firstName = "Ada", lastName = "Lovelace", birthday = "1815-12-10" }), Ct));
        await Ok(await As(Owner).PostAsync(contacts, Json(new { kind = "person", firstName = "Leap", lastName = "Day", birthday = "2000-02-29" }), Ct));
        await Ok(await As(Owner).PostAsync(contacts, Json(new { kind = "organisation", name = "Acme" }), Ct));

        var r = await Ok(await As(Reader).GetAsync($"/api/v1/spaces/{s.Slug}/events/birthdays?from=2026-01-01&to=2027-01-01", Ct));
        var list = r.GetProperty("birthdays").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("2026-03-01", list[0].GetProperty("date").GetString());   // 29 Feb in a common year
        Assert.Equal(26, list[0].GetProperty("age").GetInt32());
        Assert.Equal("Ada Lovelace", list[1].GetProperty("name").GetString());
        Assert.Equal("2026-12-10", list[1].GetProperty("date").GetString());

        var tooLong = await As(Owner).GetAsync($"/api/v1/spaces/{s.Slug}/events/birthdays?from=2026-01-01&to=2028-01-01", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Feeds_OpenTheCalendar_UntilRevokedOrTheMakerLeaves()
    {
        var s = await SpaceAsync();
        var ev = $"/api/v1/spaces/{s.Slug}/events";
        await Ok(await As(Owner).PostAsync(ev, Json(new { title = "Board meeting", startAt = "2026-11-02T18:00:00Z" }), Ct));

        var made = await Ok(await As(Member).PostAsync($"{ev}/feeds", null, Ct));
        var url = new Uri(made.GetProperty("url").GetString()!);
        Assert.StartsWith("/feeds/fbcal_", url.AbsolutePath);
        var feed = await As(null).GetAsync(url.AbsolutePath, Ct);
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        Assert.Equal("text/calendar", feed.Content.Headers.ContentType?.MediaType);
        Assert.Contains("SUMMARY:Board meeting", await feed.Content.ReadAsStringAsync(Ct));

        // Only the maker sees and revokes it.
        var mine = await Ok(await As(Member).GetAsync($"{ev}/feeds", Ct));
        Assert.Single(mine.GetProperty("feeds").EnumerateArray());
        Assert.Empty((await Ok(await As(Owner).GetAsync($"{ev}/feeds", Ct))).GetProperty("feeds").EnumerateArray());
        var id = made.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await As(Owner).DeleteAsync($"{ev}/feeds/{id}", Ct)).StatusCode);

        // The maker leaves the space: the link stops.
        await new SpaceRepository(_db).RemoveMemberAsync(s.Id, Member, Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await As(null).GetAsync(url.AbsolutePath, Ct)).StatusCode);

        // A revoked link stops too; an unknown one says nothing.
        var personal = await Ok(await As(Owner).PostAsync("/api/v1/events/feeds", null, Ct));
        var pUrl = new Uri(personal.GetProperty("url").GetString()!).AbsolutePath;
        Assert.Equal(HttpStatusCode.OK, (await As(null).GetAsync(pUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await As(Owner).DeleteAsync($"/api/v1/events/feeds/{personal.GetProperty("id").GetString()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As(null).GetAsync(pUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As(null).GetAsync("/feeds/fbcal_nope.ics", Ct)).StatusCode);
    }
}
