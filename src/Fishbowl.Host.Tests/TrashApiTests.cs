using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
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

// One trash for everything (space-apps spec, phase 2): a deleted note, todo,
// event or contact is really gone from its table and waits in the trash
// until restored, deleted for good or purged.
public class TrashApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string Owner = "trash_owner";
    private const string Reader = "trash_reader";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TrashApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_trash_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _db = new DatabaseFactory(_dataDir);
        using (var db = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[] { Owner, Reader }.Select(id => new { id, n = "Name " + id, e = id + "@example.com", now }));
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

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement;

    private static async Task<string> IdOf(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync(Ct)}");
        return (await Json(r)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task DeletedNote_GoesToTheTrash_AndComesBackSearchable()
    {
        var c = As(Owner);
        var id = await IdOf(await c.PostAsJsonAsync("/api/v1/notes",
            new { title = "Zebra crossing plan", content = "stripes everywhere", tags = new[] { "town" } }, Ct));
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/notes/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/notes/{id}", Ct)).StatusCode);

        var items = (await Json(await c.GetAsync("/api/v1/trash", Ct))).EnumerateArray().ToList();
        var item = Assert.Single(items);
        Assert.Equal("note", item.GetProperty("kind").GetString());
        Assert.Equal(id, item.GetProperty("itemId").GetString());
        Assert.Equal("Zebra crossing plan", item.GetProperty("title").GetString());
        Assert.Equal("Name " + Owner, item.GetProperty("deletedBy").GetString());
        Assert.False(item.TryGetProperty("data", out _));   // the snapshot stays on the server

        var trashId = item.GetProperty("id").GetString();
        var restored = await c.PostAsync($"/api/v1/trash/{trashId}/restore", null, Ct);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var note = await Json(await c.GetAsync($"/api/v1/notes/{id}", Ct));
        Assert.Equal("stripes everywhere", note.GetProperty("content").GetString());
        Assert.Contains("town", note.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Empty((await Json(await c.GetAsync("/api/v1/trash", Ct))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync($"/api/v1/trash/{trashId}/restore", null, Ct)).StatusCode);

        // Its full-text row is back too.
        var hits = await Json(await c.GetAsync("/api/v1/search?q=zebra", Ct));
        Assert.Contains(id, hits.ToString());
    }

    [Fact]
    public async Task TodosEventsContacts_RoundTrip_AndEmpty()
    {
        var c = As(Owner);
        var todo = await IdOf(await c.PostAsJsonAsync("/api/v1/todos", new { title = "Buy paint" }, Ct));
        var ev = await IdOf(await c.PostAsJsonAsync("/api/v1/events",
            new { title = "Club night", startAt = DateTime.UtcNow.AddDays(3).ToString("o"), reminderMinutes = 30 }, Ct));
        var contact = await IdOf(await c.PostAsJsonAsync("/api/v1/contacts", new { name = "Ada Lovelace", email = "ada@example.com" }, Ct));
        Assert.True((await c.DeleteAsync($"/api/v1/todos/{todo}", Ct)).IsSuccessStatusCode);
        Assert.True((await c.DeleteAsync($"/api/v1/events/{ev}", Ct)).IsSuccessStatusCode);
        Assert.True((await c.DeleteAsync($"/api/v1/contacts/{contact}", Ct)).IsSuccessStatusCode);

        var items = (await Json(await c.GetAsync("/api/v1/trash", Ct))).EnumerateArray().ToList();
        Assert.Equal(new[] { "contact", "event", "todo" }, items.Select(i => i.GetProperty("kind").GetString()));
        foreach (var i in items)
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/v1/trash/{i.GetProperty("id").GetString()}/restore", null, Ct)).StatusCode);
        Assert.Equal("Buy paint", (await Json(await c.GetAsync($"/api/v1/todos/{todo}", Ct))).GetProperty("title").GetString());
        Assert.Equal(30, (await Json(await c.GetAsync($"/api/v1/events/{ev}", Ct))).GetProperty("reminderMinutes").GetInt32());
        var found = await Json(await c.GetAsync("/api/v1/contacts/search?q=lovelace", Ct));
        Assert.Contains(contact, found.ToString());

        // Delete again, then empty.
        await c.DeleteAsync($"/api/v1/todos/{todo}", Ct);
        await c.DeleteAsync($"/api/v1/contacts/{contact}", Ct);
        Assert.Equal(2, (await Json(await c.DeleteAsync("/api/v1/trash", Ct))).GetProperty("deleted").GetInt32());
        Assert.Empty((await Json(await c.GetAsync("/api/v1/trash", Ct))).EnumerateArray());
    }

    [Fact]
    public async Task Restore_RefusesWhenThePlaceIsTaken()
    {
        var ctx = ContextRef.User(Owner);
        var todos = new TodoRepository(_db);
        var id = await todos.CreateAsync(ctx, Owner, new TodoItem { Title = "Original" }, Ct);
        await todos.DeleteAsync(ctx, id, Ct);
        // Something else took the id meanwhile.
        using (var db = _db.CreateContextConnection(ctx))
            db.Execute("INSERT INTO todos(id, title, created_by, created_at, updated_at) VALUES (@id, 'Squatter', 'x', @now, @now)",
                new { id, now = DateTime.UtcNow.ToString("o") });

        var c = As(Owner);
        var trashId = (await Json(await c.GetAsync("/api/v1/trash", Ct)))[0].GetProperty("id").GetString();
        var r = await c.PostAsync($"/api/v1/trash/{trashId}/restore", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("restore_conflict", (await Json(r)).GetProperty("error").GetString());
        Assert.Single((await Json(await c.GetAsync("/api/v1/trash", Ct))).EnumerateArray());   // still there

        // The daily purge drops it once it is old enough.
        Assert.Equal(0, await new TrashRepository(_db, new NoteRepository(_db, new TagRepository(_db))).PurgeAsync(ctx, DateTime.UtcNow.AddDays(-1), Ct));
        Assert.Equal(1, await new TrashRepository(_db, new NoteRepository(_db, new TagRepository(_db))).PurgeAsync(ctx, DateTime.UtcNow.AddMinutes(1), Ct));
    }

    [Fact]
    public async Task SpaceTrash_MembersSee_OnlyWritersRestore()
    {
        var spaces = new SpaceRepository(_db);
        var space = await spaces.CreateAsync(Owner, "Trash club", Ct);
        await spaces.AddMemberAsync(space.Id, Reader, SpaceRole.Reader, Ct);
        var owner = As(Owner);
        var id = await IdOf(await owner.PostAsJsonAsync($"/api/v1/spaces/{space.Slug}/todos", new { title = "Shared chore" }, Ct));
        Assert.True((await owner.DeleteAsync($"/api/v1/spaces/{space.Slug}/todos/{id}", Ct)).IsSuccessStatusCode);

        var reader = As(Reader);
        var list = await Json(await reader.GetAsync($"/api/v1/spaces/{space.Slug}/trash", Ct));
        var item = Assert.Single(list.EnumerateArray());
        Assert.Equal("Name " + Owner, item.GetProperty("deletedBy").GetString());
        var trashId = item.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/v1/spaces/{space.Slug}/trash/{trashId}/restore", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/v1/spaces/{space.Slug}/trash", Ct)).StatusCode);
        // The personal trash is a different place.
        Assert.Empty((await Json(await owner.GetAsync("/api/v1/trash", Ct))).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/spaces/{space.Slug}/trash/{trashId}/restore", null, Ct)).StatusCode);
    }
}
