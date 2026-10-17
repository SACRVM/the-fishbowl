using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// The workspace's files over MCP (file_list, file_read, file_write,
// file_move, file_delete, folder_create): the same store and the same rules
// as the REST Files API — a key reaches its own workspace, a Reader changes
// nothing, app code needs a Designer with design:apps — and a space's house
// rules (.apps/<app>/house-rules.md) first in space_guide.
public class McpFileToolsTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string Alice = "mcpfiles_alice";   // the space's owner
    private const string Bob = "mcpfiles_bob";       // a Member
    private const string Carol = "mcpfiles_carol";   // a Reader
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public McpFileToolsTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_mcp_files_" + Path.GetRandomFileName());
        _db = new DatabaseFactory(_dataDir);
        using (var sys = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            sys.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[]
                {
                    new { id = Alice, n = "A", e = "a@a", now },
                    new { id = Bob, n = "B", e = "b@b", now },
                    new { id = Carol, n = "C", e = "c@c", now },
                });
        }
        new SystemRepository(_db).SetConfigAsync(FileLimits.MinFreeBytesKey, "0").GetAwaiter().GetResult();

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton(_db);
                services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    o.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    o.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.AuthenticationScheme, o =>
                {
                    o.ForwardDefaultSelector = ctx =>
                        ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal) ? "ApiKey" : null;
                });
            });
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<Fishbowl.Core.Models.Space> SpaceAsync(string name)
    {
        var space = await new SpaceRepository(_db).CreateAsync(Alice, name, Ct);
        using var sys = _db.CreateSystemConnection();
        var now = DateTime.UtcNow.ToString("o");
        sys.Execute("INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@s, @u, @r, @now)",
            new[] { new { s = space.Id, u = Bob, r = "member", now }, new { s = space.Id, u = Carol, r = "reader", now } });
        return space;
    }

    private async Task<HttpClient> KeyAsync(string user, ContextRef ctx, params string[] scopes)
    {
        var token = (await new ApiKeyRepository(_db).IssueAsync(user, ctx, "agent", scopes, Ct)).RawToken;
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        c.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        return c;
    }

    private static async Task<JsonElement> CallAsync(HttpClient c, string tool, object arguments)
    {
        var resp = await c.PostAsync("/mcp", new StringContent(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments },
        }), Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    // The tool's answer, or a failure naming the error it gave.
    private static async Task<JsonElement> OkAsync(HttpClient c, string tool, object arguments)
    {
        var r = await CallAsync(c, tool, arguments);
        Assert.False(r.TryGetProperty("error", out var e), $"{tool}: {e}");
        return JsonDocument.Parse(r.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
    }

    // A refusal: InvalidParams, its message.
    private static async Task<string> RefusedAsync(HttpClient c, string tool, object arguments)
    {
        var r = await CallAsync(c, tool, arguments);
        Assert.True(r.TryGetProperty("error", out var e), $"{tool} should have been refused: {r}");
        Assert.Equal(-32602, e.GetProperty("code").GetInt32());
        return e.GetProperty("message").GetString()!;
    }

    [Fact]
    public async Task FileTools_WriteListReadMoveDelete_InTheKeysSpace()
    {
        var space = await SpaceAsync("Finance Files");
        var mcp = await KeyAsync(Alice, ContextRef.Space(space.Slug), "read:files", "write:files");

        // Text, with its folders made on the way; the same path again only with overwrite.
        var created = await OkAsync(mcp, "file_write", new { path = "Belege/2026/notiz.txt", text = "Monitor, 2 Jahre" });
        Assert.True(created.GetProperty("created").GetBoolean());
        Assert.Contains("name_exists", await RefusedAsync(mcp, "file_write", new { path = "Belege/2026/notiz.txt", text = "again" }));
        var replaced = await OkAsync(mcp, "file_write", new { path = "Belege/2026/notiz.txt", text = "Monitor, 3 Jahre", overwrite = true });
        Assert.False(replaced.GetProperty("created").GetBoolean());
        var read = await OkAsync(mcp, "file_read", new { path = "Belege/2026/notiz.txt" });
        Assert.Equal("Monitor, 3 Jahre", read.GetProperty("text").GetString());
        Assert.False(read.GetProperty("truncated").GetBoolean());

        // Bytes go in and come back as base64; the type is what they are.
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% a statement\n");
        await OkAsync(mcp, "file_write", new { path = "Auszüge/Giro/2026/03.pdf", base64 = Convert.ToBase64String(pdf) });
        var bytes = await OkAsync(mcp, "file_read", new { path = "Auszüge/Giro/2026/03.pdf" });
        Assert.Equal("application/pdf", bytes.GetProperty("mime").GetString());
        Assert.Equal(pdf, Convert.FromBase64String(bytes.GetProperty("base64").GetString()!));
        Assert.Contains("one of the two", await RefusedAsync(mcp, "file_write", new { path = "x.txt", text = "a", base64 = "YQ==" }));

        // A folder, a listing, a rename, a trip to the trash.
        Assert.Equal("folder", (await OkAsync(mcp, "folder_create", new { path = "Auszüge/Kreditkarte/2026" })).GetProperty("kind").GetString());
        var list = await OkAsync(mcp, "file_list", new { path = "Belege/2026" });
        Assert.Equal(["notiz.txt"], list.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()));
        await OkAsync(mcp, "file_move", new { from = "Belege/2026/notiz.txt", to = "Belege/2026/monitor.txt" });
        var gone = await OkAsync(mcp, "file_delete", new { path = "Belege/2026/monitor.txt" });
        Assert.False(string.IsNullOrEmpty(gone.GetProperty("trashId").GetString()));
        Assert.Contains("not_found", await RefusedAsync(mcp, "file_read", new { path = "Belege/2026/monitor.txt" }));

        // The space's own files — what the Files app shows.
        var stat = await _factory.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/spaces/{space.Slug}/files/stat?path=Auszüge/Giro/2026/03.pdf")
        { Headers = { { TestAuthHandler.UserIdHeader, Alice } } }, Ct);
        Assert.Equal(HttpStatusCode.OK, stat.StatusCode);
    }

    [Fact]
    public async Task FileTools_AppCode_NeedsADesignerWithDesignApps_AndReadersWriteNothing()
    {
        var space = await SpaceAsync("Finance Apps");
        var ctx = ContextRef.Space(space.Slug);

        var owner = await KeyAsync(Alice, ctx, "read:files", "write:files");
        Assert.Contains("apps_design_only", await RefusedAsync(owner, "file_write", new { path = ".apps/finance/house-rules.md", text = "x" }));
        var designer = await KeyAsync(Alice, ctx, "read:files", "write:files", "design:apps");
        await OkAsync(designer, "file_write", new { path = ".apps/finance/house-rules.md", text = "x" });
        Assert.Contains("apps_folder_fixed", await RefusedAsync(designer, "file_delete", new { path = ".apps" }));
        Assert.Contains("apps_design_only", await RefusedAsync(owner, "file_move", new { from = ".apps/finance/house-rules.md", to = "rules.md" }));

        // A Member's key loses design:apps; a Reader's loses write:files.
        var member = await KeyAsync(Bob, ctx, "read:files", "write:files", "design:apps");
        Assert.Contains("apps_design_only", await RefusedAsync(member, "file_write", new { path = ".apps/finance/app.js", text = "x" }));
        await OkAsync(member, "file_write", new { path = "Belege/beleg.txt", text = "x" });
        var reader = await KeyAsync(Carol, ctx, "read:files", "write:files");
        Assert.Contains("Scope denied", await RefusedAsync(reader, "file_write", new { path = "Belege/reader.txt", text = "x" }));
        Assert.Equal("x", (await OkAsync(reader, "file_read", new { path = "Belege/beleg.txt" })).GetProperty("text").GetString());
    }

    [Fact]
    public async Task FileTools_APersonalKey_WorksInThePersonalFiles_WhichHaveNoAppCode()
    {
        var mcp = await KeyAsync(Alice, ContextRef.User(Alice), "read:files", "write:files", "design:apps");
        await OkAsync(mcp, "file_write", new { path = "Rechnungen/pc.txt", text = "PC" });
        Assert.Equal("PC", (await OkAsync(mcp, "file_read", new { path = "Rechnungen/pc.txt" })).GetProperty("text").GetString());
        Assert.Contains("apps_space_only", await RefusedAsync(mcp, "file_write", new { path = ".apps/x/app.js", text = "x" }));
    }

    [Fact]
    public async Task SpaceGuide_PutsTheHouseRulesFirst()
    {
        var space = await SpaceAsync("Finance Rules");
        var ctx = ContextRef.Space(space.Slug);
        var designer = await KeyAsync(Alice, ctx, "read:files", "write:files", "design:apps", "read:tables");
        await OkAsync(designer, "file_write", new
        {
            path = ".apps/finance/house-rules.md",
            text = "# Import\n- Store the statement under `Auszüge/` first.\n```md\n# not a heading\n```\n",
        });

        var md = (await OkAsync(designer, "space_guide", new { })).GetProperty("markdown").GetString()!;
        var rules = md.IndexOf("## House rules", StringComparison.Ordinal);
        Assert.True(rules > 0 && rules < md.IndexOf("## How a space app works", StringComparison.Ordinal), md);
        Assert.Contains("### `.apps/finance/house-rules.md`", md);
        Assert.Contains("#### Import", md);
        Assert.Contains("Store the statement under `Auszüge/` first.", md);
        Assert.Contains("\n# not a heading\n", md);          // a code fence stays as written
        Assert.Contains("`file_write`", md);                  // how to write app code over MCP

        // Without a rules file there is no section.
        var other = await SpaceAsync("No Rules");
        var plain = await KeyAsync(Alice, ContextRef.Space(other.Slug), "read:tables");
        Assert.DoesNotContain("## House rules", (await OkAsync(plain, "space_guide", new { })).GetProperty("markdown").GetString()!);
    }
}
