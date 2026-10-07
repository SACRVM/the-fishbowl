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

// The personal Calendar's and Contacts' sources (ViewSourcesApi): personal
// alone until switched, one source at a time, per app, kept per user; only
// "personal" and the caller's own spaces — a space they left is gone.
public class ViewSourcesApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string Me = "sources_me";
    private const string Other = "sources_other";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ViewSourcesApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_view_sources_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _db = new DatabaseFactory(_dataDir);
        using (var db = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[] { Me, Other }.Select(id => new { id, n = "Name " + id, e = id + "@example.com", now }));
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

    private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static string[] List(JsonElement e, string app) => e.GetProperty(app).EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Fact]
    public async Task Sources_DefaultToPersonal_SwitchPerApp_AndKeepToOwnSpaces()
    {
        var spaces = new SpaceRepository(_db);
        var mine = await spaces.CreateAsync(Other, "Family " + Guid.NewGuid().ToString("N")[..6], Ct);
        await spaces.AddMemberAsync(mine.Id, Me, SpaceRole.Member, Ct);
        var foreign = await spaces.CreateAsync(Other, "Work " + Guid.NewGuid().ToString("N")[..6], Ct);

        var start = await Ok(await As(Me).GetAsync("/api/v1/me/sources", Ct));
        Assert.Equal(new[] { "personal" }, List(start, "calendar"));
        Assert.Equal(new[] { "personal" }, List(start, "contacts"));

        // A space on for the calendar only; personal off for contacts.
        var on = await Ok(await As(Me).PutAsync("/api/v1/me/sources", Json(new { app = "calendar", source = mine.Id, on = true }), Ct));
        Assert.Equal(new[] { "personal", mine.Id }, List(on, "calendar"));
        await Ok(await As(Me).PutAsync("/api/v1/me/sources", Json(new { app = "contacts", source = "personal", on = false }), Ct));
        var kept = await Ok(await As(Me).GetAsync("/api/v1/me/sources", Ct));
        Assert.Equal(new[] { "personal", mine.Id }, List(kept, "calendar"));
        Assert.Empty(List(kept, "contacts"));

        // Someone else's space, an unknown app: refused, nothing changes.
        var refused = await As(Me).PutAsync("/api/v1/me/sources", Json(new { app = "calendar", source = foreign.Id, on = true }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("invalid_source", await refused.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await As(Me).PutAsync("/api/v1/me/sources", Json(new { app = "notes", source = "personal", on = true }), Ct)).StatusCode);

        // Each person has their own.
        Assert.Equal(new[] { "personal" }, List(await Ok(await As(Other).GetAsync("/api/v1/me/sources", Ct)), "calendar"));

        // Leaving the space drops it.
        await spaces.RemoveMemberAsync(mine.Id, Me, Ct);
        Assert.Equal(new[] { "personal" }, List(await Ok(await As(Me).GetAsync("/api/v1/me/sources", Ct)), "calendar"));
    }
}
