using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Fishbowl.Mcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// Event range reads answer a bad window with a code instead of a 500, an
// event needs a start, list_events parses its ISO range, and contact
// import/merge refuse or skip cleanly.
public class EventRangeAndContactImportApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string User = "range_import_user";

    public EventRangeAndContactImportApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_range_import_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _db = new DatabaseFactory(_dataDir);
        using (var db = _db.CreateSystemConnection())
        {
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'R', 'r@r', @now)",
                new { id = User, now = DateTime.UtcNow.ToString("o") });
        }

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (dbDescriptor != null) services.Remove(dbDescriptor);
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

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Add(TestAuthHandler.UserIdHeader, User);
        var resp = await _factory.CreateClient().SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        return (resp.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement);
    }

    [Theory]
    [InlineData("2026-10-05T00:00:00Z", "2026-10-04T00:00:00Z", "range_invalid")]
    [InlineData("2026-10-04T00:00:00Z", "2126-10-04T00:00:00Z", "range_too_long")]
    [InlineData("9999-12-01T00:00:00Z", "9999-12-31T00:00:00Z", "range_invalid")]
    public async Task EventRange_BadWindow_Is400WithCode(string from, string to, string code)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"/api/v1/events?from={from}&to={to}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(code, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CreateEvent_WithoutStartAt_Is400()
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/v1/events",
            JsonContent.Create(new { title = "When?", rRule = "FREQ=DAILY" }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_value", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ListEventsTool_FromTo_ParsesIsoRange()
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new EventRepository(_db);
        var ctx = ContextRef.User(User);
        await events.CreateAsync(ctx, User, new Event
        {
            Title = "Standup",
            StartAt = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc),
        }, ct);
        var tool = new ListEventsTool(events);

        var args = JsonDocument.Parse("""{ "from": "2026-10-05T00:00:00Z", "to": "2026-10-06T00:00:00+02:00" }""").RootElement;
        var result = JsonSerializer.SerializeToElement(await tool.InvokeAsync(ctx, User, args, new ClaimsPrincipal(), ct));

        Assert.Equal(1, result.GetProperty("count").GetInt32());

        // A half range or a non-date is the caller's mistake (InvalidParams).
        await Assert.ThrowsAsync<ArgumentException>(() => tool.InvokeAsync(ctx, User,
            JsonDocument.Parse("""{ "from": "2026-10-05T00:00:00Z" }""").RootElement, new ClaimsPrincipal(), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => tool.InvokeAsync(ctx, User,
            JsonDocument.Parse("""{ "from": "soon", "to": "later" }""").RootElement, new ClaimsPrincipal(), ct));
    }

    [Fact]
    public async Task ImportTwice_SkipsWhatIsAlreadyHere()
    {
        const string vcf =
            "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Acme\r\nKIND:org\r\nEND:VCARD\r\n" +
            "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Lovelace;Ada;;;\r\nFN:Ada Lovelace\r\nEMAIL:ada@acme.test\r\nORG:Acme\r\nEND:VCARD\r\n" +
            "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Hopper;Grace;;;\r\nFN:Grace Hopper\r\nEND:VCARD\r\n";

        var (_, first) = await SendAsync(HttpMethod.Post, "/api/v1/contacts/import?format=vcf", new StringContent(vcf));
        var (status, second) = await SendAsync(HttpMethod.Post, "/api/v1/contacts/import?format=vcf", new StringContent(vcf));

        Assert.Equal(3, first.GetProperty("created").GetInt32());
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, second.GetProperty("created").GetInt32());
        Assert.Equal(3, second.GetProperty("skipped").GetInt32());
        var (_, all) = await SendAsync(HttpMethod.Get, "/api/v1/contacts");
        Assert.Equal(3, all.GetArrayLength());
    }

    [Fact]
    public async Task Import_OrganisationNameOverTheLimit_KeepsThePerson_No500()
    {
        var csv = $"first_name,last_name,email,organisation\r\nLinus,Long,linus@long.test,{new string('o', 300)}\r\n";

        var (status, result) = await SendAsync(HttpMethod.Post, "/api/v1/contacts/import?format=csv", new StringContent(csv));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, result.GetProperty("created").GetInt32());
        Assert.Equal(1, result.GetProperty("failed").GetInt32());
        var (_, all) = await SendAsync(HttpMethod.Get, "/api/v1/contacts");
        var linus = Assert.Single(all.EnumerateArray());
        Assert.Equal(JsonValueKind.Null, linus.GetProperty("organisationId").ValueKind);
    }

    [Fact]
    public async Task Merge_OverTheLimits_Is400WithCode()
    {
        var notes = new string('n', Fishbowl.Core.Util.ContactLimits.MaxNotesLength - 10);
        var (_, a) = await SendAsync(HttpMethod.Post, "/api/v1/contacts", JsonContent.Create(new { name = "Twin", notes }));
        var (_, b) = await SendAsync(HttpMethod.Post, "/api/v1/contacts", JsonContent.Create(new { name = "Twin", notes }));

        var (status, body) = await SendAsync(HttpMethod.Post, $"/api/v1/contacts/{a.GetProperty("id").GetString()}/merge",
            JsonContent.Create(new { from = b.GetProperty("id").GetString() }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("merge_too_large", body.GetProperty("error").GetString());
        Assert.Equal("notes", body.GetProperty("field").GetString());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}
