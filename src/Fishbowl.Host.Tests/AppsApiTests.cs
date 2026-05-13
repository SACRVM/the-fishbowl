using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Api.Endpoints;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Fishbowl.Host.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// End-to-end smoke for the Apps platform: cookie-auth lifecycle (create app +
// mint key) ⇒ Bearer-auth MCP round-trip (create_table → insert → query →
// soft-delete → restore). Plus cross-context checks: an app key cannot hit
// search_memory (no read:notes), and cannot reach another app's data.
public class AppsApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _dbFactory;
    private readonly ApiKeyRepository _keys;
    private readonly string _dataDir;
    private const string Alice = "apps_alice";

    public AppsApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_apps_e2e_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        _dbFactory = new DatabaseFactory(_dataDir);
        _keys = new ApiKeyRepository(_dbFactory);
        using (var db = _dbFactory.CreateSystemConnection())
        {
            db.Execute(
                "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'Alice', 'a@a', @now)",
                new { id = Alice, now = DateTime.UtcNow.ToString("o") });
        }

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton<DatabaseFactory>(_dbFactory);

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.AuthenticationScheme, options =>
                    {
                        // Bearer requests skip the test cookie and land on the
                        // real ApiKey scheme so scope/context claims wire end-to-end.
                        options.ForwardDefaultSelector = ctx =>
                        {
                            var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
                            if (auth is not null && auth.StartsWith("Bearer fb_", StringComparison.Ordinal))
                                return ApiKeyAuthenticationOptions.DefaultScheme;
                            return null;
                        };
                    });
            });
        });
    }

    private HttpRequestMessage Req(HttpMethod method, string path, string userId, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private async Task<(AppsApi.AppSummary App, string Bearer)> CreateAppAndMintKeyAsync(
        string ownerType, string ownerId, string slug, IEnumerable<string>? scopes = null)
    {
        var client = _factory.CreateClient();

        var createResp = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/apps", Alice, new
        {
            slug,
            name = slug,
            owner = new { type = ownerType, id = ownerId },
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var app = await createResp.Content.ReadFromJsonAsync<AppsApi.AppSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(app);

        var keyResp = await client.SendAsync(Req(HttpMethod.Post,
            $"/api/v1/apps/{ownerType}/{ownerId}/{slug}/keys", Alice, new
            {
                name = $"key-{slug}",
                scopes = scopes?.ToArray() ?? new[] { ScopeCatalog.AppRead, ScopeCatalog.AppWrite, ScopeCatalog.AppAdmin },
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, keyResp.StatusCode);
        var key = await keyResp.Content.ReadFromJsonAsync<AppsApi.CreateAppKeyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(key);

        return (app!, key!.RawToken);
    }

    private HttpClient BearerClient(string token)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static StringContent Json(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ToolsCallAsync(HttpClient client, int id, string name, object args)
    {
        var resp = await client.PostAsync("/mcp", Json(new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name, arguments = args },
        }), TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);
        return doc.RootElement.Clone();
    }

    private static JsonElement UnwrapToolResult(JsonElement rpc)
    {
        // tools/call envelope wraps the actual tool output as a JSON string in
        // content[0].text. Unwrap so callers can assert on the structured result.
        if (rpc.TryGetProperty("error", out var err))
            throw new InvalidOperationException("MCP error: " + err.ToString());
        var text = rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        return JsonDocument.Parse(text!).RootElement.Clone();
    }

    [Fact]
    public async Task CreateApp_PersonalOwner_RoundTrip()
    {
        var (app, _) = await CreateAppAndMintKeyAsync("user", Alice, "transactions");
        Assert.Equal("transactions", app.Slug);
        Assert.Equal("user", app.OwnerType);
        Assert.Equal(Alice, app.OwnerId);
    }

    [Fact]
    public async Task CreateApp_BearerForbidden()
    {
        var (_, bearer) = await CreateAppAndMintKeyAsync("user", Alice, "a-create-bearer");
        var bearerClient = BearerClient(bearer);
        var resp = await bearerClient.PostAsync("/api/v1/apps",
            JsonContent.Create(new { slug = "x", name = "x", owner = new { type = "user", id = Alice } }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task FullMcpRoundTrip_CreateTable_Insert_Query_SoftDelete_Restore()
    {
        var (_, bearer) = await CreateAppAndMintKeyAsync("user", Alice, "ledger");
        var client = BearerClient(bearer);

        // Create table
        var createRpc = await ToolsCallAsync(client, 1, "app_create_table", new
        {
            table = "tx",
            columns = new object[]
            {
                new { name = "amount", type = "real", nullable = false },
                new { name = "category", type = "text" },
            },
        });
        var createBody = UnwrapToolResult(createRpc);
        Assert.True(createBody.GetProperty("created").GetBoolean());

        // Insert row — title is required by the base-column contract.
        var insRpc = await ToolsCallAsync(client, 2, "app_insert", new
        {
            table = "tx",
            row = new { title = "Coffee", amount = 4.5, category = "food" },
        });
        var inserted = UnwrapToolResult(insRpc).GetProperty("row");
        Assert.Equal("Coffee", inserted.GetProperty("title").GetString());
        Assert.NotNull(inserted.GetProperty("created_at").GetString());
        Assert.NotNull(inserted.GetProperty("last_modified").GetString());
        Assert.Equal(1L, inserted.GetProperty("row_version").GetInt64());
        var rowId = inserted.GetProperty("id").GetString()!;
        Assert.Equal(26, rowId.Length);

        // Query
        var qRpc = await ToolsCallAsync(client, 3, "app_query", new
        {
            table = "tx",
            where = new Dictionary<string, object>
            {
                ["amount"] = new Dictionary<string, object> { ["$gt"] = 0 },
            },
        });
        var qBody = UnwrapToolResult(qRpc);
        Assert.Equal(1, qBody.GetProperty("count").GetInt32());

        // additional_data filter is rejected
        var badRpc = await ToolsCallAsync(client, 4, "app_query", new
        {
            table = "tx",
            where = new Dictionary<string, object>
            {
                ["additional_data"] = new Dictionary<string, object> { ["$eq"] = "x" },
            },
        });
        Assert.True(badRpc.TryGetProperty("error", out _),
            "DSL rejection should surface as a JSON-RPC error envelope, not a successful tools/call.");

        // Soft delete
        var delRpc = await ToolsCallAsync(client, 5, "app_delete", new { table = "tx", id = rowId });
        Assert.True(UnwrapToolResult(delRpc).GetProperty("deleted").GetBoolean());

        // Default query no longer sees it
        var afterDelRpc = await ToolsCallAsync(client, 6, "app_query", new { table = "tx" });
        Assert.Equal(0, UnwrapToolResult(afterDelRpc).GetProperty("count").GetInt32());

        // includeDeleted: true returns it
        var withDelRpc = await ToolsCallAsync(client, 7, "app_query", new
        {
            table = "tx",
            includeDeleted = true,
        });
        var withDel = UnwrapToolResult(withDelRpc);
        Assert.Equal(1, withDel.GetProperty("count").GetInt32());

        // Restore
        var restRpc = await ToolsCallAsync(client, 8, "app_restore", new { table = "tx", id = rowId });
        Assert.True(UnwrapToolResult(restRpc).GetProperty("restored").GetBoolean());
    }

    [Fact]
    public async Task AppKey_CannotReach_SearchMemory()
    {
        // App-scoped key carries only app:* scopes; calling a notes tool must 401/error.
        var (_, bearer) = await CreateAppAndMintKeyAsync("user", Alice, "cross-a");
        var client = BearerClient(bearer);
        var rpc = await ToolsCallAsync(client, 1, "search_memory", new { query = "anything" });
        Assert.True(rpc.TryGetProperty("error", out _),
            "search_memory requires read:notes; an app key should not authorise it.");
    }

    [Fact]
    public async Task AppKey_CannotReach_DifferentApp()
    {
        // App-A's bearer hits App-B's REST data route — must be 403 from
        // ResolveAppAsync (token AppId mismatch).
        var (_, aBearer) = await CreateAppAndMintKeyAsync("user", Alice, "app-a");
        var (_, _) = await CreateAppAndMintKeyAsync("user", Alice, "app-b");

        var client = BearerClient(aBearer);
        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/apps/user/{Alice}/app-b/tables"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task RestMirror_DataPlane_TableCrud()
    {
        var (_, bearer) = await CreateAppAndMintKeyAsync("user", Alice, "rest");
        var client = BearerClient(bearer);

        // Create a table via REST
        var createResp = await client.PostAsync($"/api/v1/apps/user/{Alice}/rest/tables",
            JsonContent.Create(new
            {
                table = "notes",
                columns = new object[]
                {
                    new { name = "body", type = "text" },
                },
            }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        // List tables
        var listResp = await client.GetAsync($"/api/v1/apps/user/{Alice}/rest/tables",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var listed = await listResp.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        var tables = listed.GetProperty("tables").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("notes", tables);

        // Insert a row
        var insertResp = await client.PostAsync(
            $"/api/v1/apps/user/{Alice}/rest/rows/notes",
            JsonContent.Create(new { title = "hello", body = "world" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, insertResp.StatusCode);
    }

    [Fact]
    public async Task DeleteApp_RemovesFolder()
    {
        var (app, _) = await CreateAppAndMintKeyAsync("user", Alice, "doomed");
        var client = _factory.CreateClient();

        var appFolder = Path.Combine(_dataDir, "users", Alice, "apps", app.Id);

        // Force creation of the app.db file via a write through the bearer.
        var (_, bearer) = await CreateAppAndMintKeyAsync("user", Alice, "doomed2");

        // Issue delete with ?confirm=<slug>.
        var req = Req(HttpMethod.Delete,
            $"/api/v1/apps/user/{Alice}/doomed?confirm=doomed", Alice, body: null);
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // Folder gone.
        SqliteConnection.ClearAllPools();
        Assert.False(Directory.Exists(appFolder),
            $"App folder should be deleted, still exists at {appFolder}");
    }

    // ── Task 2: Team-owner end-to-end MCP round-trip ────────────────────────

    [Fact]
    public async Task FullMcpRoundTrip_TeamOwner()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. Seed a team via TeamRepository (Alice added as owner by CreateAsync).
        var teams = new TeamRepository(_dbFactory);
        var team = await teams.CreateAsync(Alice, "ledger-team", ct);

        // 2. Create an app under that team via REST (cookie-authed as Alice).
        var (app, bearer) = await CreateAppAndMintKeyAsync(
            "team", team.Slug, "ledger",
            new[] { ScopeCatalog.AppRead, ScopeCatalog.AppWrite, ScopeCatalog.AppAdmin });

        Assert.Equal("ledger", app.Slug);
        Assert.Equal("team", app.OwnerType);
        Assert.Equal(team.Slug, app.OwnerId);

        // 3. App key was minted by CreateAppAndMintKeyAsync — use that bearer.
        var client = BearerClient(bearer);

        // 4a. Create table.
        var createRpc = await ToolsCallAsync(client, 1, "app_create_table", new
        {
            table = "tx",
            columns = new object[]
            {
                new { name = "amount", type = "real", nullable = false },
            },
        });
        var createBody = UnwrapToolResult(createRpc);
        Assert.True(createBody.GetProperty("created").GetBoolean());

        // 4b. Insert row.
        var insRpc = await ToolsCallAsync(client, 2, "app_insert", new
        {
            table = "tx",
            row = new { title = "Salary", amount = 1000.0 },
        });
        var inserted = UnwrapToolResult(insRpc).GetProperty("row");
        Assert.Equal("Salary", inserted.GetProperty("title").GetString());

        // 4c. Query — must find exactly one row.
        var qRpc = await ToolsCallAsync(client, 3, "app_query", new { table = "tx" });
        var qBody = UnwrapToolResult(qRpc);
        Assert.Equal(1, qBody.GetProperty("count").GetInt32());
    }

    // ── Task 3: App data must NOT leak into search_memory ───────────────────

    [Fact]
    public async Task AppData_NotVisible_To_SearchMemory_OnOwner()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. Create personal app for Alice and mint an app key.
        var (_, appBearer) = await CreateAppAndMintKeyAsync("user", Alice, "isolation");

        // 2. Via bearer, create a table and insert a distinctively-titled row.
        var appClient = BearerClient(appBearer);
        await ToolsCallAsync(appClient, 1, "app_create_table", new
        {
            table = "secrets",
            columns = Array.Empty<object>(),
        });
        var insRpc = await ToolsCallAsync(appClient, 2, "app_insert", new
        {
            table = "secrets",
            row = new { title = "fishbowl-isolation-marker-xyzzy" },
        });
        // Confirm the insert succeeded so we know the data exists in the app DB.
        Assert.False(insRpc.TryGetProperty("error", out _),
            "app_insert should succeed: " + insRpc.GetRawText());

        // 3. Mint a separate user-scoped key for Alice with read:notes scope.
        var issued = await _keys.IssueAsync(
            Alice, ContextRef.User(Alice), "notes-key",
            new[] { ScopeCatalog.ReadNotes }, ct);
        var notesClient = BearerClient(issued.RawToken);

        // 4. Call search_memory with the marker as query.
        var searchRpc = await ToolsCallAsync(notesClient, 1, "search_memory",
            new { query = "fishbowl-isolation-marker-xyzzy" });
        // Must succeed (no JSON-RPC error) — the key has read:notes scope.
        Assert.False(searchRpc.TryGetProperty("error", out _),
            "search_memory should not error with read:notes key: " + searchRpc.GetRawText());

        var text = searchRpc.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString();
        using var doc = JsonDocument.Parse(text!);
        var notes = doc.RootElement.GetProperty("notes");
        var titles = notes.EnumerateArray()
            .Select(n => n.GetProperty("title").GetString())
            .ToList();

        // The marker must NOT appear — app data is file-isolated from notes.
        Assert.DoesNotContain("fishbowl-isolation-marker-xyzzy", titles);
    }

    public void Dispose()
    {
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { /* swallow */ }
        }
    }
}
