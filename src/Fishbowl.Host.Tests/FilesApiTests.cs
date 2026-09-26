using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Files;
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

// /api/v1/files over HTTP (spec § Testing — Host): upload conditions and
// limits, Range/ETag, the inline allow-list and its headers, Bearer scopes
// and cookie-only routes, space roles, transfer, feed and snapshot.
public class FilesApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string Alice = "files_alice";
    private const string Bob = "files_bob";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FilesApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_files_api_" + Path.GetRandomFileName());
        _db = new DatabaseFactory(_dataDir);
        using (var sys = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            sys.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[] { new { id = Alice, n = "A", e = "a@a", now }, new { id = Bob, n = "B", e = "b@b", now } });
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
                // Test user by header; a Bearer token still goes to the real
                // ApiKey handler, so scopes and bindings are the real ones.
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

    private HttpClient As(string user)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, user);
        return c;
    }

    private HttpClient WithToken(string token)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static HttpRequestMessage Upload(string url, byte[] body, string? ifMatch = null, bool create = true, bool header = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (header) req.Headers.Add("X-Fishbowl-Upload", "1");
        if (create) req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return req;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private async Task<HttpResponseMessage> Put(HttpClient c, string path, string text, string prefix = "/api/v1/files")
        => await c.SendAsync(Upload($"{prefix}/content?path={Uri.EscapeDataString(path)}", Encoding.UTF8.GetBytes(text)), Ct);

    [Fact]
    public async Task Upload_Conditions_Header_AndLimits()
    {
        var c = As(Alice);
        var created = await Put(c, "a.txt", "hello");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var etag = created.Headers.ETag!.Tag;

        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Upload("/api/v1/files/content?path=b.txt", "x"u8.ToArray(), header: false), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await c.SendAsync(Upload("/api/v1/files/content?path=b.txt", "x"u8.ToArray(), create: false), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Put(c, "a.txt", "again")).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await c.SendAsync(Upload("/api/v1/files/content?path=a.txt", "x"u8.ToArray(), "\"1-2\"", create: false), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Upload("/api/v1/files/content?path=a.txt", "new!"u8.ToArray(), etag, create: false), Ct)).StatusCode);

        var bad = await Put(c, "CON", "x");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            var body = await Json(bad);
            Assert.Equal("invalid_name", body.GetProperty("error").GetString());
            Assert.Equal("reserved_device", body.GetProperty("rule").GetString());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(c, "../escape.txt", "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/files/stat?path=%2e%2e%2fsystem.db", Ct)).StatusCode);

        await new SystemRepository(_db).SetConfigAsync(FileLimits.MaxFileBytesKey, "8", Ct);
        var tooBig = await Put(c, "big.bin", "0123456789");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.Equal("size", (await Json(tooBig)).GetProperty("field").GetString());
    }

    [Fact]
    public async Task Content_Range_ETag_AndInlineRules()
    {
        var c = As(Alice);
        await Put(c, "song.txt", "0123456789abcdef");
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/files/content?path=song.txt");
        req.Headers.Range = new RangeHeaderValue(4, 7);
        var partial = await c.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("4567", await partial.Content.ReadAsStringAsync(Ct));
        Assert.Equal("bytes 4-7/16", partial.Content.Headers.ContentRange!.ToString());

        var full = await c.GetAsync("/api/v1/files/content?path=song.txt", Ct);
        var again = new HttpRequestMessage(HttpMethod.Get, "/api/v1/files/content?path=song.txt");
        again.Headers.IfNoneMatch.Add(full.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(again, Ct)).StatusCode);

        // Default: attachment + nosniff + sandbox CSP.
        Assert.Equal("attachment", full.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", full.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", full.Headers.GetValues("Content-Security-Policy").Single());

        // Plain text inline: as text/plain.
        var inlineText = await c.GetAsync("/api/v1/files/content?path=song.txt&inline=1", Ct);
        Assert.Equal("inline", inlineText.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("text/plain", inlineText.Content.Headers.ContentType!.MediaType);

        // HTML — also when it pretends to be a PDF or a PNG — never inline.
        foreach (var name in new[] { "page.html", "fake.pdf", "fake.png" })
        {
            await Put(c, name, "<!doctype html><script>alert(1)</script>");
            var r = await c.GetAsync($"/api/v1/files/content?path={name}&inline=1", Ct);
            Assert.Equal("attachment", r.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("application/octet-stream", r.Content.Headers.ContentType!.MediaType);
            Assert.Contains("sandbox", r.Headers.GetValues("Content-Security-Policy").Single());
        }

        // A real PDF opens inline in its own tab — its own CSP, no sandbox.
        await Put(c, "real.pdf", "%PDF-1.7\n%âãÏÓ\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF");
        var pdf = await c.GetAsync("/api/v1/files/content?path=real.pdf&inline=1", Ct);
        Assert.Equal("inline", pdf.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", pdf.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.DoesNotContain("sandbox", pdf.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Folders_Move_Copy_Trash_Restore_Usage()
    {
        var c = As(Alice);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/v1/files/folders", new { path = "docs" }, Ct)).StatusCode);
        await Put(c, "docs/x.txt", "x");
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/files/move", new { from = "docs/x.txt", to = "docs/y.txt" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/v1/files/copy", new { from = "docs", to = "docs2" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/v1/files/folders", new { path = "docs" }, Ct)).StatusCode);

        var list = await Json(await c.GetAsync("/api/v1/files/list", Ct));
        Assert.Equal(new[] { "docs", "docs2" }, list.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()));

        var trashed = await Json(await c.DeleteAsync("/api/v1/files?path=docs/y.txt", Ct));
        var id = trashed.GetProperty("id").GetString();
        Assert.Single((await Json(await c.GetAsync("/api/v1/files/trash", Ct))).EnumerateArray());
        var emptyBody = await c.PostAsync($"/api/v1/files/trash/{id}/restore", null, Ct);
        Assert.True(emptyBody.StatusCode == HttpStatusCode.OK, $"{emptyBody.StatusCode}: {await emptyBody.Content.ReadAsStringAsync(Ct)}");   // body optional
        var again = (await Json(await c.DeleteAsync("/api/v1/files?path=docs/y.txt", Ct))).GetProperty("id").GetString();
        await Put(c, "docs/y.txt", "clash");
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/v1/files/trash/{again}/restore", new { onConflict = "fail" }, Ct)).StatusCode);
        var renamed = await Json(await c.PostAsJsonAsync($"/api/v1/files/trash/{again}/restore", new { onConflict = "rename" }, Ct));
        Assert.Equal("y (restored).txt", renamed.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/v1/files?path=docs2&permanent=1", Ct)).StatusCode);

        var usage = await Json(await c.GetAsync("/api/v1/files/usage", Ct));
        Assert.Equal(5 + 1, usage.GetProperty("bytes").GetInt64());   // the clash + y (restored).txt; docs2 is gone
    }

    [Fact]
    public async Task Bearer_ScopesAndCookieOnlyRoutes()
    {
        await Put(As(Alice), "note.txt", "x");
        var keys = new ApiKeyRepository(_db);
        var none = (await keys.IssueAsync(Alice, ContextRef.User(Alice), "no-files", new[] { "read:notes" }, Ct)).RawToken;
        var read = (await keys.IssueAsync(Alice, ContextRef.User(Alice), "read", new[] { "read:files" }, Ct)).RawToken;
        var rw = (await keys.IssueAsync(Alice, ContextRef.User(Alice), "rw", new[] { "read:files", "write:files" }, Ct)).RawToken;

        Assert.Equal(HttpStatusCode.Forbidden, (await WithToken(none).GetAsync("/api/v1/files/list", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WithToken(read).GetAsync("/api/v1/files/list", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(WithToken(read), "nope.txt", "x")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Put(WithToken(rw), "yes.txt", "x")).StatusCode);

        var sync = WithToken(rw);
        Assert.Equal(HttpStatusCode.OK, (await sync.DeleteAsync("/api/v1/files?path=yes.txt", Ct)).StatusCode);   // trash is fine
        Assert.Equal(HttpStatusCode.Forbidden, (await sync.DeleteAsync("/api/v1/files?path=note.txt&permanent=1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sync.DeleteAsync("/api/v1/files/trash", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sync.PostAsJsonAsync("/api/v1/files/transfer",
            new { op = "copy", from = new { workspace = "personal", paths = new[] { "note.txt" } }, to = new { workspace = "personal", folder = "" } }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Spaces_ReadonlyAndTokenBinding_AndTransfer()
    {
        var space = await new SpaceRepository(_db).CreateAsync(Alice, "Shared Files", Ct);
        using (var sys = _db.CreateSystemConnection())
            sys.Execute("INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@s, @u, 'readonly', @now)",
                new { s = space.Id, u = Bob, now = DateTime.UtcNow.ToString("o") });
        var prefix = $"/api/v1/spaces/{space.Slug}/files";

        Assert.Equal(HttpStatusCode.Created, (await Put(As(Alice), "team.txt", "t", prefix)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(Bob).GetAsync($"{prefix}/list", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(As(Bob), "bob.txt", "b", prefix)).StatusCode);

        // A personal token on a space URL is refused, even for a member.
        var personal = (await new ApiKeyRepository(_db).IssueAsync(Alice, ContextRef.User(Alice), "p", new[] { "read:files" }, Ct)).RawToken;
        Assert.Equal(HttpStatusCode.Forbidden, (await WithToken(personal).GetAsync($"{prefix}/list", Ct)).StatusCode);
        var bound = (await new ApiKeyRepository(_db).IssueAsync(Alice, ContextRef.Space(space.Slug), "s", new[] { "read:files" }, Ct)).RawToken;
        Assert.Equal(HttpStatusCode.OK, (await WithToken(bound).GetAsync($"{prefix}/list", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await WithToken(bound).GetAsync("/api/v1/files/list", Ct)).StatusCode);

        // Transfer: personal → space; a readonly target is refused.
        await Put(As(Alice), "mine.txt", "m");
        var ok = await As(Alice).PostAsJsonAsync("/api/v1/files/transfer", new
        {
            op = "move",
            from = new { workspace = "personal", paths = new[] { "mine.txt" } },
            to = new { workspace = $"space:{space.Slug}", folder = "" },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var names = (await Json(await As(Alice).GetAsync($"{prefix}/list", Ct))).GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("mine.txt", names);

        await Put(As(Bob), "bobs.txt", "b");
        var refused = await As(Bob).PostAsJsonAsync("/api/v1/files/transfer", new
        {
            op = "copy",
            from = new { workspace = "personal", paths = new[] { "bobs.txt" } },
            to = new { workspace = $"space:{space.Slug}", folder = "" },
        }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Changes_And_Snapshot()
    {
        var c = As(Alice);
        await Put(c, "one.txt", "1");
        var head = await Json(await c.GetAsync("/api/v1/files/changes", Ct));
        var cursor = head.GetProperty("cursor").GetString()!;
        await Put(c, "two.txt", "2");

        var page = await Json(await c.GetAsync($"/api/v1/files/changes?cursor={Uri.EscapeDataString(cursor)}", Ct));
        Assert.Equal("two.txt", page.GetProperty("changes").EnumerateArray().Single().GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.Gone, (await c.GetAsync("/api/v1/files/changes?cursor=nope:1", Ct)).StatusCode);

        var snap = await c.GetAsync("/api/v1/files/snapshot", Ct);
        Assert.Equal("application/x-ndjson", snap.Content.Headers.ContentType!.MediaType);
        var lines = (await snap.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "one.txt", "two.txt" }, lines[..^1].Select(l => JsonDocument.Parse(l).RootElement.GetProperty("path").GetString()));
        Assert.True(JsonDocument.Parse(lines[^1]).RootElement.TryGetProperty("cursor", out _));
    }
}
