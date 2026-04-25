using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
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
            content = $"Public text.\n::secret\n{secretMarker}\n::end\nMore public.",
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
        Assert.Equal(-32603, err.GetProperty("code").GetInt32());
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
