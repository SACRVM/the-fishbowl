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
using Fishbowl.Host.Testing;
using Fishbowl.Mail.Sync;
using Fishbowl.Scheduler;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Fishbowl.Host.Tests;

// The mail HTTP API (both workspaces, the three access levels) and the MCP
// mail tools, against the in-memory sample mail server. Two hosts share one
// data dir: one with the test sign-in (cookie users), one with the real
// Bearer pipeline.
public class MailApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _cookie;
    private readonly WebApplicationFactory<Program> _bearer;
    private readonly DatabaseFactory _db;
    private readonly ApiKeyRepository _keys;
    private readonly string _dataDir;

    private const string Owner = "mail_owner";
    private const string Reader = "mail_reader";
    private const string Member = "mail_member";
    private const string Admin = "mail_admin";
    private const string Outsider = "mail_outsider";
    private const string Pw = SampleMailboxConnector.Password;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public MailApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_mail_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _db = new DatabaseFactory(_dataDir);
        _keys = new ApiKeyRepository(_db);
        using (var db = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[] { Owner, Reader, Member, Admin, Outsider }.Select(id => new { id, n = "Name " + id, e = id + "@example.com", now }));
        }
        var testDb = _db;
        void Common(IWebHostBuilder builder, bool testAuth)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                var d = services.SingleOrDefault(x => x.ServiceType == typeof(DatabaseFactory));
                if (d != null) services.Remove(d);
                services.AddSingleton<DatabaseFactory>(testDb);
                foreach (var c in services.Where(x => x.ServiceType == typeof(IMailboxConnector)).ToList()) services.Remove(c);
                services.AddSingleton<IMailboxConnector>(new SampleMailboxConnector(new ImapMailboxConnector()));
                if (testAuth)
                    services.AddAuthentication(o =>
                    {
                        o.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                        o.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                        o.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.AuthenticationScheme, _ => { });
            });
        }
        _cookie = factory.WithWebHostBuilder(b => Common(b, true));
        _bearer = factory.WithWebHostBuilder(b => Common(b, false));
        _ = SyncService; // stops the background loop before any test adds an account
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    // ---- helpers ----

    private HttpClient As(string user)
    {
        var c = _cookie.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        c.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, user);
        return c;
    }

    private async Task<HttpClient> KeyAsync(string userId, ContextRef ctx, params string[] scopes)
    {
        var issued = await _keys.IssueAsync(userId, ctx, "mail-test", scopes, Ct);
        var c = _bearer.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.RawToken);
        return c;
    }

    private HttpClient Fresh()
    {
        var id = "mail_u" + Guid.NewGuid().ToString("N")[..10];
        using (var db = _db.CreateSystemConnection())
            db.Execute("INSERT INTO users(id, name, email, created_at) VALUES (@id, 'F', @e, @now)",
                new { id, e = id + "@example.com", now = DateTime.UtcNow.ToString("o") });
        return As(id);
    }

    private static string NewAddress() => $"pw-{Guid.NewGuid():N}@fishbowl.test";

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement;

    private static async Task<string?> ErrorOf(HttpResponseMessage r) =>
        (await Json(r)).TryGetProperty("error", out var e) ? e.GetString() : null;

    private static object AccountBody(string address, string password = Pw, string? provider = "custom") => new
    {
        provider,
        address,
        password,
        imapHost = "imap.fishbowl.test",
        smtpHost = "smtp.fishbowl.test",
    };

    private async Task<string> AddAccountAsync(HttpClient c, string basePath, string? address = null)
    {
        var r = await c.PostAsJsonAsync(basePath + "/accounts", AccountBody(address ?? NewAddress()), Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetString()!;
    }

    // The background loop is stopped: a "sync now" request would wake it to run beside the
    // passes this class drives itself (two passes on one account race on the message keys).
    private MailSyncService SyncService
    {
        get
        {
            var service = _cookie.Services.GetServices<IHostedService>().OfType<MailSyncService>().Single();
            if (!_stopped)
            {
                _stopped = true;
                service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            return service;
        }
    }
    private bool _stopped;

    /// <summary>Runs sync passes until the account is ok with its history read.</summary>
    private async Task SyncUntilDoneAsync(HttpClient c, string basePath, string accountId)
    {
        var last = "";
        for (var i = 0; i < 25; i++)
        {
            await SyncService.RunOnceAsync(Ct);
            var accounts = await Json(await c.GetAsync(basePath + "/accounts", Ct));
            var a = accounts.EnumerateArray().Single(x => x.GetProperty("id").GetString() == accountId);
            last = a.ToString();
            if (a.GetProperty("state").GetString() == "ok" && a.GetProperty("backfillDone").GetBoolean()) return;
        }
        Assert.Fail("account never finished syncing: " + last);
    }

    private async Task<string> SyncedAccountAsync(HttpClient c, string basePath, string? address = null)
    {
        var id = await AddAccountAsync(c, basePath, address);
        await SyncUntilDoneAsync(c, basePath, id);
        return id;
    }

    private static async Task<List<JsonElement>> Threads(HttpClient c, string basePath, string query = "")
    {
        var r = await c.GetAsync(basePath + "/threads" + query, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static string ThreadIdOf(IEnumerable<JsonElement> threads, string subject) =>
        threads.Single(t => t.GetProperty("subject").GetString() == subject).GetProperty("threadId").GetString()!;

    private static async Task<List<JsonElement>> Messages(HttpClient c, string basePath, string threadId)
    {
        var r = await c.GetAsync($"{basePath}/threads/{threadId}", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private const string P = "/api/v1/mail";

    // ---- 1. accounts and threads ----

    [Fact]
    public async Task Account_Add_ReturnsNoSecret_AndSyncFillsTheList()
    {
        var c = Fresh();
        var address = NewAddress();
        var add = await c.PostAsJsonAsync(P + "/accounts", AccountBody(address), Ct);
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var raw = await add.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(Pw, raw);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        var id = JsonDocument.Parse(raw).RootElement.GetProperty("id").GetString()!;

        await SyncUntilDoneAsync(c, P, id);

        var listRaw = await (await c.GetAsync(P + "/accounts", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(Pw, listRaw);
        Assert.DoesNotContain("secret", listRaw, StringComparison.OrdinalIgnoreCase);

        var threads = await Threads(c, P);
        Assert.Equal(3, threads.Count);
        Assert.Contains(threads, t => t.GetProperty("subject").GetString() == "Analytical engine notes");

        var archived = await Threads(c, P, "?archived=true");
        Assert.Contains(archived, t => t.GetProperty("subject").GetString() == "Old project");
        Assert.True(archived.Count >= 31);
        Assert.DoesNotContain(archived, t => t.GetProperty("subject").GetString() == "Analytical engine notes");

        var found = await Threads(c, P, "?q=variables");
        Assert.Single(found);
        Assert.Equal("Analytical engine notes", found[0].GetProperty("subject").GetString());

        var unread = await Threads(c, P, "?unread=true");
        Assert.Equal(2, unread.Count);
        Assert.Contains(unread, t => t.GetProperty("subject").GetString() == "Analytical engine notes");
        Assert.Contains(unread, t => t.GetProperty("subject").GetString() == "This week in engines");
    }

    [Fact]
    public async Task Thread_ReturnsMessagesOldestFirst_WithHtmlFlags_ButNoHtmlBody()
    {
        var c = Fresh();
        await SyncedAccountAsync(c, P);
        var threads = await Threads(c, P);

        var msgs = await Messages(c, P, ThreadIdOf(threads, "Analytical engine notes"));
        Assert.Equal(3, msgs.Count);
        Assert.Equal(new[] { "in", "out", "in" }, msgs.Select(m => m.GetProperty("direction").GetString()));
        Assert.Equal(msgs.OrderBy(m => m.GetProperty("sentAt").GetDateTime()).Select(m => m.GetProperty("id").GetString()),
            msgs.Select(m => m.GetProperty("id").GetString()));
        Assert.Contains("operations cards", msgs[0].GetProperty("bodyText").GetString());
        Assert.All(msgs, m => Assert.False(m.TryGetProperty("bodyHtml", out _)));
        Assert.All(msgs, m => Assert.False(m.GetProperty("hasHtml").GetBoolean()));

        var news = await Messages(c, P, ThreadIdOf(threads, "This week in engines"));
        Assert.True(news[0].GetProperty("hasHtml").GetBoolean());
        Assert.True(news[0].GetProperty("remoteImages").GetBoolean());
        Assert.False(news[0].TryGetProperty("bodyHtml", out _));
        // How each is shown: a newsletter on paper, Outlook's plain HTML in the
        // app's colours, a mail made for light and dark in its own dark design.
        Assert.Equal("paper", news[0].GetProperty("htmlLook").GetString());
        Assert.Equal("plain", (await Messages(c, P, ThreadIdOf(threads, "Your invoice")))[0].GetProperty("htmlLook").GetString());
        var old = await Messages(c, P, ThreadIdOf(await Threads(c, P, "?archived=true"), "Old project"));
        Assert.Equal("adaptive", old[0].GetProperty("htmlLook").GetString());
        Assert.Equal(JsonValueKind.Null, msgs[0].GetProperty("htmlLook").ValueKind);
    }

    [Fact]
    public async Task Html_MadeForDark_IsSetDarkInTheDarkTheme_PaperNever()
    {
        var c = Fresh();
        await SyncedAccountAsync(c, P);
        var old = (await Messages(c, P, ThreadIdOf(await Threads(c, P, "?archived=true"), "Old project")))[0].GetProperty("id").GetString();
        var news = (await Messages(c, P, ThreadIdOf(await Threads(c, P), "This week in engines")))[0].GetProperty("id").GetString();

        var dark = await c.GetStringAsync($"{P}/messages/{old}/html?theme=dark", Ct);
        Assert.Contains("html{background:transparent;color-scheme:dark}", dark);
        Assert.Contains("@media (min-width:0px)", dark);           // its dark rules hold, whatever the browser prefers
        var light = await c.GetStringAsync($"{P}/messages/{old}/html?theme=light", Ct);
        Assert.Contains("html{background:#fff;color:#1a1a1a;color-scheme:light}", light);
        Assert.Contains("@media (max-width:0px)", light);          // …and never on paper
        Assert.DoesNotContain("prefers-color-scheme", light);
        Assert.Contains("html{background:#fff;color:#1a1a1a;color-scheme:light}", await c.GetStringAsync($"{P}/messages/{news}/html?theme=dark", Ct));
    }

    // ---- 2. errors ----

    [Fact]
    public async Task AddAccount_Errors()
    {
        var c = As(Owner);

        var wrongPw = await c.PostAsJsonAsync(P + "/accounts", AccountBody(NewAddress(), "nope"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrongPw.StatusCode);
        Assert.Equal("mail_auth_failed", await ErrorOf(wrongPw));

        var badAddress = await c.PostAsJsonAsync(P + "/accounts", AccountBody("not-an-address"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badAddress.StatusCode);
        Assert.Equal("invalid_address", await ErrorOf(badAddress));

        var badProvider = await c.PostAsJsonAsync(P + "/accounts", AccountBody(NewAddress(), Pw, "aol"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badProvider.StatusCode);
        Assert.Equal("invalid_provider", await ErrorOf(badProvider));
    }

    [Fact]
    public async Task AddAccount_EleventhIsRefused()
    {
        const string user = "mail_many";
        using (var db = _db.CreateSystemConnection())
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'Many', 'many@example.com', @now)",
                new { id = user, now = DateTime.UtcNow.ToString("o") });
        var c = As(user);
        for (var i = 0; i < 10; i++) await AddAccountAsync(c, P);
        var eleventh = await c.PostAsJsonAsync(P + "/accounts", AccountBody(NewAddress()), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, eleventh.StatusCode);
        Assert.Equal("too_many_accounts", await ErrorOf(eleventh));
    }

    [Fact]
    public async Task Tags_Invalid_IsRefused()
    {
        var c = Fresh();
        await SyncedAccountAsync(c, P);
        var id = ThreadIdOf(await Threads(c, P), "Your invoice");
        var r = await c.PutAsJsonAsync($"{P}/threads/{id}/tags", new { tags = new[] { "has space" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_tag", await ErrorOf(r));
    }

    // ---- 3. HTML page ----

    [Fact]
    public async Task Html_IsSandboxed_ImagesOnlyOnRequest()
    {
        var c = Fresh();
        await SyncedAccountAsync(c, P);
        var threads = await Threads(c, P);
        var msgId = (await Messages(c, P, ThreadIdOf(threads, "This week in engines")))[0].GetProperty("id").GetString();

        var plain = await c.GetAsync($"{P}/messages/{msgId}/html", Ct);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        var csp = string.Join(";", plain.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("sandbox", csp);
        Assert.DoesNotContain("allow-scripts", csp);
        Assert.Contains("img-src data:", csp);
        Assert.Contains("font-src 'self';", csp);   // the kit's Inter, nothing from elsewhere
        Assert.DoesNotContain("https:", csp);
        Assert.Equal("nosniff", plain.Headers.GetValues("X-Content-Type-Options").Single());
        var page = await plain.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Engines everywhere", page);
        Assert.Contains("url(/kit/fonts/inter-latin.woff2)", page);

        var withImages = await c.GetAsync($"{P}/messages/{msgId}/html?images=true", Ct);
        var csp2 = string.Join(";", withImages.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("img-src data: 'self' https:", csp2);
        Assert.DoesNotContain("allow-scripts", csp2);
        Assert.Equal("nosniff", withImages.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Html_OfATextOnlyMessage_Is404()
    {
        var c = Fresh();
        await SyncedAccountAsync(c, P);
        var msgId = (await Messages(c, P, ThreadIdOf(await Threads(c, P), "Analytical engine notes")))[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"{P}/messages/{msgId}/html", Ct)).StatusCode);
    }

    // ---- 3b. delete (decision 8) ----

    /// <summary>One pass now: an account synced a moment ago isn't due until asked.</summary>
    private async Task SyncNowAsync(HttpClient c, string accountId)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsync($"{P}/accounts/{accountId}/sync", null, Ct)).StatusCode);
        await SyncService.RunOnceAsync(Ct);
    }

    private static async Task<string> TrashIdOfMailAsync(HttpClient c)
    {
        var trash = await Json(await c.GetAsync("/api/v1/trash", Ct));
        return trash.EnumerateArray().Single(x => x.GetProperty("kind").GetString() == "mail").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Delete_OnlyInFishbowl_StaysAway_ARestoreFindsItsPlaceAgain()
    {
        var c = Fresh();
        var accountId = await SyncedAccountAsync(c, P);
        var id = ThreadIdOf(await Threads(c, P), "Your invoice");

        var r = await c.DeleteAsync($"{P}/threads/{id}?mode=fishbowl", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, (await Json(r)).GetProperty("deleted").GetInt32());
        Assert.DoesNotContain(await Threads(c, P), t => t.GetProperty("subject").GetString() == "Your invoice");

        // The server keeps it; the next pass leaves it out.
        await SyncNowAsync(c, accountId);
        Assert.DoesNotContain(await Threads(c, P), t => t.GetProperty("subject").GetString() == "Your invoice");

        // Restored, the next pass finds it in the inbox again.
        var restore = await c.PostAsync($"/api/v1/trash/{await TrashIdOfMailAsync(c)}/restore", null, Ct);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        await SyncNowAsync(c, accountId);
        Assert.Contains(await Threads(c, P), t => t.GetProperty("subject").GetString() == "Your invoice");
    }

    [Fact]
    public async Task Delete_Everywhere_GoesToTheServersTrash_OneMessageOfAThread()
    {
        var c = Fresh();
        var accountId = await SyncedAccountAsync(c, P);
        var engine = ThreadIdOf(await Threads(c, P), "Analytical engine notes");
        var first = (await Messages(c, P, engine))[0].GetProperty("id").GetString();

        var r = await c.DeleteAsync($"{P}/messages/{first}?mode=everywhere", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("everywhere", (await Json(r)).GetProperty("mode").GetString());
        Assert.Equal(2, (await Messages(c, P, engine)).Count);

        // Off the server's inbox: restored, it finds no place there and stays archived.
        await SyncNowAsync(c, accountId);
        Assert.Equal(2, (await Messages(c, P, engine)).Count);
        await c.PostAsync($"/api/v1/trash/{await TrashIdOfMailAsync(c)}/restore", null, Ct);
        await SyncNowAsync(c, accountId);
        var back = (await Messages(c, P, engine)).Single(m => m.GetProperty("id").GetString() == first);
        Assert.Equal("archived", back.GetProperty("state").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"{P}/messages/nope?mode=everywhere", Ct)).StatusCode);
    }

    // ---- 3b. archive and flags (phase 2) ----

    private static string ThreadState(IEnumerable<JsonElement> messages) =>
        string.Join(",", messages.Select(m => m.GetProperty("state").GetString()));

    [Fact]
    public async Task Archive_MovesOnTheServer_LeavesTheList_AndComesBack()
    {
        var c = Fresh();
        var accountId = await SyncedAccountAsync(c, P);
        var engine = ThreadIdOf(await Threads(c, P), "Analytical engine notes");

        var r = await c.PostAsJsonAsync($"{P}/threads/{engine}/archive", new { archived = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // Moved on the server: the next pass reads it from the archive folder. The
        // reply in Sent stays there and leaves the list with its conversation.
        await SyncNowAsync(c, accountId);
        Assert.DoesNotContain(await Threads(c, P), t => t.GetProperty("threadId").GetString() == engine);
        var archived = (await Threads(c, P, "?archived=true")).Single(t => t.GetProperty("threadId").GetString() == engine);
        Assert.True(archived.GetProperty("archived").GetBoolean());
        Assert.Equal("archived,archived,archived", ThreadState(await Messages(c, P, engine)));

        r = await c.PostAsJsonAsync($"{P}/threads/{engine}/archive", new { archived = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        await SyncNowAsync(c, accountId);
        Assert.Contains(await Threads(c, P), t => t.GetProperty("threadId").GetString() == engine);
        Assert.Equal("inbox,sent,inbox", ThreadState(await Messages(c, P, engine)));

        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync($"{P}/threads/nope/archive", new { archived = true }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Flag_OnTheLatestMessage_ReachesTheServer_UnflagTakesEveryFlagOff()
    {
        var c = Fresh();
        var accountId = await SyncedAccountAsync(c, P);
        var engine = ThreadIdOf(await Threads(c, P), "Analytical engine notes");
        string Flags(List<JsonElement> ms) => string.Join(",", ms.Select(m => m.GetProperty("flagged").GetBoolean() ? "1" : "0"));

        // Written back in the background: a pass before it lands reads the old
        // flag, the next one the new — what the server holds wins.
        async Task<string> SettledAsync(string want)
        {
            var now = "";
            for (var i = 0; i < 20 && now != want; i++)
            {
                await SyncNowAsync(c, accountId);
                now = Flags(await Messages(c, P, engine));
            }
            return now;
        }

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"{P}/threads/{engine}/flagged", new { flagged = true }, Ct)).StatusCode);
        Assert.Equal("0,0,1", Flags(await Messages(c, P, engine)));
        Assert.True((await Threads(c, P)).Single(t => t.GetProperty("threadId").GetString() == engine).GetProperty("flagged").GetBoolean());
        Assert.Equal("0,0,1", await SettledAsync("0,0,1"));

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"{P}/threads/{engine}/flagged", new { flagged = false }, Ct)).StatusCode);
        Assert.Equal("0,0,0", await SettledAsync("0,0,0"));
    }

    // ---- 3c. writing and sending (phase 2) ----

    private static async Task<JsonElement> DraftAsync(HttpClient c, object body)
    {
        var r = await c.PostAsJsonAsync($"{P}/drafts", body, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).Clone();
    }

    [Fact]
    public async Task Reply_IsFilledFromItsMessage_GoesOutWithQuoteAndThreading_AndStandsInItsConversation()
    {
        var c = Fresh();
        var address = NewAddress();
        var accountId = await SyncedAccountAsync(c, P, address);
        var engine = ThreadIdOf(await Threads(c, P), "Analytical engine notes");
        var latest = (await Messages(c, P, engine))[^1].GetProperty("id").GetString()!;

        var d = await DraftAsync(c, new { kind = "reply", messageId = latest });
        var id = d.GetProperty("id").GetString();
        Assert.Equal(accountId, d.GetProperty("accountId").GetString());
        Assert.Equal(engine, d.GetProperty("threadId").GetString());
        Assert.Equal("Re: Analytical engine notes", d.GetProperty("subject").GetString());
        Assert.Contains("ada@example.org", d.GetProperty("to")[0].GetString());

        var r = await c.PatchAsJsonAsync($"{P}/drafts/{id}", new { body = "Thanks, **Ada**.\nSee you." }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Single((await Json(await c.GetAsync($"{P}/drafts", Ct))).EnumerateArray());

        r = await c.PostAsJsonAsync($"{P}/drafts/{id}/send", new { timeZone = "Europe/Berlin", language = "de" }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(engine, (await Json(r)).GetProperty("threadId").GetString());
        Assert.Empty((await Json(await c.GetAsync($"{P}/drafts", Ct))).EnumerateArray());

        var sent = Fishbowl.Host.Testing.SampleMailboxConnector.Outbox(address).Last();
        Assert.Equal("engine-3@example.org", sent.InReplyTo);
        Assert.Equal(new[] { "engine-1@example.org", "engine-2@fishbowl.test", "engine-3@example.org" }, sent.References.ToArray());
        Assert.Contains("Thanks, **Ada**.", sent.TextBody);
        Assert.True(sent.TextBody.Contains("schrieb Ada Lovelace <ada@example.org>:"), sent.TextBody);
        Assert.Contains("> The variables live in the store, column by column.", sent.TextBody);
        Assert.Contains("<strong>Ada</strong>", sent.HtmlBody);
        Assert.Contains("<br", sent.HtmlBody);   // a line break is a line break
        Assert.Contains("<blockquote", sent.HtmlBody);

        // At once in its conversation, read and outgoing; the next pass finds its
        // copy in Sent and adds no second one.
        var msgs = await Messages(c, P, engine);
        Assert.Equal(4, msgs.Count);
        Assert.Equal("out", msgs[^1].GetProperty("direction").GetString());
        Assert.Equal("sent", msgs[^1].GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, msgs[^1].GetProperty("sentBy").ValueKind);
        await SyncNowAsync(c, accountId);
        Assert.Equal(4, (await Messages(c, P, engine)).Count);
    }

    [Fact]
    public async Task Forward_CarriesTheOriginalsAttachment_AndAnUploadedFile_InItsConversation()
    {
        var c = Fresh();
        var address = NewAddress();
        await SyncedAccountAsync(c, P, address);
        var invoice = ThreadIdOf(await Threads(c, P), "Your invoice");
        var original = (await Messages(c, P, invoice))[0].GetProperty("id").GetString()!;

        var d = await DraftAsync(c, new { kind = "forward", messageId = original });
        var id = d.GetProperty("id").GetString();
        Assert.Equal("Fwd: Your invoice", d.GetProperty("subject").GetString());
        Assert.Equal(0, d.GetProperty("to").GetArrayLength());
        var files = d.GetProperty("files");
        Assert.Equal("invoice.pdf", files[0].GetProperty("name").GetString());
        Assert.True(files[0].GetProperty("forwarded").GetBoolean());

        var upload = new ByteArrayContent(Encoding.UTF8.GetBytes("hello"));
        upload.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        var r = await c.PostAsync($"{P}/drafts/{id}/files?name=notes.txt", upload, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, (await Json(r)).GetProperty("index").GetInt32());

        // No one to send it to yet: refused, nothing goes out.
        r = await c.PostAsJsonAsync($"{P}/drafts/{id}/send", new { }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("no_recipients", (await Json(r)).GetProperty("error").GetString());
        await c.PatchAsJsonAsync($"{P}/drafts/{id}", new { to = new[] { "not an address" } }, Ct);
        r = await c.PostAsJsonAsync($"{P}/drafts/{id}/send", new { }, Ct);
        Assert.Equal("invalid_address", (await Json(r)).GetProperty("error").GetString());
        Assert.Empty(Fishbowl.Host.Testing.SampleMailboxConnector.Outbox(address));

        await c.PatchAsJsonAsync($"{P}/drafts/{id}", new { to = new[] { "Grace <grace@example.org>" }, body = "For you." }, Ct);
        r = await c.PostAsJsonAsync($"{P}/drafts/{id}/send", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // Like Gmail: the forward joins its original's conversation (same subject, a shared address).
        Assert.Equal(invoice, (await Json(r)).GetProperty("threadId").GetString());

        var sent = Fishbowl.Host.Testing.SampleMailboxConnector.Outbox(address).Single();
        Assert.Null(sent.InReplyTo);
        Assert.Contains("---------- Forwarded message ----------", sent.TextBody);
        Assert.Contains("Your invoice is attached.", sent.TextBody);
        var parts = sent.Attachments.OfType<MimeKit.MimePart>().ToList();
        Assert.Equal(new[] { "invoice.pdf", "notes.txt" }, parts.Select(p => p.FileName).ToArray());
        using var pdf = new MemoryStream();
        await parts[0].Content.DecodeToAsync(pdf, Ct);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.ToArray()));
    }

    [Fact]
    public async Task Send_NeedsItsOwnScope_AndADraftIsItsWritersOwn()
    {
        const string user = "mail_sender";
        using (var db = _db.CreateSystemConnection())
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'S', 's@example.com', @now)",
                new { id = user, now = DateTime.UtcNow.ToString("o") });
        var cookie = As(user);
        var address = NewAddress();
        await SyncedAccountAsync(cookie, P, address);

        // Read and write may draft, not send.
        var write = await KeyAsync(user, ContextRef.User(user), "read:mail", "write:mail");
        var d = await DraftAsync(write, new { kind = "new" });
        var id = d.GetProperty("id").GetString();
        await write.PatchAsJsonAsync($"{P}/drafts/{id}", new { to = new[] { "ada@example.org" }, subject = "Hello", body = "Hi." }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await write.PostAsJsonAsync($"{P}/drafts/{id}/send", new { }, Ct)).StatusCode);

        // Another person never sees it.
        Assert.Equal(HttpStatusCode.NotFound, (await Fresh().GetAsync($"{P}/drafts/{id}", Ct)).StatusCode);

        var send = await KeyAsync(user, ContextRef.User(user), "read:mail", "write:mail", "send:mail");
        var r = await send.PostAsJsonAsync($"{P}/drafts/{id}/send", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var threadId = (await Json(r)).GetProperty("threadId").GetString()!;
        var mine = (await Messages(cookie, P, threadId)).Single();
        Assert.False(string.IsNullOrEmpty(mine.GetProperty("sentBy").GetString()));
        Assert.Equal("Hello", Fishbowl.Host.Testing.SampleMailboxConnector.Outbox(address).Single().Subject);
    }

    // ---- 3e. spam: read live, never stored, a person's only ----

    [Fact]
    public async Task Spam_IsReadLive_NeverStored_NotSpamBringsItIn_AndNoKeySeesIt()
    {
        const string user = "mail_spam";
        using (var db = _db.CreateSystemConnection())
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'S', 'sp@example.com', @now)",
                new { id = user, now = DateTime.UtcNow.ToString("o") });
        var c = As(user);
        var accountId = await SyncedAccountAsync(c, P);
        // The sync never reads the spam folder: not in the list, not in search.
        Assert.DoesNotContain(await Threads(c, P), t => t.GetProperty("subject").GetString() == "Contract renewal");
        Assert.Empty(await Threads(c, P, "?q=renewal"));

        var items = (await Json(await c.GetAsync($"{P}/spam", Ct))).GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
        Assert.Equal(new[] { "Contract renewal", "You have won!!!" }, items.Select(i => i.GetProperty("subject").GetString()).ToArray());
        var contract = items[0];
        var uid = contract.GetProperty("uid").GetUInt32();
        var one = await Json(await c.GetAsync($"{P}/spam/{accountId}/{uid}", Ct));
        Assert.Contains("renewal by Friday", one.GetProperty("text").GetString());

        // A spam folder that changed under the UID moves nothing.
        var r = await c.PostAsJsonAsync($"{P}/spam/{accountId}/{uid}/not-spam", new { uidValidity = 999u }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("spam_changed", (await Json(r)).GetProperty("error").GetString());

        r = await c.PostAsJsonAsync($"{P}/spam/{accountId}/{uid}/not-spam", new { uidValidity = contract.GetProperty("uidValidity").GetUInt32() }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        await SyncNowAsync(c, accountId);
        Assert.Contains(await Threads(c, P), t => t.GetProperty("subject").GetString() == "Contract renewal");
        Assert.Single((await Json(await c.GetAsync($"{P}/spam", Ct))).GetProperty("items").EnumerateArray());

        // No key reads spam or moves it, whatever its scopes.
        var key = await KeyAsync(user, ContextRef.User(user), "read:mail", "write:mail", "send:mail");
        Assert.Equal(HttpStatusCode.Forbidden, (await key.GetAsync($"{P}/spam", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await key.GetAsync($"{P}/spam/{accountId}/1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await key.PostAsJsonAsync($"{P}/spam/{accountId}/1/not-spam", new { uidValidity = 1u }, Ct)).StatusCode);
    }

    [Fact]
    public async Task SpamReadAll_MarksTheWholeSpamFolderRead_OnTheServer()
    {
        const string user = "mail_spam_read";
        using (var db = _db.CreateSystemConnection())
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'S', 'spr@example.com', @now)",
                new { id = user, now = DateTime.UtcNow.ToString("o") });
        var c = As(user);
        await SyncedAccountAsync(c, P);
        async Task<bool[]> Seen() => (await Json(await c.GetAsync($"{P}/spam", Ct))).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("seen").GetBoolean()).ToArray();
        Assert.Equal(new[] { false, false }, await Seen());

        // A key never does it, whatever its scopes.
        var key = await KeyAsync(user, ContextRef.User(user), "read:mail", "write:mail", "send:mail");
        Assert.Equal(HttpStatusCode.Forbidden, (await key.PostAsync($"{P}/spam/read-all", null, Ct)).StatusCode);

        var r = await Json(await c.PostAsync($"{P}/spam/read-all", null, Ct));
        Assert.Equal(2, r.GetProperty("messages").GetInt32());
        Assert.Empty(r.GetProperty("failed").EnumerateArray());
        // Read on the server: the next look (no cached read) says so; again, nothing is left.
        Assert.Equal(new[] { true, true }, await Seen());
        Assert.Equal(0, (await Json(await c.PostAsync($"{P}/spam/read-all", null, Ct))).GetProperty("messages").GetInt32());
    }

    // ---- 4. attachment ----

    [Fact]
    public async Task Attachment_IsDownloadedAsAttachment_WithItsName()
    {
        var c = Fresh();
        await SyncedAccountAsync(c, P);
        var msgId = (await Messages(c, P, ThreadIdOf(await Threads(c, P), "Your invoice")))[0].GetProperty("id").GetString();

        var r = await c.GetAsync($"{P}/messages/{msgId}/attachments/0", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var bytes = await r.Content.ReadAsByteArrayAsync(Ct);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes));
        Assert.Equal("attachment", r.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("invoice.pdf", r.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"{P}/messages/{msgId}/attachments/5", Ct)).StatusCode);
    }

    // ---- 5. tags ----

    [Fact]
    public async Task Tags_AreNormalised_ListedInTheWorkspace_AndFilter()
    {
        var c = Fresh();
        await SyncedAccountAsync(c, P);
        var threads = await Threads(c, P);
        var id = ThreadIdOf(threads, "Your invoice");

        var r = await c.PutAsJsonAsync($"{P}/threads/{id}/tags", new { tags = new[] { "Bills" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new[] { "bills" }, (await Json(r)).GetProperty("tags").EnumerateArray().Select(x => x.GetString()));

        var tags = await (await c.GetAsync("/api/v1/tags", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("bills", tags);

        var filtered = await Threads(c, P, "?tag=bills");
        Assert.Single(filtered);
        Assert.Equal(id, filtered[0].GetProperty("threadId").GetString());
        // Where it came from first (the account's source tag), then what was given.
        // The account's source tag: its name (here its address) in tag letters, no prefix.
        Assert.DoesNotContain(":", filtered[0].GetProperty("tags")[0].GetString());
        Assert.NotEqual("bills", filtered[0].GetProperty("tags")[0].GetString());
        Assert.Equal("bills", filtered[0].GetProperty("tags")[1].GetString());

        // The workspace's tag says where it is used: a conversation, no note —
        // and a rule once one adds it.
        async Task<JsonElement> Bills() => (await Json(await c.GetAsync("/api/v1/tags", Ct))).EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "bills");
        var bills = await Bills();
        Assert.Equal((0, 1, 0), (bills.GetProperty("usageCount").GetInt32(), bills.GetProperty("mailCount").GetInt32(), bills.GetProperty("ruleCount").GetInt32()));
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync(P + "/rules", RuleBody("from", "billing", ["bills"]), Ct)).StatusCode);
        Assert.Equal(1, (await Bills()).GetProperty("ruleCount").GetInt32());
    }

    // ---- 6. seen ----

    [Fact]
    public async Task Seen_DropsUnreadCount_AndTheServerKeepsIt()
    {
        var c = Fresh();
        var accountId = await SyncedAccountAsync(c, P);
        var id = ThreadIdOf(await Threads(c, P), "Analytical engine notes");
        var before = (await Json(await c.GetAsync(P + "/unread-count", Ct))).GetProperty("unread").GetInt32();
        Assert.Equal(2, before);

        var r = await c.PostAsJsonAsync($"{P}/threads/{id}/seen", new { seen = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, (await Json(await c.GetAsync(P + "/unread-count", Ct))).GetProperty("unread").GetInt32());

        // The write-back to the server runs in the background; a later sync reads the server's flags.
        await Task.Delay(1500, Ct);
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsync($"{P}/accounts/{accountId}/sync", null, Ct)).StatusCode);
        await SyncService.RunOnceAsync(Ct);
        Assert.Equal(1, (await Json(await c.GetAsync(P + "/unread-count", Ct))).GetProperty("unread").GetInt32());
        var thread = (await Threads(c, P)).Single(t => t.GetProperty("threadId").GetString() == id);
        Assert.Equal(0, thread.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task ReadAll_ReadsTheWholeTab_AndTheServerKeepsIt()
    {
        var c = Fresh();
        var accountId = await SyncedAccountAsync(c, P);
        int Unread(JsonElement e) => e.GetProperty("unread").GetInt32();
        Assert.Equal(2, Unread(await Json(await c.GetAsync(P + "/unread-count", Ct))));

        // The archive holds nothing unread: the list keeps its two.
        var r = await c.PostAsJsonAsync($"{P}/read-all", new { archived = true }, Ct);
        Assert.Equal(0, (await Json(r)).GetProperty("conversations").GetInt32());
        Assert.Equal(2, Unread(await Json(await c.GetAsync(P + "/unread-count", Ct))));

        r = await c.PostAsJsonAsync($"{P}/read-all", new { archived = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await Json(r)).GetProperty("conversations").GetInt32());
        Assert.Equal(0, Unread(await Json(await c.GetAsync(P + "/unread-count", Ct))));
        Assert.Equal(0, (await Json(await c.PostAsJsonAsync($"{P}/read-all", new { }, Ct))).GetProperty("conversations").GetInt32());

        // Written back in the background: a later sync reads the server's flags.
        await Task.Delay(1500, Ct);
        await SyncNowAsync(c, accountId);
        Assert.Equal(0, Unread(await Json(await c.GetAsync(P + "/unread-count", Ct))));
        Assert.All(await Threads(c, P), t => Assert.Equal(0, t.GetProperty("unread").GetInt32()));
    }

    // ---- 7. space roles ----

    private async Task<(Space Space, string Base)> SpaceAsync()
    {
        var repo = new SpaceRepository(_db);
        var space = await repo.CreateAsync(Owner, "Mail " + Guid.NewGuid().ToString("N")[..6], Ct);
        await repo.AddMemberAsync(space.Id, Admin, SpaceRole.Admin, Ct);
        await repo.AddMemberAsync(space.Id, Member, SpaceRole.Member, Ct);
        await repo.AddMemberAsync(space.Id, Reader, SpaceRole.Reader, Ct);
        return (space, $"/api/v1/spaces/{space.Slug}/mail");
    }

    [Fact]
    public async Task Space_Roles_ReaderReads_MemberOrganises_AdminManages()
    {
        var (space, sp) = await SpaceAsync();
        var owner = As(Owner);
        var accountId = await SyncedAccountAsync(owner, sp);
        var threads = await Threads(owner, sp);
        Assert.Equal(3, threads.Count);
        var id = ThreadIdOf(threads, "Your invoice");

        // Reader: reads, nothing else.
        var reader = As(Reader);
        Assert.Equal(3, (await Threads(reader, sp)).Count);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(sp + "/accounts", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(sp + "/unread-count", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{sp}/threads/{id}/tags", new { tags = new[] { "x" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{sp}/threads/{id}/seen", new { seen = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"{sp}/accounts/{accountId}/sync", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(sp + "/accounts", AccountBody(NewAddress()), Ct)).StatusCode);

        // Member: organises, doesn't manage.
        var member = As(Member);
        Assert.Equal(HttpStatusCode.OK, (await member.PutAsJsonAsync($"{sp}/threads/{id}/tags", new { tags = new[] { "shared" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await member.PostAsync($"{sp}/accounts/{accountId}/sync", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(sp + "/accounts", AccountBody(NewAddress()), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"{sp}/accounts/{accountId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PatchAsJsonAsync($"{sp}/accounts/{accountId}", new { name = "x" }, Ct)).StatusCode);

        // Admin manages.
        var admin = As(Admin);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(sp + "/accounts", AccountBody(NewAddress()), Ct)).StatusCode);

        // The personal workspace stays out of it.
        Assert.Empty(await Threads(As(Owner), P));

        // Not a member.
        var outsider = As(Outsider);
        var denied = (await outsider.GetAsync(sp + "/threads", Ct)).StatusCode;
        Assert.True(denied is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"got {denied}");
        denied = (await outsider.PostAsJsonAsync(sp + "/accounts", AccountBody(NewAddress()), Ct)).StatusCode;
        Assert.True(denied is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"got {denied}");
    }

    // ---- 8. Bearer keys ----

    [Fact]
    public async Task Bearer_ReadScope_Reads_ButNothingElse()
    {
        const string user = "mail_bearer";
        using (var db = _db.CreateSystemConnection())
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'B', 'b@example.com', @now)",
                new { id = user, now = DateTime.UtcNow.ToString("o") });
        var cookie = As(user);
        var accountId = await SyncedAccountAsync(cookie, P);
        var id = ThreadIdOf(await Threads(cookie, P), "Your invoice");

        var read = await KeyAsync(user, ContextRef.User(user), "read:mail");
        Assert.Equal(3, (await Threads(read, P)).Count);
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync(P + "/accounts", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PutAsJsonAsync($"{P}/threads/{id}/tags", new { tags = new[] { "x" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsJsonAsync($"{P}/threads/{id}/seen", new { seen = true }, Ct)).StatusCode);

        var noMail = await KeyAsync(user, ContextRef.User(user), "read:notes");
        Assert.Equal(HttpStatusCode.Forbidden, (await noMail.GetAsync(P + "/threads", Ct)).StatusCode);

        // Writing scope organises, but accounts are cookie-only for any Bearer.
        var write = await KeyAsync(user, ContextRef.User(user), "read:mail", "write:mail");
        Assert.Equal(HttpStatusCode.OK, (await write.PutAsJsonAsync($"{P}/threads/{id}/tags", new { tags = new[] { "x" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await write.PostAsJsonAsync(P + "/accounts", AccountBody(NewAddress()), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await write.PatchAsJsonAsync($"{P}/accounts/{accountId}", new { name = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await write.DeleteAsync($"{P}/accounts/{accountId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Bearer_SpaceKey_OnPersonalRoute_Is403()
    {
        var (space, _) = await SpaceAsync();
        var spaceKey = await KeyAsync(Owner, ContextRef.Space(space.Slug), "read:mail", "write:mail");
        Assert.Equal(HttpStatusCode.Forbidden, (await spaceKey.GetAsync(P + "/threads", Ct)).StatusCode);
    }

    // ---- 9. MCP ----

    private static async Task<JsonElement> RpcAsync(HttpClient c, string method, object? args = null, string? tool = null)
    {
        var body = tool is null
            ? (object)new { jsonrpc = "2.0", id = 1, method }
            : new { jsonrpc = "2.0", id = 1, method, @params = new { name = tool, arguments = args ?? new { } } };
        var r = await c.PostAsync("/mcp", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), Ct);
        r.EnsureSuccessStatusCode();
        return await Json(r);
    }

    private static JsonElement ToolPayload(JsonElement rpc)
    {
        var text = rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task Mcp_MailTools_AreListed_Untrusted_AndScoped()
    {
        const string user = "mail_mcp";
        using (var db = _db.CreateSystemConnection())
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'M', 'm@example.com', @now)",
                new { id = user, now = DateTime.UtcNow.ToString("o") });
        await SyncedAccountAsync(As(user), P);

        var key = await KeyAsync(user, ContextRef.User(user), "read:mail");
        var list = await RpcAsync(key, "tools/list");
        var names = list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("mail_search", names);
        Assert.Contains("mail_thread", names);

        var search = ToolPayload(await RpcAsync(key, "tools/call", new { query = "engine" }, "mail_search"));
        Assert.True(search.GetProperty("untrusted").GetBoolean());
        var conversations = search.GetProperty("conversations").EnumerateArray().ToList();
        Assert.Contains(conversations, c => c.GetProperty("subject").GetString() == "Analytical engine notes");
        var threadId = conversations.Single(c => c.GetProperty("subject").GetString() == "Analytical engine notes").GetProperty("threadId").GetString();

        var thread = ToolPayload(await RpcAsync(key, "tools/call", new { threadId }, "mail_thread"));
        Assert.True(thread.GetProperty("untrusted").GetBoolean());
        var messages = thread.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(3, messages.Count);
        Assert.Contains("operations cards", messages[0].GetProperty("text").GetString());
        Assert.Equal("in", messages[0].GetProperty("direction").GetString());

        // Without read:mail the tools are refused like any scope-less tool.
        var noMail = await KeyAsync(user, ContextRef.User(user), "read:notes");
        foreach (var (tool, args) in new (string, object)[] { ("mail_search", new { }), ("mail_thread", new { threadId }) })
        {
            var err = (await RpcAsync(noMail, "tools/call", args, tool)).GetProperty("error");
            Assert.Equal(-32602, err.GetProperty("code").GetInt32());
            Assert.Contains("Scope denied", err.GetProperty("message").GetString() ?? "");
        }
    }

    // Phase 2 for agents: organise, draft and — only with send:mail — send.
    [Fact]
    public async Task Mcp_MailWriteTools_Organise_Draft_AndSendOnlyWithItsScope()
    {
        const string user = "mail_mcp_write";
        using (var db = _db.CreateSystemConnection())
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'W', 'w@example.com', @now)",
                new { id = user, now = DateTime.UtcNow.ToString("o") });
        var cookie = As(user);
        var address = NewAddress();
        await SyncedAccountAsync(cookie, P, address);
        var engine = ThreadIdOf(await Threads(cookie, P), "Analytical engine notes");
        var latest = (await Messages(cookie, P, engine))[^1].GetProperty("id").GetString();

        var key = await KeyAsync(user, ContextRef.User(user), "read:mail", "write:mail", "write:contacts");
        var names = (await RpcAsync(key, "tools/list")).GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToHashSet();
        foreach (var tool in new[] { "mail_tag", "mail_mark", "mail_archive", "mail_delete", "mail_draft", "mail_send", "mail_assign_contact" })
            Assert.Contains(tool, names);

        var tagged = ToolPayload(await RpcAsync(key, "tools/call", new { threadId = engine, add = new[] { "project" } }, "mail_tag"));
        Assert.Contains("project", tagged.GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
        ToolPayload(await RpcAsync(key, "tools/call", new { threadId = engine, seen = true, flagged = true }, "mail_mark"));
        Assert.True((await Threads(cookie, P)).Single(t => t.GetProperty("threadId").GetString() == engine).GetProperty("flagged").GetBoolean());

        // A reply drafted for the person: theirs in Drafts; sending needs send:mail.
        var draft = ToolPayload(await RpcAsync(key, "tools/call", new { kind = "reply", messageId = latest, body = "On it." }, "mail_draft"));
        var draftId = draft.GetProperty("draftId").GetString();
        Assert.Contains("ada@example.org", draft.GetProperty("to")[0].GetString());
        Assert.Single((await Json(await cookie.GetAsync($"{P}/drafts", Ct))).EnumerateArray());
        var denied = (await RpcAsync(key, "tools/call", new { draftId }, "mail_send")).GetProperty("error");
        Assert.Contains("Scope denied", denied.GetProperty("message").GetString() ?? "");

        var sender = await KeyAsync(user, ContextRef.User(user), "read:mail", "write:mail", "send:mail");
        var sent = ToolPayload(await RpcAsync(sender, "tools/call", new { draftId }, "mail_send"));
        Assert.True(sent.GetProperty("sent").GetBoolean());
        Assert.Equal(engine, sent.GetProperty("threadId").GetString());
        // …and one written in the call itself.
        sent = ToolPayload(await RpcAsync(sender, "tools/call", new { to = new[] { "grace@example.org" }, subject = "Agenda", body = "- one\n- two" }, "mail_send"));
        Assert.True(sent.GetProperty("sent").GetBoolean());
        var outbox = Fishbowl.Host.Testing.SampleMailboxConnector.Outbox(address);
        Assert.Equal(new[] { "Re: Analytical engine notes", "Agenda" }, outbox.Select(m => m.Subject).ToArray());
        Assert.Contains("<li>two</li>", outbox[1].HtmlBody);

        // Archived on the server, then deleted here only.
        ToolPayload(await RpcAsync(key, "tools/call", new { threadId = engine }, "mail_archive"));
        Assert.Contains(await Threads(cookie, P, "?archived=true"), t => t.GetProperty("threadId").GetString() == engine);
        var deleted = ToolPayload(await RpcAsync(key, "tools/call", new { threadId = engine }, "mail_delete"));
        Assert.Equal(4, deleted.GetProperty("deleted").GetInt32());

        // An address for a contact, once.
        var contact = await Json(await cookie.PostAsJsonAsync("/api/v1/contacts", new { kind = "person", firstName = "Grace", lastName = "Hopper" }, Ct));
        var contactId = contact.GetProperty("id").GetString();
        var assigned = ToolPayload(await RpcAsync(key, "tools/call", new { contactId, address = "grace@example.org" }, "mail_assign_contact"));
        Assert.True(assigned.GetProperty("added").GetBoolean());
        assigned = ToolPayload(await RpcAsync(key, "tools/call", new { contactId, address = "GRACE@example.org" }, "mail_assign_contact"));
        Assert.False(assigned.GetProperty("added").GetBoolean());
    }

    // ---- 10. delete ----

    [Fact]
    public async Task Account_Delete_RemovesItsThreads_NotTheOthers()
    {
        var c = Fresh();
        var first = await SyncedAccountAsync(c, P);
        var second = await SyncedAccountAsync(c, P);
        // The sample accounts carry the same Message-Ids, so the conversations are shared.
        Assert.Equal(3, (await Threads(c, P)).Count);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"{P}/accounts/{first}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"{P}/accounts/{first}", Ct)).StatusCode);
        var accounts = (await Json(await c.GetAsync(P + "/accounts", Ct))).EnumerateArray().ToList();
        Assert.Single(accounts);
        Assert.Equal(second, accounts[0].GetProperty("id").GetString());
        Assert.True(accounts[0].GetProperty("messageCount").GetInt32() > 0);
        Assert.Equal(3, (await Threads(c, P)).Count);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"{P}/accounts/{second}", Ct)).StatusCode);
        Assert.Empty(await Threads(c, P));
        Assert.Empty(await Threads(c, P, "?archived=true"));
    }

    // ---- rules (decision 11) ----

    private static object RuleBody(string field, string value, string[]? tags = null, bool archive = false, bool markRead = false, string name = "Rule") => new
    {
        name,
        conditions = new[] { new { field, value } },
        addTags = tags ?? [],
        archive,
        markRead,
    };

    private static IEnumerable<string?> TagsOf(JsonElement thread) => thread.GetProperty("tags").EnumerateArray().Select(x => x.GetString());

    [Fact]
    public async Task Rules_ApplyToExisting_TagsReadAndArchive()
    {
        var c = Fresh();
        await SyncedAccountAsync(c, P);

        // "From or to" meets Ada's mail and my reply to her.
        var r = await c.PostAsJsonAsync(P + "/rules", RuleBody("anyone", "LOVELACE", ["Helvetia", "versicherungen"], name: "Ada"), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var rule = await Json(r);
        Assert.Equal(new[] { "helvetia", "versicherungen" }, rule.GetProperty("addTags").EnumerateArray().Select(x => x.GetString()));
        var applied = await Json(await c.PostAsync($"{P}/rules/{rule.GetProperty("id").GetString()}/apply", null, Ct));
        Assert.Equal(1, applied.GetProperty("conversations").GetInt32());

        var threads = await Threads(c, P);
        var engine = threads.Single(t => t.GetProperty("subject").GetString() == "Analytical engine notes");
        Assert.Contains("helvetia", TagsOf(engine));
        Assert.Contains("versicherungen", TagsOf(engine));
        Assert.All(await Messages(c, P, engine.GetProperty("threadId").GetString()!),
            m => Assert.Contains("helvetia", m.GetProperty("tags").EnumerateArray().Select(x => x.GetString())));
        Assert.DoesNotContain("helvetia", TagsOf(threads.Single(t => t.GetProperty("subject").GetString() == "This week in engines")));

        // Read + archive: the newsletter leaves the inbox, read, on the server too.
        var news = await Json(await c.PostAsJsonAsync(P + "/rules", RuleBody("from", "news@example", archive: true, markRead: true), Ct));
        var done = await Json(await c.PostAsync($"{P}/rules/{news.GetProperty("id").GetString()}/apply", null, Ct));
        Assert.Equal(1, done.GetProperty("archived").GetInt32());
        Assert.False(done.GetProperty("archiveFolderMissing").GetBoolean());
        Assert.DoesNotContain(await Threads(c, P), t => t.GetProperty("subject").GetString() == "This week in engines");
        var archived = (await Threads(c, P, "?archived=true")).Single(t => t.GetProperty("subject").GetString() == "This week in engines");
        Assert.Equal(0, archived.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task Rules_MeetNewMail_OnSync_InAndOut()
    {
        var c = Fresh();
        // The rules are there before the mail: the sync applies them as it stores.
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync(P + "/rules", RuleBody("anyone", "ada@example.org", ["ada"], markRead: true), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync(P + "/rules", RuleBody("from", "billing", archive: true), Ct)).StatusCode);
        var off = await Json(await c.PostAsJsonAsync(P + "/rules", RuleBody("subject", "week", ["never"]), Ct));
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"{P}/rules/{off.GetProperty("id").GetString()}",
            new { name = "Off", enabled = false, conditions = new[] { new { field = "subject", value = "week" } }, addTags = new[] { "never" } }, Ct)).StatusCode);

        await SyncedAccountAsync(c, P);

        var threads = await Threads(c, P);
        var engine = threads.Single(t => t.GetProperty("subject").GetString() == "Analytical engine notes");
        // Every message of the conversation — Ada's and my reply to her — has the tag; read now.
        Assert.All(await Messages(c, P, engine.GetProperty("threadId").GetString()!),
            m => Assert.Contains("ada", m.GetProperty("tags").EnumerateArray().Select(x => x.GetString())));
        Assert.Equal(0, engine.GetProperty("unread").GetInt32());
        Assert.DoesNotContain("never", TagsOf(threads.Single(t => t.GetProperty("subject").GetString() == "This week in engines")));
        // Archived at the end of the pass, on the server first.
        Assert.DoesNotContain(threads, t => t.GetProperty("subject").GetString() == "Your invoice");
        Assert.Contains(await Threads(c, P, "?archived=true"), t => t.GetProperty("subject").GetString() == "Your invoice");

        // The next pass reads the server: still read, still archived.
        await SyncService.RunOnceAsync(Ct);
        Assert.Equal(1, (await Json(await c.GetAsync(P + "/unread-count", Ct))).GetProperty("unread").GetInt32());
        Assert.DoesNotContain(await Threads(c, P), t => t.GetProperty("subject").GetString() == "Your invoice");
    }

    [Fact]
    public async Task Rules_AreManagedByTheOwnerOrASpaceAdmin_ByCookie()
    {
        var c = Fresh();
        // What a rule must have.
        Assert.Equal("rule_incomplete", await ErrorOf(await c.PostAsJsonAsync(P + "/rules", RuleBody("from", "x"), Ct)));
        Assert.Equal("rule_incomplete", await ErrorOf(await c.PostAsJsonAsync(P + "/rules", RuleBody("body", "x", ["t"]), Ct)));
        Assert.Equal("invalid_tag", await ErrorOf(await c.PostAsJsonAsync(P + "/rules", RuleBody("from", "x", ["no spaces"]), Ct)));

        // A key reads them, never writes them.
        var key = await KeyAsync(Owner, ContextRef.User(Owner), "read:mail", "write:mail");
        Assert.Equal(HttpStatusCode.OK, (await key.GetAsync(P + "/rules", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await key.PostAsJsonAsync(P + "/rules", RuleBody("from", "x", ["t"]), Ct)).StatusCode);

        // In a space: everyone reads, an Admin writes, a Member doesn't.
        var (_, sp) = await SpaceAsync();
        var made = await As(Admin).PostAsJsonAsync(sp + "/rules", RuleBody("from", "x", ["t"]), Ct);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var id = (await Json(made)).GetProperty("id").GetString();
        Assert.Single((await Json(await As(Reader).GetAsync(sp + "/rules", Ct))).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Member).PostAsJsonAsync(sp + "/rules", RuleBody("from", "y", ["t"]), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Member).DeleteAsync($"{sp}/rules/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await As(Owner).DeleteAsync($"{sp}/rules/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As(Owner).DeleteAsync($"{sp}/rules/{id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task DeleteThreads_SeveralAtOnce_Everywhere()
    {
        var c = Fresh();
        await SyncedAccountAsync(c, P);
        var threads = await Threads(c, P);
        var ids = new[] { ThreadIdOf(threads, "Your invoice"), ThreadIdOf(threads, "This week in engines") };

        var r = await c.PostAsJsonAsync(P + "/threads/delete", new { threadIds = ids, mode = "everywhere" }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await Json(r)).GetProperty("deleted").GetInt32());
        Assert.Equal(new[] { "Analytical engine notes" }, (await Threads(c, P)).Select(t => t.GetProperty("subject").GetString()));
        // Gone from the server's inbox too: the next pass doesn't bring them back.
        await SyncService.RunOnceAsync(Ct);
        Assert.Single(await Threads(c, P));

        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync(P + "/threads/delete", new { threadIds = ids, mode = "fishbowl" }, Ct)).StatusCode);
        Assert.Equal("too_many_threads", await ErrorOf(await c.PostAsJsonAsync(P + "/threads/delete",
            new { threadIds = Enumerable.Range(0, 1001).Select(i => "t" + i), mode = "fishbowl" }, Ct)));
    }
}
