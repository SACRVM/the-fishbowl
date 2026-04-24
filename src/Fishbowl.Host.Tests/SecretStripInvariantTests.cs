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

// Non-negotiable invariant: the plaintext inside `::secret`…`::end` blocks
// must never appear in ANY MCP response, and `content_secret` blobs must
// never be serialised on the way out. Every tool that returns note content
// is exercised here — if a new tool lands that also returns notes, add a
// fact. CONCEPT § Core Philosophy and § MCP Server make this load-bearing.
public class SecretStripInvariantTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string SecretMarker = "supersecret-token-abc123";
    private const string OtherSecret = "another-extremely-private-value";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _dbFactory;
    private readonly ApiKeyRepository _keys;
    private readonly NoteRepository _notes;
    private readonly string _dataDir;
    private const string UserId = "secret_strip_user";

    public SecretStripInvariantTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_secret_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        _dbFactory = new DatabaseFactory(_dataDir);
        _keys = new ApiKeyRepository(_dbFactory);
        _notes = new NoteRepository(_dbFactory, new TagRepository(_dbFactory));

        using (var db = _dbFactory.CreateSystemConnection())
        {
            db.Execute(
                "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'U', 'u@u', @now)",
                new { id = UserId, now = DateTime.UtcNow.ToString("o") });
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

    private async Task<(HttpClient Client, string NoteId)> SetupClientWithSecretNoteAsync()
    {
        // Seed a note whose content holds a secret marker. Going straight
        // through the repository with NoteSource.Mcp — that's the same
        // pathway remember() uses, so we exercise the "incoming secret"
        // scenario and the "outgoing strip" scenario in one shot.
        var id = await _notes.CreateAsync(
            new ContextRef(ContextType.User, UserId), UserId,
            new Note
            {
                Title = "note-with-secret",
                Content = $"Public preamble\n::secret\n{SecretMarker}\n::end\nPublic tail",
                ContentSecret = Encoding.UTF8.GetBytes(OtherSecret),
            },
            NoteSource.Mcp,
            TestContext.Current.CancellationToken);

        var issued = await _keys.IssueAsync(UserId, ContextRef.User(UserId), "secret-probe",
            new[] { "read:notes", "write:notes" }, TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", issued.RawToken);
        return (client, id);
    }

    private static void AssertNoSecretLeak(string responseBody)
    {
        Assert.DoesNotContain(SecretMarker, responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OtherSecret, responseBody, StringComparison.OrdinalIgnoreCase);
        // content_secret lives on the row but must never cross the wire.
        // Both JSON casings are checked because System.Text.Json defaults
        // to camelCase but attribute-annotated shapes may override it.
        Assert.DoesNotContain("contentSecret", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content_secret", responseBody, StringComparison.OrdinalIgnoreCase);
    }

    private static StringContent RpcBody(string tool, object args)
        => new(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments = args },
        }), Encoding.UTF8, "application/json");

    [Fact]
    public async Task SearchMemory_DoesNotLeakSecret()
    {
        var (client, _) = await SetupClientWithSecretNoteAsync();
        var resp = await client.PostAsync("/mcp",
            RpcBody("search_memory", new { query = "note-with-secret" }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        AssertNoSecretLeak(body);
    }

    [Fact]
    public async Task GetMemory_DoesNotLeakSecret()
    {
        var (client, id) = await SetupClientWithSecretNoteAsync();
        var resp = await client.PostAsync("/mcp",
            RpcBody("get_memory", new { id }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        AssertNoSecretLeak(body);
    }

    [Fact]
    public async Task ListPending_DoesNotLeakSecret()
    {
        var (client, _) = await SetupClientWithSecretNoteAsync();
        // Mcp-sourced write auto-tags review:pending, so the seeded note
        // should land in this result set.
        var resp = await client.PostAsync("/mcp",
            RpcBody("list_pending", new { }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        AssertNoSecretLeak(body);
    }

    [Fact]
    public async Task UpdateMemory_EchoDoesNotLeakSecret()
    {
        var (client, id) = await SetupClientWithSecretNoteAsync();
        // The update preserves the existing content (we only send a title
        // change). The tool re-reads the note and returns it; that echo
        // path must still strip.
        var resp = await client.PostAsync("/mcp",
            RpcBody("update_memory", new { id, title = "renamed" }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        AssertNoSecretLeak(body);
    }

    [Fact]
    public async Task StrippedResponse_StillContainsPublicContent()
    {
        // Positive control: the strip replaces the secret block, but the
        // public text either side must survive. Catches an over-eager
        // stripper that blanks the whole content field.
        var (client, id) = await SetupClientWithSecretNoteAsync();
        var resp = await client.PostAsync("/mcp",
            RpcBody("get_memory", new { id }),
            TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Public preamble", body);
        Assert.Contains("Public tail", body);
        Assert.Contains("[secret content hidden]", body);
    }

    [Fact]
    public async Task SeededSecret_ActuallyLandsInTheRawDatabase()
    {
        // Sanity check the test setup — if the secret never hit disk in
        // the first place, the strip tests above are vacuous. Read the
        // note directly from the context DB and assert the marker exists.
        await SetupClientWithSecretNoteAsync();
        using var db = _dbFactory.CreateContextConnection(new ContextRef(ContextType.User, UserId));
        var rawContent = await db.QuerySingleAsync<string>(
            "SELECT content FROM notes WHERE title = 'note-with-secret'");
        Assert.Contains(SecretMarker, rawContent);
    }

    [Fact]
    public async Task MarkerForm_AlsoStripped()
    {
        // Phase 3 encrypted secrets leave `::secret#N::end` markers in the
        // public content. They carry no plaintext themselves, but leaking
        // them reveals how many secrets a note has and which indices
        // exist — strip to the same placeholder.
        await _notes.CreateAsync(
            new ContextRef(ContextType.User, UserId), UserId,
            new Note
            {
                Title = "note-with-marker",
                Content = "Prefix.\n::secret#0::end\nMiddle.\n::secret#1::end\nSuffix.",
                ContentSecret = Encoding.UTF8.GetBytes("{\"v\":1,\"blocks\":[\"opaque\",\"opaque\"]}"),
            },
            NoteSource.Mcp,
            TestContext.Current.CancellationToken);

        var issued = await _keys.IssueAsync(UserId, ContextRef.User(UserId), "marker-probe",
            new[] { "read:notes" }, TestContext.Current.CancellationToken);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", issued.RawToken);

        var resp = await client.PostAsync("/mcp",
            RpcBody("search_memory", new { query = "note-with-marker" }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("::secret#0::end", body);
        Assert.DoesNotContain("::secret#1::end", body);
        Assert.DoesNotContain("contentSecret", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Prefix.", body);
        Assert.Contains("Suffix.", body);
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
