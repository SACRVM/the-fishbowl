using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// What MCP answers with InvalidParams is written for the caller: the table
// layer's refusals, the tools' own argument errors, a plain sentence for an
// argument of the wrong JSON type — never a .NET exception's wording.
public class McpRefusalMappingTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string UserId = "mcp_refusal_user";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public McpRefusalMappingTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_mcp_refusal_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _db = new DatabaseFactory(_dataDir);
        using (var db = _db.CreateSystemConnection())
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'U', 'u@example.com', @now)",
                new { id = UserId, now = DateTime.UtcNow.ToString("o") });
        var testFactory = _db;
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton<DatabaseFactory>(testFactory);
            });
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private async Task<JsonElement> CallAsync(string body)
    {
        var issued = await new ApiKeyRepository(_db).IssueAsync(UserId, ContextRef.User(UserId), "refusals",
            new[] { "read:notes", "write:notes", "read:tables" }, Ct);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.RawToken);
        var resp = await client.PostAsync("/mcp", new StringContent(body, Encoding.UTF8, "application/json"), Ct);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error");
    }

    private static string Call(string tool, string? arguments) =>
        $$"""{ "jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": { "name": "{{tool}}"{{(arguments is null ? "" : ", \"arguments\": " + arguments)}} } }""";

    [Fact]
    public async Task AnArgumentOfTheWrongJsonType_IsInvalidParams_InPlainWords()
    {
        var error = await CallAsync(Call("get_memory", """{ "id": 5 }"""));
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Equal("An argument has the wrong type — see the tool's inputSchema.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task MissingArguments_ReachTheToolsOwnCheck()
    {
        var error = await CallAsync(Call("remember", null));
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Equal("title is required (Parameter 'arguments')", error.GetProperty("message").GetString());

        var notAnObject = await CallAsync(Call("remember", "\"title\""));
        Assert.Equal(-32602, notAnObject.GetProperty("code").GetInt32());
        Assert.Equal("`arguments` must be an object.", notAnObject.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ATableRefusal_IsInvalidParams_WithItsMessage()
    {
        var error = await CallAsync(Call("table_list", "{}"));
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.StartsWith("Tables live in spaces", error.GetProperty("message").GetString());
    }
}
