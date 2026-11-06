using System.Net;
using System.Net.Http.Headers;
using System.Text;
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

// What the Notes app needs beyond CRUD (NoteToolsApi, spec
// 2026-11-06-notes-for-agents): a patch that adds and removes tags in place
// (a board move keeps review:pending; a text change approves), slugs unique in
// any case, a lock only a person — the owner, a space's Admin — lifts, the
// versions agents leave and their restore, saved filters (writers only), and
// todos' tags (kept by a client that doesn't send them, rewritten by a rename).
public class NoteToolsApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string Owner = "nt_owner";
    private const string Member = "nt_member";
    private const string Reader = "nt_reader";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public NoteToolsApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_note_tools_" + Path.GetRandomFileName());
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
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> Error(HttpResponseMessage r, HttpStatusCode status)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.True(r.StatusCode == status, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("error").GetString()!;
    }

    private static HttpRequestMessage Patch(string url, object body) => new(HttpMethod.Patch, url) { Content = Json(body) };

    private static List<string> Tags(JsonElement note) =>
        note.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToList();

    private async Task<Space> SpaceAsync()
    {
        var spaces = new SpaceRepository(_db);
        var space = await spaces.CreateAsync(Owner, "Docs " + Guid.NewGuid().ToString("N")[..6], Ct);
        await spaces.AddMemberAsync(space.Id, Member, SpaceRole.Member, Ct);
        await spaces.AddMemberAsync(space.Id, Reader, SpaceRole.Reader, Ct);
        return space;
    }

    private NoteRepository Notes => new(_db, new TagRepository(_db));

    [Fact]
    public async Task Patch_ChangesTagsInPlace_ABoardMoveKeepsReview_ATextChangeApproves()
    {
        var ctx = ContextRef.User(Owner);
        var id = await Notes.CreateAsync(ctx, Owner, new Note { Title = "Plan", Tags = new() { "kind:plan", "status:draft" } }, NoteSource.Mcp, Ct);
        var client = As(Owner);

        var moved = await Ok(await client.SendAsync(Patch($"/api/v1/notes/{id}",
            new { addTags = new[] { "status:approved" }, removeTags = new[] { "status:draft" } }), Ct));
        var tags = Tags(moved);
        Assert.Contains("status:approved", tags);
        Assert.DoesNotContain("status:draft", tags);
        Assert.Contains("kind:plan", tags);
        Assert.Contains("review:pending", tags);   // a move is no approval
        Assert.Contains("source:mcp", tags);

        var edited = await Ok(await client.SendAsync(Patch($"/api/v1/notes/{id}", new { content = "Approved by editing" }), Ct));
        Assert.DoesNotContain("review:pending", Tags(edited));
        Assert.Contains("source:mcp", Tags(edited));

        // Nobody takes provenance off, and a person can't mark a note for review.
        var tried = await Ok(await client.SendAsync(Patch($"/api/v1/notes/{id}",
            new { removeTags = new[] { "source:mcp" }, addTags = new[] { "review:pending" } }), Ct));
        Assert.Contains("source:mcp", Tags(tried));
        Assert.DoesNotContain("review:pending", Tags(tried));
    }

    [Fact]
    public async Task Slug_IsUniqueInAnyCase_AndKeptByAnUpdateWithoutIt()
    {
        var client = As(Owner);
        var a = await Ok(await client.PostAsync("/api/v1/notes", Json(new { title = "A", slug = "knowledge/Unity-tips" }), Ct));
        var id = a.GetProperty("id").GetString()!;
        Assert.Equal("slug_taken",
            await Error(await client.PostAsync("/api/v1/notes", Json(new { title = "B", slug = "KNOWLEDGE/unity-tips" }), Ct), HttpStatusCode.Conflict));
        Assert.Equal("slug_invalid",
            await Error(await client.PostAsync("/api/v1/notes", Json(new { title = "C", slug = "../escape" }), Ct), HttpStatusCode.BadRequest));

        // A PUT that doesn't name a slug keeps it; "" clears it.
        await Ok(await client.PutAsync($"/api/v1/notes/{id}", Json(new { title = "A2", content = "x" }), Ct));
        Assert.Equal("knowledge/Unity-tips", (await Notes.GetByIdAsync(ContextRef.User(Owner), id, Ct))!.Slug);
        Assert.Equal(id, (await Notes.GetBySlugAsync(ContextRef.User(Owner), "knowledge/unity-tips", Ct))!.Id);
        await Ok(await client.SendAsync(Patch($"/api/v1/notes/{id}", new { slug = "" }), Ct));
        Assert.Null((await Notes.GetByIdAsync(ContextRef.User(Owner), id, Ct))!.Slug);
    }

    [Fact]
    public async Task Lock_KeepsTitleAndText_TakesAnAppend_OnlyAnAdminLiftsIt()
    {
        var space = await SpaceAsync();
        var url = $"/api/v1/spaces/{space.Slug}/notes";
        var note = await Ok(await As(Member).PostAsync(url, Json(new { title = "Decision 7", content = "We use URP." }), Ct));
        var id = note.GetProperty("id").GetString()!;

        // A member may lock …
        Assert.True((await Ok(await As(Member).SendAsync(Patch($"{url}/{id}", new { locked = true }), Ct))).GetProperty("locked").GetBoolean());
        // … then the text holds, through a patch and through a PUT.
        Assert.Equal("note_locked", await Error(await As(Member).SendAsync(Patch($"{url}/{id}", new { content = "We use HDRP." }), Ct), HttpStatusCode.Conflict));
        Assert.Equal("note_locked", await Error(await As(Member).PutAsync($"{url}/{id}", Json(new { title = "Decision 7", content = "We use HDRP." }), Ct), HttpStatusCode.Conflict));
        // An append and a tag change go through.
        var appended = await Ok(await As(Member).SendAsync(Patch($"{url}/{id}", new { append = "Superseded by Decision 9.", addTags = new[] { "status:superseded" } }), Ct));
        Assert.Equal("We use URP.\n\nSuperseded by Decision 9.", appended.GetProperty("content").GetString());
        Assert.Contains("status:superseded", Tags(appended));
        // Only an Admin or the Owner lifts it.
        Assert.Equal("note_locked", await Error(await As(Member).SendAsync(Patch($"{url}/{id}", new { locked = false }), Ct), HttpStatusCode.Conflict));
        Assert.False((await Ok(await As(Owner).SendAsync(Patch($"{url}/{id}", new { locked = false }), Ct))).GetProperty("locked").GetBoolean());
        // A reader writes nothing.
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Reader).SendAsync(Patch($"{url}/{id}", new { addTags = new[] { "x" } }), Ct)).StatusCode);
    }

    [Fact]
    public async Task AnAgentsChange_LeavesTheVersionBefore_WhichAPersonRestores()
    {
        var ctx = ContextRef.User(Owner);
        var id = await Notes.CreateAsync(ctx, Owner, new Note { Title = "Spec", Content = "first", Tags = new() { "kind:spec" } }, Ct);
        await Notes.PatchAsync(ctx, id, new NotePatch { Content = "second", AddTags = new() { "status:draft" } }, NoteSource.Mcp, Owner, Ct);
        await Notes.PatchAsync(ctx, id, new NotePatch { Content = "third" }, NoteSource.Mcp, Owner, Ct);
        // A person's edit leaves none.
        await Notes.PatchAsync(ctx, id, new NotePatch { Title = "Spec v3" }, NoteSource.Human, Owner, Ct);

        var client = As(Owner);
        var versions = (await Ok(await client.GetAsync($"/api/v1/notes/{id}/versions", Ct))).EnumerateArray().ToList();
        Assert.Equal(2, versions.Count);
        Assert.False(versions[0].TryGetProperty("content", out _));
        var oldest = await Ok(await client.GetAsync($"/api/v1/notes/{id}/versions/{versions[1].GetProperty("id").GetString()}", Ct));
        Assert.Equal("first", oldest.GetProperty("content").GetString());

        var back = await Ok(await client.PostAsync($"/api/v1/notes/{id}/versions/{versions[1].GetProperty("id").GetString()}/restore", null, Ct));
        Assert.Equal("first", back.GetProperty("content").GetString());
        Assert.Equal("Spec", back.GetProperty("title").GetString());
        Assert.DoesNotContain("status:draft", Tags(back));
    }

    [Fact]
    public async Task SavedFilters_AreTheWorkspaces_WritersMakeThem()
    {
        var space = await SpaceAsync();
        var url = $"/api/v1/spaces/{space.Slug}/notes/filters";
        var made = await Ok(await As(Member).PostAsync(url, Json(new { name = "Open decisions", expr = "kind:decision AND NOT status:superseded" }), Ct));
        var fid = made.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Reader).PostAsync(url, Json(new { name = "x", expr = "y" }), Ct)).StatusCode);
        Assert.Equal("invalid_value", await Error(await As(Member).PostAsync(url, Json(new { name = "", expr = "y" }), Ct), HttpStatusCode.BadRequest));

        var list = (await Ok(await As(Reader).GetAsync(url, Ct))).EnumerateArray().ToList();
        Assert.Single(list);
        Assert.Equal("kind:decision AND NOT status:superseded", list[0].GetProperty("expr").GetString());
        var renamed = await Ok(await As(Owner).SendAsync(Patch($"{url}/{fid}", new { name = "Decisions in force" }), Ct));
        Assert.Equal("Decisions in force", renamed.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await As(Member).DeleteAsync($"{url}/{fid}", Ct)).StatusCode);
        Assert.Empty((await Ok(await As(Reader).GetAsync(url, Ct))).EnumerateArray());
        // The personal workspace has its own.
        Assert.Empty((await Ok(await As(Owner).GetAsync("/api/v1/notes/filters", Ct))).EnumerateArray());
    }

    [Fact]
    public async Task TodoTags_FollowTheNotesRules_AndARename()
    {
        var space = await SpaceAsync();
        var url = $"/api/v1/spaces/{space.Slug}/todos";
        var made = await Ok(await As(Member).PostAsync(url, Json(new { title = "Bake lightmaps", tags = new[] { "Area:Lighting", "source:mcp" } }), Ct));
        var id = made.GetProperty("id").GetString()!;
        var todos = new TodoRepository(_db);
        var stored = (await todos.GetByIdAsync(ContextRef.Space(space.Id), id, Ct))!;
        Assert.Equal(new[] { "area:lighting" }, stored.Tags);   // normalised; a person can't give source:mcp

        // A client that sends no tags keeps them.
        await Ok(await As(Member).PutAsync($"{url}/{id}", Json(new { title = "Bake lightmaps tonight" }), Ct));
        Assert.Equal(new[] { "area:lighting" }, (await todos.GetByIdAsync(ContextRef.Space(space.Id), id, Ct))!.Tags);

        // A rename follows into todos, and the tag counts them.
        await Ok(await As(Member).PostAsync($"/api/v1/spaces/{space.Slug}/tags/area:lighting/rename", Json(new { newName = "area:light" }), Ct));
        Assert.Equal(new[] { "area:light" }, (await todos.GetByIdAsync(ContextRef.Space(space.Id), id, Ct))!.Tags);
        var tags = (await Ok(await As(Reader).GetAsync($"/api/v1/spaces/{space.Slug}/tags", Ct))).EnumerateArray().ToList();
        Assert.Equal(1, tags.Single(t => t.GetProperty("name").GetString() == "area:light").GetProperty("todoCount").GetInt32());

        // A tag's description.
        Assert.Equal(HttpStatusCode.NoContent, (await As(Member).PutAsync($"/api/v1/spaces/{space.Slug}/tags/area:light/description", Json(new { description = "Lighting and baking" }), Ct)).StatusCode);
        tags = (await Ok(await As(Reader).GetAsync($"/api/v1/spaces/{space.Slug}/tags", Ct))).EnumerateArray().ToList();
        Assert.Equal("Lighting and baking", tags.Single(t => t.GetProperty("name").GetString() == "area:light").GetProperty("description").GetString());
    }

    [Fact]
    public async Task APurgedNote_TakesItsVersionsAlong()
    {
        var ctx = ContextRef.User(Owner);
        var id = await Notes.CreateAsync(ctx, Owner, new Note { Title = "Gone", Content = "a" }, Ct);
        await Notes.PatchAsync(ctx, id, new NotePatch { Content = "b" }, NoteSource.Mcp, Owner, Ct);
        await Notes.DeleteAsync(ctx, id, Ct);
        Assert.Single(await Notes.ListVersionsAsync(ctx, id, Ct));   // kept while it is in the trash
        await new TrashRepository(_db, Notes).EmptyAsync(ctx, Ct);
        Assert.Empty(await Notes.ListVersionsAsync(ctx, id, Ct));
    }
}
