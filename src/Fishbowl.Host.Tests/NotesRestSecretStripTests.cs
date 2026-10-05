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

// The REST notes routes hold the same line as the MCP wire: a key (MCP
// client, OAuth connector, script) reads notes with their `:::secret` blocks
// replaced and without `contentSecret` — personal and space alike. A space
// holds no secrets since they are refused there, but a note from before may.
public class NotesRestSecretStripTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string UserId = "rest_strip_user";
    private const string Secret = "rest-plaintext-secret-42";
    private const string Cipher = "rest-cipher-payload-77";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly ApiKeyRepository _keys;
    private readonly NoteRepository _notes;
    private readonly string _dataDir;

    public NotesRestSecretStripTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_rest_strip_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _db = new DatabaseFactory(_dataDir);
        _keys = new ApiKeyRepository(_db);
        _notes = new NoteRepository(_db, new TagRepository(_db));
        using (var sys = _db.CreateSystemConnection())
            sys.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'U', 'u@u', @now)",
                new { id = UserId, now = DateTime.UtcNow.ToString("o") });

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton<DatabaseFactory>(_db);
            });
        });
    }

    private static byte[] Envelope()
    {
        var block = Convert.ToBase64String([.. new byte[12], .. Encoding.UTF8.GetBytes(Cipher), .. new byte[16]]);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v = 2, blocks = new[] { block } }));
    }

    private HttpClient Client(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static void AssertStripped(string body)
    {
        Assert.DoesNotContain(Secret, body);
        // The envelope never goes out (the signed-in UI is the one reader of it).
        Assert.DoesNotContain("contentSecret\":\"", body);
        Assert.Contains("[secret content hidden]", body);
    }

    [Fact]
    public async Task PersonalKey_ReadsNotesStripped()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await _notes.CreateAsync(ContextRef.User(UserId), UserId, new Note
        {
            Title = "with-secret",
            Content = $"intro\n:::secret\n{Secret}\n:::end\n:::secret#0:::end",
            ContentSecret = Envelope(),
        }, ct);
        var key = await _keys.IssueAsync(UserId, ContextRef.User(UserId), "reader", new[] { "read:notes" }, ct);
        var client = Client(key.RawToken);

        AssertStripped(await client.GetStringAsync("/api/v1/notes", ct));
        AssertStripped(await client.GetStringAsync($"/api/v1/notes/{id}", ct));
    }

    [Fact]
    public async Task SpaceKey_ReadsLegacySecretStripped_AndCantWriteOverIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = await new SpaceRepository(_db).CreateAsync(UserId, "Strip Club", ct);
        // Written before spaces refused secrets: straight into the space DB.
        const string id = "01LEGACYSECRETNOTE0000000";
        using (var db = _db.CreateContextConnection(ContextRef.Space(space.Id)))
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute("INSERT INTO notes (id, title, content, type, tags, created_by, created_at, updated_at) " +
                       "VALUES (@id, 'legacy', @content, 'note', '[]', @u, @now, @now)",
                new { id, content = $"x\n:::secret\n{Secret}\n:::end", u = UserId, now });
        }
        var key = await _keys.IssueAsync(UserId, ContextRef.Space(space.Slug), "space-rw",
            new[] { "read:notes", "write:notes" }, ct);
        var client = Client(key.RawToken);

        AssertStripped(await client.GetStringAsync($"/api/v1/spaces/{space.Slug}/notes", ct));
        AssertStripped(await client.GetStringAsync($"/api/v1/spaces/{space.Slug}/notes/{id}", ct));
        AssertStripped(await client.GetStringAsync("/api/v1/notes", ct));

        var put = await client.PutAsJsonAsync($"/api/v1/spaces/{space.Slug}/notes/{id}",
            new { title = "legacy", content = "x\n[secret content hidden]" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Contains("secret_note", await put.Content.ReadAsStringAsync(ct));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}
