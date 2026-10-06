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

// A space app's tile.js (AppTiles, SpaceAppTiles): GET …/apps/tiles runs it as
// the caller, read-only, in the trigger sandbox, and returns the cleaned model
// per folder; a script that writes, fails or runs too long leaves no tile and
// a "tile" entry in the error store. The desktop flags the apps that have one.
public class SpaceAppTilesApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string Owner = "tiles_owner";
    private const string Reader = "tiles_reader";
    private const string Stranger = "tiles_stranger";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SpaceAppTilesApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_app_tiles_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _db = new DatabaseFactory(_dataDir);
        using (var db = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[] { Owner, Reader, Stranger }.Select(id => new { id, n = "Name " + id, e = id + "@example.com", now }));
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

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private async Task<Space> SpaceWithCardsAsync()
    {
        var spaces = new SpaceRepository(_db);
        var space = await spaces.CreateAsync(Owner, "Tiles " + Guid.NewGuid().ToString("N")[..6], Ct);
        await spaces.AddMemberAsync(space.Id, Reader, SpaceRole.Reader, Ct);
        var t = $"/api/v1/spaces/{space.Slug}/tables";
        await Ok(await As(Owner).PostAsync(t, Body("""{ "name": "cards", "columns": [{ "name": "done", "type": "yesno" }] }"""), Ct));
        foreach (var (title, done) in new[] { ("Write the plan", false), ("Ship it", false), ("Old one", true) })
            await Ok(await As(Owner).PostAsync($"{t}/cards/rows", Body(JsonSerializer.Serialize(new { title, done })), Ct));
        return space;
    }

    private void AppFile(Space space, string folder, string file, string content)
    {
        var dir = Path.Combine(_dataDir, "spaces", space.Id, "files", ".apps", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), content);
    }

    private async Task<JsonElement> TilesAsync(Space space, string user = Reader) =>
        (await Ok(await As(user).GetAsync($"/api/v1/spaces/{space.Slug}/apps/tiles", Ct))).GetProperty("tiles");

    private async Task<string> ErrorsAsync(Space space) =>
        (await Ok(await As(Owner).GetAsync($"/api/v1/spaces/{space.Slug}/apps/errors", Ct))).GetRawText();

    [Fact]
    public async Task Tile_ShowsWhatTileJsReturns_RunAsTheViewer()
    {
        var space = await SpaceWithCardsAsync();
        AppFile(space, "kanban", "tile.js", """
            function tile(ctx) {
              const open = ctx.query('cards', { where: { done: false }, orderBy: [{ field: 'title', dir: 'asc' }] });
              return {
                main: ctx.count('cards', { done: false }) + ' open',
                sub: ['as ' + ctx.user.id, ctx.user.canWrite ? 'writer' : 'reader'],
                items: open.map(c => ({ lead: '#', text: c.title, tail: c.done ? 'done' : 'open' })),
                empty: 'Nothing open',
              };
            }
            """);

        var kanban = (await TilesAsync(space)).GetProperty("kanban");
        Assert.Equal("2 open", kanban.GetProperty("main").GetString());
        Assert.Equal(new[] { "as " + Reader, "reader" }, kanban.GetProperty("sub").EnumerateArray().Select(x => x.GetString()));
        var items = kanban.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { "Ship it", "Write the plan" }, items.Select(i => i.GetProperty("text").GetString()));
        Assert.Equal("open", items[0].GetProperty("tail").GetString());
        Assert.Equal("Nothing open", kanban.GetProperty("empty").GetString());
    }

    [Fact]
    public async Task Tile_IsReadOnly_AndFailuresLeaveNoTile_ButAnError()
    {
        var space = await SpaceWithCardsAsync();
        AppFile(space, "writer", "tile.js", "function tile(ctx) { ctx.insert('cards', { title: 'sneaky' }); return { main: 'x' }; }");
        AppFile(space, "loop", "tile.js", "function tile(ctx) { while (true) {} }");
        AppFile(space, "broken", "tile.js", "function tile(ctx) { return {");
        AppFile(space, "logger", "tile.js", "function tile(ctx) { ctx.log('hello from the tile'); return null; }");

        var tiles = await TilesAsync(space, Owner);
        Assert.Empty(tiles.EnumerateObject());
        // Nothing was written, even with the owner's role.
        var count = await Ok(await As(Owner).PostAsync($"/api/v1/spaces/{space.Slug}/tables/cards/count", Body("{}"), Ct));
        Assert.Equal(3, count.GetProperty("count").GetInt32());

        var errors = await ErrorsAsync(space);
        Assert.Contains("\"kind\":\"tile\"", errors.Replace(" ", ""));
        foreach (var app in new[] { "writer", "loop", "broken", "logger" })
            Assert.Contains($"\"app\":\"{app}\"", errors.Replace(" ", ""));
        Assert.Contains("hello from the tile", errors);
    }

    [Fact]
    public async Task Tile_IsCutToSize_TextOnly()
    {
        var space = await SpaceWithCardsAsync();
        AppFile(space, "big", "tile.js", """
            function tile(ctx) {
              const rows = [];
              for (let i = 0; i < 50; i++) rows.push({ text: 'row ' + i, tail: i, nested: { no: true } });
              return { main: 'x'.repeat(500), sub: ['a', 'b', 'c', 'd'], items: rows, extra: '<b>dropped</b>' };
            }
            """);
        AppFile(space, "nothing", "tile.js", "function tile(ctx) { return 42; }");

        var tiles = await TilesAsync(space);
        Assert.False(tiles.TryGetProperty("nothing", out _));
        var big = tiles.GetProperty("big");
        Assert.Equal(200, big.GetProperty("main").GetString()!.Length);
        Assert.Equal(3, big.GetProperty("sub").GetArrayLength());
        Assert.Equal(30, big.GetProperty("items").GetArrayLength());
        Assert.Equal("7", big.GetProperty("items")[7].GetProperty("tail").GetString());
        Assert.False(big.TryGetProperty("extra", out _));
    }

    [Fact]
    public async Task Desktop_FlagsTheAppsWithATile_AndStrangersGetNothing()
    {
        var space = await SpaceWithCardsAsync();
        AppFile(space, "kanban", "app.json", """{ "name": "Kanban", "tag": "kanban-app" }""");
        AppFile(space, "kanban", "app.js", "customElements.define('kanban-app', class extends HTMLElement {});");
        AppFile(space, "kanban", "tile.js", "function tile() { return { main: 'hi' }; }");
        AppFile(space, "plain", "app.json", """{ "name": "Plain", "tag": "plain-app" }""");
        AppFile(space, "plain", "app.js", "customElements.define('plain-app', class extends HTMLElement {});");

        var desktop = await Ok(await As(Reader).GetAsync($"/api/v1/spaces/{space.Slug}/desktop", Ct));
        var apps = desktop.GetProperty("apps").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!);
        Assert.True(apps["space.kanban"].GetProperty("tile").GetBoolean());
        Assert.False(apps["space.plain"].GetProperty("tile").GetBoolean());

        var stranger = await As(Stranger).GetAsync($"/api/v1/spaces/{space.Slug}/apps/tiles", Ct);
        Assert.True(stranger.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, $"{(int)stranger.StatusCode}");
    }
}
