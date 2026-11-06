using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// Integration tests for the MCP Streamable HTTP endpoint. Lives in
// Fishbowl.Host.Tests (not Fishbowl.Mcp.Tests) because it needs the full
// WebApplicationFactory<Program> wiring — pure-unit tests for the dispatcher
// can move into a dedicated Mcp.Tests project later if that pays off.
public class McpEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _dbFactory;
    private readonly ApiKeyRepository _keys;
    private readonly string _dataDir;
    private const string AliceId = "mcp_alice";

    public McpEndpointTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_mcp_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        _dbFactory = new DatabaseFactory(_dataDir);
        _keys = new ApiKeyRepository(_dbFactory);

        using (var db = _dbFactory.CreateSystemConnection())
        {
            db.Execute(
                "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'A', 'a@a', @now)",
                new { id = AliceId, now = DateTime.UtcNow.ToString("o") });
        }

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton<DatabaseFactory>(_dbFactory);
            });
        });
    }

    private async Task<HttpClient> BearerClientAsync(params string[] scopes)
    {
        var issued = await _keys.IssueAsync(AliceId, ContextRef.User(AliceId),
            "mcp-test",
            scopes.Length == 0 ? new[] { "read:notes" } : scopes,
            TestContext.Current.CancellationToken);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", issued.RawToken);
        return client;
    }

    private static StringContent JsonRpc(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Mcp_NoAuth_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/mcp",
            JsonRpc(new { jsonrpc = "2.0", id = 1, method = "initialize" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Mcp_InvalidBearer_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fb_live_bogus_token_xxx");
        var resp = await client.PostAsync("/mcp",
            JsonRpc(new { jsonrpc = "2.0", id = 1, method = "initialize" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Mcp_Initialize_ReturnsServerInfo()
    {
        var client = await BearerClientAsync();
        var resp = await client.PostAsync("/mcp",
            JsonRpc(new { jsonrpc = "2.0", id = 1, method = "initialize" }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = doc.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        var result = root.GetProperty("result");
        Assert.Equal("2025-03-26", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("fishbowl", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.TryGetProperty("capabilities", out var caps));
        Assert.True(caps.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Mcp_ToolsList_ReturnsAllRegisteredTools()
    {
        var client = await BearerClientAsync();
        var resp = await client.PostAsync("/mcp",
            JsonRpc(new { jsonrpc = "2.0", id = 2, method = "tools/list" }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();

        // Assert each known tool is present rather than the exact count —
        // additions elsewhere shouldn't require touching this test.
        Assert.Contains("search_memory", names);
        Assert.Contains("remember", names);
        Assert.Contains("get_memory", names);
        Assert.Contains("update_memory", names);
        Assert.Contains("list_pending", names);
        Assert.Contains("note_list", names);
        Assert.Contains("todo_list", names);
        Assert.Contains("todo_create", names);
        Assert.Contains("todo_update", names);
        Assert.Contains("todo_delete", names);
        Assert.Contains("tag_list", names);
        Assert.Contains("tag_update", names);
        Assert.Contains("list_contacts", names);
        Assert.Contains("find_contact", names);
        Assert.Contains("list_events", names);
    }

    [Fact]
    public async Task Mcp_Notification_Returns202_WithEmptyBody()
    {
        // JSON-RPC notifications have no `id`. Server must not respond with
        // a body — Streamable HTTP recommends 202.
        var client = await BearerClientAsync();
        var resp = await client.PostAsync("/mcp",
            JsonRpc(new { jsonrpc = "2.0", method = "notifications/initialized" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(string.IsNullOrEmpty(body), $"expected empty body, got: {body}");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task Mcp_GetAndDelete_Answer405_NotTheUi(string method)
    {
        // No server-sent stream, no sessions: the Streamable HTTP spec's
        // answer is 405. Before, a GET fell through to the UI's index page
        // with a 200, and the client opened it again every second.
        var client = await BearerClientAsync();
        var request = new HttpRequestMessage(new HttpMethod(method), "/mcp");
        request.Headers.Accept.ParseAdd("text/event-stream");
        var resp = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
        Assert.Contains("POST", resp.Content.Headers.Allow);
        Assert.NotEqual("text/html", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Mcp_UnknownMethod_ReturnsMethodNotFound()
    {
        var client = await BearerClientAsync();
        var resp = await client.PostAsync("/mcp",
            JsonRpc(new { jsonrpc = "2.0", id = 3, method = "totally/unknown" }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("result", out _));
        var err = root.GetProperty("error");
        Assert.Equal(-32601, err.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Mcp_MalformedJson_ReturnsParseError()
    {
        var client = await BearerClientAsync();
        var resp = await client.PostAsync("/mcp",
            new StringContent("{not valid json", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        // Even with a parse failure the server responds 200 with a JSON-RPC
        // error envelope — the HTTP layer is fine, the payload is not.
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var err = doc.RootElement.GetProperty("error");
        Assert.Equal(-32700, err.GetProperty("code").GetInt32());
    }

    // ────────── tools/call ──────────

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string toolName, object arguments)
    {
        var resp = await client.PostAsync("/mcp",
            JsonRpc(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new { name = toolName, arguments },
            }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.Clone();
    }

    // An agent with a space key builds a table and fills it; the web UI's
    // space.db (by id) holds the same rows — and a Member's key can't change
    // the schema whatever its scopes.
    [Fact]
    public async Task Mcp_SpaceKey_TablesLandInTheSpacesOwnDb()
    {
        var ct = TestContext.Current.CancellationToken;
        var spaces = new SpaceRepository(_dbFactory);
        var space = await spaces.CreateAsync(AliceId, "Agent Club", ct);
        var scopes = new[] { "read:tables", "write:tables", "design:tables", "read:notes", "write:notes" };
        var issued = await _keys.IssueAsync(AliceId, ContextRef.Space(space.Slug), "agent", scopes, ct);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.RawToken);

        string Text(JsonElement r) => r.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var created = await CallToolAsync(client, "table_create", new
        {
            name = "inventory",
            description = "What the club owns",
            columns = new object[] { new { name = "qty", type = "integer", required = true, description = "How many" } },
        });
        Assert.Contains("| qty | integer | required; How many |", Text(created));
        await CallToolAsync(client, "row_insert", new { table = "inventory", values = new { title = "Chairs", qty = 40 } });
        var bad = await CallToolAsync(client, "row_insert", new { table = "inventory", values = new { title = "Tables" } });
        Assert.Equal(-32602, bad.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("'qty' is required", bad.GetProperty("error").GetProperty("message").GetString());

        // The same rows through the UI's path (space id), and a note too.
        var rows = await new Fishbowl.Data.Tables.TableRepository(_dbFactory, spaces)
            .QueryAsync(ContextRef.Space(space.Id), "inventory", new Fishbowl.Core.Apps.QuerySpec(), ct);
        Assert.Equal("Chairs", Assert.Single(rows).GetProperty("title").GetString());
        await CallToolAsync(client, "remember", new { title = "agent note", content = "x" });
        Assert.Contains((await new NoteRepository(_dbFactory, new TagRepository(_dbFactory)).GetAllAsync(ContextRef.Space(space.Id), ct: ct)),
            n => n.Title == "agent note");

        // A Member's key: rows yes, schema no.
        await _dbFactory.CreateSystemConnection().ExecuteAsync(
            "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES ('mcp_bob', 'B', 'b@b', @now)", new { now = DateTime.UtcNow.ToString("o") });
        await spaces.AddMemberAsync(space.Id, "mcp_bob", SpaceRole.Member, ct);
        var bobKey = await _keys.IssueAsync("mcp_bob", ContextRef.Space(space.Slug), "bob", scopes, ct);
        var bob = _factory.CreateClient();
        bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bobKey.RawToken);
        // The key loses design:tables: its owner is only a Member.
        Assert.Contains("design:tables", (await CallToolAsync(bob, "table_drop", new { table = "inventory" })).GetProperty("error").GetProperty("message").GetString());
        var agg = await CallToolAsync(bob, "row_aggregate", new { table = "inventory", fn = "sum", column = "qty" });
        Assert.Contains("40", Text(agg));
    }

    // An agent never saw a note's secrets, so it can't rewrite its text —
    // title and tags it may change; REST PUT through a key is refused too.
    [Fact]
    public async Task Mcp_UpdateMemory_LeavesSecretNotesText_Alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var notes = new NoteRepository(_dbFactory, new TagRepository(_dbFactory));
        var id = await notes.CreateAsync(ContextRef.User(AliceId), AliceId,
            new Fishbowl.Core.Models.Note { Title = "Bank", Content = "login\n:::secret\npin 1234\n:::end" }, Fishbowl.Core.Models.NoteSource.Human, ct);
        var client = await BearerClientAsync("read:notes", "write:notes");

        var refused = await CallToolAsync(client, "update_memory", new { id, content = "login\n[secret content hidden]" });
        Assert.Contains("holds secrets", refused.GetProperty("error").GetProperty("message").GetString());
        var renamed = await CallToolAsync(client, "update_memory", new { id, title = "Bank (old)" });
        Assert.False(renamed.TryGetProperty("error", out _));
        var stored = await notes.GetByIdAsync(ContextRef.User(AliceId), id, ct);
        Assert.Equal("Bank (old)", stored!.Title);
        Assert.Contains("pin 1234", stored.Content);

        var put = await client.PutAsJsonAsync($"/api/v1/notes/{id}", new { title = "x", content = "y" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
    }

    [Fact]
    public async Task Mcp_RememberAndGet_RoundTripsANote()
    {
        var client = await BearerClientAsync("read:notes", "write:notes");

        var remember = await CallToolAsync(client, "remember", new
        {
            title = "mcp-test-title",
            content = "some body",
            tags = new[] { "demo" },
        });
        var rememberText = remember.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var rememberDoc = JsonDocument.Parse(rememberText!);
        var noteId = rememberDoc.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(noteId));

        var get = await CallToolAsync(client, "get_memory", new { id = noteId });
        var getText = get.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var getDoc = JsonDocument.Parse(getText!);
        Assert.True(getDoc.RootElement.GetProperty("found").GetBoolean());
        Assert.Equal("mcp-test-title",
            getDoc.RootElement.GetProperty("note").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Mcp_SearchMemory_FindsByTitleSubstring()
    {
        var client = await BearerClientAsync("read:notes", "write:notes");

        await CallToolAsync(client, "remember", new { title = "distinctive-search-target", content = "" });
        await CallToolAsync(client, "remember", new { title = "irrelevant", content = "" });

        var search = await CallToolAsync(client, "search_memory", new { query = "distinctive-search" });
        var text = search.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var doc = JsonDocument.Parse(text!);
        var notes = doc.RootElement.GetProperty("notes");
        // Hybrid search can surface additional low-signal hits from the
        // vector side, so we no longer require exclusivity — the lexical
        // winner must still rank first though.
        Assert.True(notes.GetArrayLength() >= 1);
        Assert.Equal("distinctive-search-target",
            notes[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Mcp_SearchMemory_SurfacesPinnedFlag()
    {
        // Pinned is the user's signal that a note matters. Agents asking
        // search_memory should see it so they can prioritise the user's
        // own emphasis. The other note shape fields already round-trip.
        var repo = new Fishbowl.Data.Repositories.NoteRepository(_dbFactory,
            new Fishbowl.Data.Repositories.TagRepository(_dbFactory));

        await repo.CreateAsync(AliceId,
            new Fishbowl.Core.Models.Note
            {
                Title = "pin-marked-title",
                Content = "important",
                Pinned = true,
            },
            TestContext.Current.CancellationToken);

        var client = await BearerClientAsync("read:notes", "write:notes");
        var search = await CallToolAsync(client, "search_memory", new { query = "pin-marked-title" });
        var text = search.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var doc = JsonDocument.Parse(text!);
        var first = doc.RootElement.GetProperty("notes")[0];

        Assert.True(first.GetProperty("pinned").GetBoolean());
    }

    [Fact]
    public async Task Mcp_SearchMemory_TagFilter_ScopesResults()
    {
        var client = await BearerClientAsync("read:notes", "write:notes");

        await CallToolAsync(client, "remember", new
        {
            title = "alpha-keep",
            content = "shared body",
            tags = new[] { "project-a" },
        });
        await CallToolAsync(client, "remember", new
        {
            title = "alpha-drop",
            content = "shared body",
            tags = new[] { "project-b" },
        });

        var search = await CallToolAsync(client, "search_memory", new
        {
            query = "shared body",
            tags = new[] { "project-a" },
        });
        var text = search.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var doc = JsonDocument.Parse(text!);
        var titles = doc.RootElement.GetProperty("notes")
            .EnumerateArray().Select(n => n.GetProperty("title").GetString()).ToList();

        Assert.Contains("alpha-keep", titles);
        Assert.DoesNotContain("alpha-drop", titles);
    }

    [Fact]
    public async Task Mcp_SearchMemory_SecretsStrippedFromResponse()
    {
        var client = await BearerClientAsync("read:notes", "write:notes");

        var secretMarker = "supersecret-token-abc123";
        await CallToolAsync(client, "remember", new
        {
            title = "secret-holder",
            content = $"Public text.\n:::secret\n{secretMarker}\n:::end\nMore public.",
        });

        var search = await CallToolAsync(client, "search_memory", new { query = "secret-holder" });
        var raw = search.GetRawText();
        Assert.DoesNotContain(secretMarker, raw);
        Assert.Contains("[secret content hidden]", raw);
    }

    [Fact]
    public async Task Mcp_RememberWithoutWriteScope_ReturnsRpcError()
    {
        var client = await BearerClientAsync("read:notes"); // no write:notes
        var resp = await client.PostAsync("/mcp",
            JsonRpc(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new { name = "remember", arguments = new { title = "blocked" } },
            }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var err = doc.RootElement.GetProperty("error");
        // Apps platform tightened this from InternalError (-32603) to InvalidParams
        // (-32602): scope mismatch is caller-fixable, not a server fault.
        Assert.Equal(-32602, err.GetProperty("code").GetInt32());
        Assert.Contains("Scope denied", err.GetProperty("message").GetString() ?? "");
    }

    [Fact]
    public async Task Remember_MissingRequiredTitle_ReturnsInvalidParams()
    {
        // RememberTool throws ArgumentException for "title is required". The
        // dispatcher must surface that as JSON-RPC InvalidParams (-32602)
        // so the MCP client knows to fix its input — not InternalError,
        // which suggests retry-might-help.
        var client = await BearerClientAsync("read:notes", "write:notes");
        var resp = await client.PostAsync("/mcp",
            JsonRpc(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new { name = "remember", arguments = new { content = "no title here" } },
            }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var err = doc.RootElement.GetProperty("error");
        Assert.Equal(-32602, err.GetProperty("code").GetInt32());
        Assert.Contains("title", err.GetProperty("message").GetString() ?? "",
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remember_OversizedContent_ReturnsInvalidParams()
    {
        // ResourceValidationException must also come back as InvalidParams. Use
        // a title (cheap field) past its 8KB cap rather than a 4MB content
        // body — the test stays cheap while exercising the same code path.
        var client = await BearerClientAsync("read:notes", "write:notes");
        var oversizedTitle = new string('x', Fishbowl.Core.Util.NoteLimits.MaxTitleLength + 1);

        var resp = await client.PostAsync("/mcp",
            JsonRpc(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new { name = "remember", arguments = new { title = oversizedTitle } },
            }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var err = doc.RootElement.GetProperty("error");
        Assert.Equal(-32602, err.GetProperty("code").GetInt32());
        Assert.Contains("title", err.GetProperty("message").GetString() ?? "");
    }

    [Fact]
    public async Task Mcp_UnknownTool_ReturnsMethodNotFound()
    {
        var client = await BearerClientAsync("read:notes");
        var resp = await client.PostAsync("/mcp",
            JsonRpc(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new { name = "nope", arguments = new { } },
            }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(-32601,
            doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    // ────────── auto-tagging (Task 4.4) ──────────

    [Fact]
    public async Task Remember_AutoTagsSourceMcpAndReviewPending()
    {
        var client = await BearerClientAsync("read:notes", "write:notes");

        var resp = await CallToolAsync(client, "remember", new { title = "tagged-on-create" });
        var text = resp.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var doc = JsonDocument.Parse(text!);
        var tags = doc.RootElement.GetProperty("note").GetProperty("tags")
            .EnumerateArray().Select(t => t.GetString()).ToHashSet();

        Assert.Contains("source:mcp", tags);
        Assert.Contains("review:pending", tags);
    }

    [Fact]
    public async Task Update_ViaMcp_ReAddsReviewPending()
    {
        var client = await BearerClientAsync("read:notes", "write:notes");

        var created = await CallToolAsync(client, "remember",
            new { title = "to-edit", tags = new[] { "custom" } });
        var createdText = created.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var createdDoc = JsonDocument.Parse(createdText!);
        var id = createdDoc.RootElement.GetProperty("id").GetString();

        // A human approving the note would strip review:pending. Simulate
        // that here by hitting the cookie-authed REST path via the repo,
        // then re-updating through MCP to verify it re-adds the tag.
        var repo = new Fishbowl.Data.Repositories.NoteRepository(_dbFactory,
            new Fishbowl.Data.Repositories.TagRepository(_dbFactory));
        var asHuman = await repo.GetByIdAsync(AliceId, id!, TestContext.Current.CancellationToken);
        await repo.UpdateAsync(AliceId, asHuman!, TestContext.Current.CancellationToken);
        var afterHuman = await repo.GetByIdAsync(AliceId, id!, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("review:pending", afterHuman!.Tags);

        // Now MCP updates → review:pending comes back.
        var updated = await CallToolAsync(client, "update_memory",
            new { id, content = "edited via mcp" });
        var updatedText = updated.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var updatedDoc = JsonDocument.Parse(updatedText!);
        var tags = updatedDoc.RootElement.GetProperty("note").GetProperty("tags")
            .EnumerateArray().Select(t => t.GetString()).ToHashSet();
        Assert.Contains("review:pending", tags);
        Assert.Contains("source:mcp", tags);
    }

    [Fact]
    public async Task HumanEdit_StripsReviewPending()
    {
        // Seed a note that already has review:pending, simulating a prior
        // MCP write. Update via the repo with NoteSource.Human → tag gone.
        var repo = new Fishbowl.Data.Repositories.NoteRepository(_dbFactory,
            new Fishbowl.Data.Repositories.TagRepository(_dbFactory));
        var id = await repo.CreateAsync(
            new ContextRef(ContextType.User, AliceId), AliceId,
            new Fishbowl.Core.Models.Note
            {
                Title = "mcp-flagged",
                Tags = new List<string> { "source:mcp", "review:pending" },
            },
            Fishbowl.Core.Models.NoteSource.Mcp,
            TestContext.Current.CancellationToken);

        var note = await repo.GetByIdAsync(AliceId, id, TestContext.Current.CancellationToken);
        Assert.Contains("review:pending", note!.Tags);

        note.Title = "edited by human";
        await repo.UpdateAsync(
            new ContextRef(ContextType.User, AliceId), note,
            Fishbowl.Core.Models.NoteSource.Human,
            TestContext.Current.CancellationToken);

        var after = await repo.GetByIdAsync(AliceId, id, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("review:pending", after!.Tags);
    }

    [Fact]
    public async Task HumanCreate_CannotForgeSourceMcpTag()
    {
        // Audit invariant: source:mcp is provenance, not user-assignable.
        // A cookie/Human path that stuffs source:mcp into the tags array
        // must not be able to claim agent attribution for a manual write.
        var repo = new Fishbowl.Data.Repositories.NoteRepository(_dbFactory,
            new Fishbowl.Data.Repositories.TagRepository(_dbFactory));

        var id = await repo.CreateAsync(AliceId,
            new Fishbowl.Core.Models.Note
            {
                Title = "human-tries-to-forge",
                Tags = new List<string> { "source:mcp", "custom" },
            },
            TestContext.Current.CancellationToken);

        var stored = await repo.GetByIdAsync(AliceId, id, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("source:mcp", stored!.Tags);
        Assert.Contains("custom", stored.Tags);
    }

    [Fact]
    public async Task HumanCreate_CannotForgeReviewPending()
    {
        // review:pending is also UserAssignable=false. A human can't mark
        // their own write as needing-review by injecting the tag — only
        // MCP writes get that flag.
        var repo = new Fishbowl.Data.Repositories.NoteRepository(_dbFactory,
            new Fishbowl.Data.Repositories.TagRepository(_dbFactory));

        var id = await repo.CreateAsync(AliceId,
            new Fishbowl.Core.Models.Note
            {
                Title = "human-tries-to-flag",
                Tags = new List<string> { "review:pending" },
            },
            TestContext.Current.CancellationToken);

        var stored = await repo.GetByIdAsync(AliceId, id, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("review:pending", stored!.Tags);
    }

    [Fact]
    public async Task HumanUpdate_PreservesSourceMcp_EvenIfOmittedFromPut()
    {
        // Audit invariant flip-side: a human update that doesn't include
        // source:mcp in its tags array must not be able to wipe the
        // provenance marker. SystemTags marks source:mcp as
        // UserRemovable=false; preservation re-adds it inside the update tx.
        var repo = new Fishbowl.Data.Repositories.NoteRepository(_dbFactory,
            new Fishbowl.Data.Repositories.TagRepository(_dbFactory));

        var id = await repo.CreateAsync(
            new ContextRef(ContextType.User, AliceId), AliceId,
            new Fishbowl.Core.Models.Note { Title = "mcp-origin", Tags = new() },
            Fishbowl.Core.Models.NoteSource.Mcp,
            TestContext.Current.CancellationToken);

        // Sanity: the row carries source:mcp from the MCP create.
        var afterCreate = await repo.GetByIdAsync(AliceId, id, TestContext.Current.CancellationToken);
        Assert.Contains("source:mcp", afterCreate!.Tags);

        // Human edit that omits source:mcp from its tags array.
        afterCreate.Tags = new List<string> { "approved" };
        await repo.UpdateAsync(AliceId, afterCreate, TestContext.Current.CancellationToken);

        var afterEdit = await repo.GetByIdAsync(AliceId, id, TestContext.Current.CancellationToken);
        Assert.Contains("source:mcp", afterEdit!.Tags);
        Assert.Contains("approved", afterEdit.Tags);
    }

    [Fact]
    public async Task CookieWrite_DoesNotAddSourceMcp()
    {
        // Direct repository call with the default (Human) overload — proves
        // the human path never injects source:mcp or review:pending.
        var repo = new Fishbowl.Data.Repositories.NoteRepository(_dbFactory,
            new Fishbowl.Data.Repositories.TagRepository(_dbFactory));
        var id = await repo.CreateAsync(AliceId,
            new Fishbowl.Core.Models.Note { Title = "human-wrote-this" },
            TestContext.Current.CancellationToken);

        var stored = await repo.GetByIdAsync(AliceId, id, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("source:mcp", stored!.Tags);
        Assert.DoesNotContain("review:pending", stored.Tags);
    }

    [Fact]
    public async Task Mcp_ListPending_ReturnsOnlyPendingNotes()
    {
        // Seed via direct repository. Use NoteSource.Mcp for the pending
        // note so ApplySourceTags auto-adds review:pending — the default
        // (Human) overload would strip it.
        var notes = new Fishbowl.Data.Repositories.NoteRepository(_dbFactory,
            new Fishbowl.Data.Repositories.TagRepository(_dbFactory));
        await notes.CreateAsync(
            new ContextRef(ContextType.User, AliceId), AliceId,
            new Fishbowl.Core.Models.Note { Title = "pending-1" },
            Fishbowl.Core.Models.NoteSource.Mcp,
            TestContext.Current.CancellationToken);
        await notes.CreateAsync(AliceId, new Fishbowl.Core.Models.Note
        {
            Title = "approved-1",
        }, TestContext.Current.CancellationToken);

        var client = await BearerClientAsync("read:notes");
        var resp = await CallToolAsync(client, "list_pending", new { });
        var text = resp.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var doc = JsonDocument.Parse(text!);
        var list = doc.RootElement.GetProperty("notes");
        var titles = list.EnumerateArray()
            .Select(n => n.GetProperty("title").GetString()).ToHashSet();
        Assert.Contains("pending-1", titles);
        Assert.DoesNotContain("approved-1", titles);
    }

    // ────────── note_list: a copy kept in sync ──────────

    private static JsonElement ToolResult(JsonElement rpc)
    {
        Assert.False(rpc.TryGetProperty("error", out var error), error.ToString());
        var text = rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static List<string> Ids(JsonElement result) =>
        result.GetProperty("notes").EnumerateArray().Select(n => n.GetProperty("id").GetString()!).ToList();

    [Fact]
    public async Task Mcp_NoteList_PagesInChangeOrder_ThenOnlyWhatChangedOrWentSince()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = ContextRef.User(AliceId);
        var notes = new NoteRepository(_dbFactory, new TagRepository(_dbFactory));
        var trash = new TrashRepository(_dbFactory, notes);
        var made = new List<string>();
        for (var i = 1; i <= 5; i++)
            made.Add(await notes.CreateAsync(ctx, AliceId, new Note { Title = $"doc {i}", Content = $"body {i}" }, ct));

        var client = await BearerClientAsync("read:notes");

        // Every note, oldest change first, two a page; light: no content.
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        JsonElement page;
        do
        {
            page = ToolResult(await CallToolAsync(client, "note_list", cursor is null ? new { limit = 2 } : new { limit = 2, cursor }));
            seen.AddRange(Ids(page));
            Assert.All(page.GetProperty("notes").EnumerateArray(), n =>
            {
                Assert.False(n.TryGetProperty("content", out _));
                Assert.EndsWith("Z", n.GetProperty("updatedAt").GetString());
            });
            // The last page has no nextCursor (nulls stay off the wire).
            cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            pages++;
        } while (cursor is not null);
        Assert.Equal(made, seen);
        Assert.Equal(3, pages);
        var newest = page.GetProperty("notes").EnumerateArray().Last().GetProperty("updatedAt").GetString()!;

        // Nothing changed since the newest: an empty page, nothing deleted.
        var none = ToolResult(await CallToolAsync(client, "note_list", new { updated_since = newest }));
        Assert.Empty(Ids(none));
        Assert.Empty(none.GetProperty("deleted").EnumerateArray());

        // One edited, one deleted: the edit is the change, the delete is listed.
        var second = (await notes.GetByIdAsync(ctx, made[1], ct))!;
        second.Content = "body 2, edited";
        await notes.UpdateAsync(ctx, second, ct);
        await notes.DeleteAsync(ctx, made[3], ct);
        var changed = ToolResult(await CallToolAsync(client, "note_list", new { updated_since = newest, include_content = true }));
        Assert.Equal(new[] { made[1] }, Ids(changed));
        Assert.Equal("body 2, edited", changed.GetProperty("notes")[0].GetProperty("content").GetString());
        var deleted = changed.GetProperty("deleted").EnumerateArray().ToList();
        Assert.Single(deleted);
        Assert.Equal(made[3], deleted[0].GetProperty("id").GetString());
        Assert.Equal("doc 4", deleted[0].GetProperty("title").GetString());

        // Restored, it changed again: a copy that dropped it sees it come back.
        var since = changed.GetProperty("notes")[0].GetProperty("updatedAt").GetString()!;
        var item = (await trash.ListAsync(ctx, ct)).Single(t => t.ItemId == made[3]);
        Assert.Equal(TrashRestore.Restored, (await trash.RestoreAsync(ctx, item.Id, ct)).Result);
        var back = ToolResult(await CallToolAsync(client, "note_list", new { updated_since = since }));
        Assert.Equal(new[] { made[3] }, Ids(back));
        Assert.Empty(back.GetProperty("deleted").EnumerateArray());
    }

    [Fact]
    public async Task Mcp_NoteList_FiltersByTags_AndRefusesAForeignCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = ContextRef.User(AliceId);
        var notes = new NoteRepository(_dbFactory, new TagRepository(_dbFactory));
        var plan = await notes.CreateAsync(ctx, AliceId, new Note { Title = "plan", Tags = new() { "kind:plan", "status:draft" } }, ct);
        var decision = await notes.CreateAsync(ctx, AliceId, new Note { Title = "decision", Tags = new() { "kind:decision", "status:draft" } }, ct);
        await notes.CreateAsync(ctx, AliceId, new Note { Title = "untagged" }, ct);

        var client = await BearerClientAsync("read:notes");
        var any = ToolResult(await CallToolAsync(client, "note_list", new { tags = new[] { "kind:plan", "kind:decision" } }));
        Assert.Equal(new[] { plan, decision }, Ids(any));
        var all = ToolResult(await CallToolAsync(client, "note_list", new { tags = new[] { "kind:plan", "status:draft" }, match = "all" }));
        Assert.Equal(new[] { plan }, Ids(all));
        Assert.Contains("kind:plan", all.GetProperty("notes")[0].GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

        var bad = await CallToolAsync(client, "note_list", new { cursor = "not-a-cursor" });
        Assert.Equal(-32602, bad.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("cursor", bad.GetProperty("error").GetProperty("message").GetString());
        var badTime = await CallToolAsync(client, "note_list", new { updated_since = "yesterday" });
        Assert.Equal(-32602, badTime.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Mcp_UpdateMemory_AddsAndRemovesTags_FindsBySlug_AppendsToALockedNote()
    {
        var client = await BearerClientAsync("read:notes", "write:notes");
        var made = ToolResult(await CallToolAsync(client, "remember",
            new { title = "Decision 3", content = "Forward rendering.", tags = new[] { "kind:decision", "status:draft" }, slug = "decisions/0003-forward" }));
        var id = made.GetProperty("id").GetString()!;

        // A status change in place: the other tags stay.
        var moved = ToolResult(await CallToolAsync(client, "update_memory",
            new { by_slug = "Decisions/0003-FORWARD", add_tags = new[] { "status:approved" }, remove_tags = new[] { "status:draft" } }));
        var tags = moved.GetProperty("note").GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("status:approved", tags);
        Assert.DoesNotContain("status:draft", tags);
        Assert.Contains("kind:decision", tags);
        Assert.Contains("review:pending", tags);
        Assert.Equal("decisions/0003-forward", moved.GetProperty("note").GetProperty("slug").GetString());

        // Locked: the text holds, an append goes through, an agent can't unlock.
        ToolResult(await CallToolAsync(client, "update_memory", new { id, @lock = true }));
        var refused = await CallToolAsync(client, "update_memory", new { id, content = "Deferred rendering." });
        Assert.Equal(-32602, refused.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("locked", refused.GetProperty("error").GetProperty("message").GetString());
        var appended = ToolResult(await CallToolAsync(client, "update_memory", new { id, append = "Superseded by Decision 9." }));
        Assert.Equal("Forward rendering.\n\nSuperseded by Decision 9.", appended.GetProperty("note").GetProperty("content").GetString());
        var unlock = await CallToolAsync(client, "update_memory", new { id, @lock = false });
        Assert.Equal(-32602, unlock.GetProperty("error").GetProperty("code").GetInt32());

        // get_memory by slug; a second note can't take the slug.
        var got = ToolResult(await CallToolAsync(client, "get_memory", new { slug = "decisions/0003-forward" }));
        Assert.Equal(id, got.GetProperty("note").GetProperty("id").GetString());
        Assert.True(got.GetProperty("note").GetProperty("locked").GetBoolean());
        var taken = await CallToolAsync(client, "remember", new { title = "Other", slug = "decisions/0003-forward" });
        Assert.Contains("slug", taken.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Mcp_TodoTools_CreateListUpdateDelete_WithTags()
    {
        var client = await BearerClientAsync("read:tasks", "write:tasks");
        var made = ToolResult(await CallToolAsync(client, "todo_create",
            new { title = "Bake lightmaps", due = "2026-11-10", tags = new[] { "area:lighting", "feature:night" } }));
        var id = made.GetProperty("id").GetString()!;
        var todo = made.GetProperty("todo");
        Assert.Equal("2026-11-10T09:00:00Z", todo.GetProperty("dueAt").GetString());
        Assert.Contains("source:mcp", todo.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        ToolResult(await CallToolAsync(client, "todo_create", new { title = "Write the README" }));

        var lighting = ToolResult(await CallToolAsync(client, "todo_list", new { tags = new[] { "area:lighting" } }));
        Assert.Equal(1, lighting.GetProperty("count").GetInt32());

        var done = ToolResult(await CallToolAsync(client, "todo_update",
            new { id, done = true, add_tags = new[] { "status:done" }, remove_tags = new[] { "feature:night" }, due = "" }));
        var t = done.GetProperty("todo");
        Assert.True(t.GetProperty("done").GetBoolean());
        Assert.False(t.TryGetProperty("dueAt", out _));
        var tags = t.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("area:lighting", tags);
        Assert.Contains("status:done", tags);
        Assert.DoesNotContain("feature:night", tags);

        Assert.Equal(1, ToolResult(await CallToolAsync(client, "todo_list", new { })).GetProperty("count").GetInt32());
        Assert.Equal(1, ToolResult(await CallToolAsync(client, "todo_list", new { state = "done" })).GetProperty("count").GetInt32());
        var reopened = ToolResult(await CallToolAsync(client, "todo_update", new { id, done = false }));
        Assert.False(reopened.GetProperty("todo").GetProperty("done").GetBoolean());

        Assert.True(ToolResult(await CallToolAsync(client, "todo_delete", new { id })).GetProperty("deleted").GetBoolean());
        Assert.Equal(1, ToolResult(await CallToolAsync(client, "todo_list", new { state = "all" })).GetProperty("count").GetInt32());

        // A key without write:tasks writes nothing.
        var reader = await BearerClientAsync("read:tasks");
        var refused = await CallToolAsync(reader, "todo_create", new { title = "x" });
        Assert.True(refused.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Mcp_TagTools_ListUsage_SetColourAndMeaning_Rename()
    {
        var client = await BearerClientAsync("read:tags", "write:tags", "write:tasks", "write:notes");
        ToolResult(await CallToolAsync(client, "remember", new { title = "n", tags = new[] { "status:draft" } }));
        ToolResult(await CallToolAsync(client, "todo_create", new { title = "t", tags = new[] { "status:draft" } }));

        var set = ToolResult(await CallToolAsync(client, "tag_update",
            new { name = "status:draft", color = "orange", description = "Written, not yet approved" }));
        Assert.Equal("orange", set.GetProperty("tag").GetProperty("color").GetString());
        var renamed = ToolResult(await CallToolAsync(client, "tag_update", new { name = "status:draft", rename_to = "status:wip" }));
        Assert.Equal("status:wip", renamed.GetProperty("tag").GetProperty("name").GetString());
        Assert.Equal("Written, not yet approved", renamed.GetProperty("tag").GetProperty("description").GetString());

        var list = ToolResult(await CallToolAsync(client, "tag_list", new { }));
        var wip = list.GetProperty("tags").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "status:wip");
        Assert.Equal(1, wip.GetProperty("notes").GetInt32());
        Assert.Equal(1, wip.GetProperty("todos").GetInt32());
        var system = await CallToolAsync(client, "tag_update", new { name = "source:mcp", rename_to = "origin" });
        Assert.True(system.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Mcp_ListContacts_ReturnsPersistedContacts()
    {
        var contacts = new ContactRepository(_dbFactory);
        await contacts.CreateAsync(AliceId,
            new Fishbowl.Core.Models.Contact
            {
                Name = "Alice Example",
                Email = "alice@example.com",
                Phone = "+1-555",
            },
            TestContext.Current.CancellationToken);
        await contacts.CreateAsync(AliceId,
            new Fishbowl.Core.Models.Contact
            {
                Name = "Shelved",
                Archived = true,
            },
            TestContext.Current.CancellationToken);

        var client = await BearerClientAsync("read:contacts");
        var resp = await CallToolAsync(client, "list_contacts", new { });
        var text = resp.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var doc = JsonDocument.Parse(text!);
        var list = doc.RootElement.GetProperty("contacts");
        var names = list.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToHashSet();

        Assert.Contains("Alice Example", names);
        Assert.DoesNotContain("Shelved", names);   // archived hidden by default

        // One of the entries has the rich fields populated — sanity check
        // that email/phone aren't dropped by the MCP wrapper.
        var alice = list.EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "Alice Example");
        Assert.Equal("alice@example.com", alice.GetProperty("email").GetString());
        Assert.Equal("+1-555", alice.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task Mcp_ListContacts_WithoutContactsScope_Returns403Envelope()
    {
        // Scope gating for the new contacts tool — the dispatcher returns a
        // JSON-RPC error with code -32000 when the Bearer token is missing
        // the required scope, mirroring how other write:* gates behave.
        var client = await BearerClientAsync("read:notes");  // no read:contacts
        var resp = await CallToolAsync(client, "list_contacts", new { });
        Assert.True(resp.TryGetProperty("error", out var err),
            "expected JSON-RPC error envelope for missing scope");
        Assert.NotEqual(0, err.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Mcp_ListEvents_RespectsUpcomingDaysShortcut()
    {
        var eventRepo = new EventRepository(_dbFactory);
        var now = DateTime.UtcNow;
        await eventRepo.CreateAsync(AliceId, new Fishbowl.Core.Models.Event
        {
            Title = "soon",
            StartAt = now.AddDays(2),
        }, TestContext.Current.CancellationToken);
        await eventRepo.CreateAsync(AliceId, new Fishbowl.Core.Models.Event
        {
            Title = "later",
            StartAt = now.AddDays(30),
        }, TestContext.Current.CancellationToken);

        var client = await BearerClientAsync("read:events");
        var resp = await CallToolAsync(client, "list_events", new { upcoming_days = 7 });
        var text = resp.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString();
        using var doc = JsonDocument.Parse(text!);
        var list = doc.RootElement.GetProperty("events");
        var titles = list.EnumerateArray()
            .Select(e => e.GetProperty("title").GetString()).ToHashSet();
        Assert.Contains("soon", titles);
        Assert.DoesNotContain("later", titles);
    }

    [Fact]
    public async Task Mcp_FindContact_ReturnsMatchingHits()
    {
        var contacts = new ContactRepository(_dbFactory);
        await contacts.CreateAsync(AliceId,
            new Fishbowl.Core.Models.Contact
            {
                Name = "Alice Example",
                Email = "alice@studio.example",
                Notes = "venue manager",
            },
            TestContext.Current.CancellationToken);
        await contacts.CreateAsync(AliceId,
            new Fishbowl.Core.Models.Contact
            {
                Name = "Carol",
                Notes = "unrelated",
            },
            TestContext.Current.CancellationToken);

        var client = await BearerClientAsync("read:contacts");
        var resp = await CallToolAsync(client, "find_contact", new { query = "venue" });
        var text = resp.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString();
        using var doc = JsonDocument.Parse(text!);
        var list = doc.RootElement.GetProperty("contacts");
        var names = list.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("Alice Example", names);
        Assert.DoesNotContain("Carol", names);
    }

    [Fact]
    public async Task Mcp_StringId_IsEchoedBackExactly()
    {
        // MCP clients use string ids too; we must not mangle them.
        var client = await BearerClientAsync();
        var resp = await client.PostAsync("/mcp",
            JsonRpc(new { jsonrpc = "2.0", id = "abc-42", method = "initialize" }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("abc-42", doc.RootElement.GetProperty("id").GetString());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }
}
