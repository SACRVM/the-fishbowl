using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
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

// Triggers run only for writes the table layer would take, on every table
// name, without swallowing what ctx.remove is refused or bending big whole
// numbers; a script that builds too much is refused, not the host. The
// trash keeps the tables' rules: a dropped table comes back (or goes for
// good) with the Designer role, another member's own-rows row not at all;
// a row restored into a re-typed column fits it or stays in the trash.
public class TableTriggerAndTrashRulesTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string Owner = "rules_owner";
    private const string Designer = "rules_designer";
    private const string Member = "rules_member";
    private const string Member2 = "rules_member2";
    private const string Reader = "rules_reader";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TableTriggerAndTrashRulesTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_table_rules_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _db = new DatabaseFactory(_dataDir);
        using (var db = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[] { Owner, Designer, Member, Member2, Reader }.Select(id => new { id, n = "Name " + id, e = id + "@example.com", now }));
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

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement;

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(Ct)}");
        return r.StatusCode == HttpStatusCode.NoContent ? default : await Json(r);
    }

    private static async Task<JsonElement> Refused(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.True(r.StatusCode == status, $"expected {(int)status} {code}, got {(int)r.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(code, body.GetProperty("error").GetString());
        return body;
    }

    private async Task<Space> ClubAsync()
    {
        var spaces = new SpaceRepository(_db);
        var space = await spaces.CreateAsync(Owner, "Rules " + Guid.NewGuid().ToString("N")[..6], Ct);
        await spaces.AddMemberAsync(space.Id, Designer, SpaceRole.Designer, Ct);
        await spaces.AddMemberAsync(space.Id, Member, SpaceRole.Member, Ct);
        await spaces.AddMemberAsync(space.Id, Member2, SpaceRole.Member, Ct);
        await spaces.AddMemberAsync(space.Id, Reader, SpaceRole.Reader, Ct);
        return space;
    }

    private void Trigger(Space space, string table, string code)
    {
        var dir = Path.Combine(_dataDir, "spaces", space.Id, "files", ".apps", "shop", "triggers");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, table + ".js"), code);
    }

    [Fact]
    public async Task Triggers_RunOnTablesNamedLikeSqlKeywords()
    {
        var space = await ClubAsync();
        var t = $"/api/v1/spaces/{space.Slug}/tables";
        await Ok(await As(Owner).PostAsync(t, Body("""{ "name": "order", "columns": [] }"""), Ct));
        Trigger(space, "order", "function beforeInsert(row) { throw new Error('checked'); }");

        var r = await Refused(await As(Member).PostAsync($"{t}/order/rows", Body("""{ "title": "x" }"""), Ct), HttpStatusCode.BadRequest, "trigger_refused");
        Assert.Equal("checked", r.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Triggers_RunOnlyForWritesThatPassTheRoleRowAndOwnRowChecks()
    {
        var space = await ClubAsync();
        var t = $"/api/v1/spaces/{space.Slug}/tables";
        await Ok(await As(Owner).PostAsync(t, Body("""{ "name": "shifts", "ownRows": true, "columns": [] }"""), Ct));
        Trigger(space, "shifts", """
            function beforeInsert(row, ctx) { if (row.title === 'probe') throw new Error('ran'); return row; }
            function beforeUpdate(row, old, ctx) { if (row.title === 'probe') throw new Error(old && old.id ? 'ran with old' : 'ran without old'); return row; }
            function beforeDelete(old, ctx) { return false; }
            """);
        var mine = (await Ok(await As(Member).PostAsync($"{t}/shifts/rows", Body("""{ "title": "a" }"""), Ct))).GetProperty("id").GetString();

        await Refused(await As(Reader).PostAsync($"{t}/shifts/rows", Body("""{ "title": "probe" }"""), Ct), HttpStatusCode.Forbidden, "role_write");
        await Refused(await As(Reader).DeleteAsync($"{t}/shifts/rows/{mine}", Ct), HttpStatusCode.Forbidden, "role_write");
        await Refused(await As(Member2).DeleteAsync($"{t}/shifts/rows/{mine}", Ct), HttpStatusCode.Forbidden, "not_your_row");
        await Refused(await As(Member2).PatchAsync($"{t}/shifts/rows/{mine}", Body("""{ "title": "probe" }"""), Ct), HttpStatusCode.Forbidden, "not_your_row");
        await Refused(await As(Member).PatchAsync($"{t}/shifts/rows/01JNOSUCHROW0000000000000A", Body("""{ "title": "probe" }"""), Ct), HttpStatusCode.NotFound, "row_missing");
        await Refused(await As(Member).PatchAsync($"{t}/shifts/rows/{mine}?rowVersion=99", Body("""{ "title": "probe" }"""), Ct), HttpStatusCode.Conflict, "version_conflict");

        var withOld = await Refused(await As(Member).PatchAsync($"{t}/shifts/rows/{mine}", Body("""{ "title": "probe" }"""), Ct), HttpStatusCode.BadRequest, "trigger_refused");
        Assert.Equal("ran with old", withOld.GetProperty("message").GetString());
        await Ok(await As(Member).DeleteAsync($"{t}/shifts/rows/{mine}", Ct));   // kept by beforeDelete
        await Ok(await As(Member).GetAsync($"{t}/shifts/rows/{mine}", Ct));
    }

    [Fact]
    public async Task CtxRemove_PassesItsRefusalOn()
    {
        var space = await ClubAsync();
        var t = $"/api/v1/spaces/{space.Slug}/tables";
        await Ok(await As(Owner).PostAsync(t, Body("""{ "name": "parents", "columns": [] }"""), Ct));
        await Ok(await As(Owner).PostAsync(t, Body("""{ "name": "kids", "columns": [ { "name": "parent", "type": "link", "link": "parents" } ] }"""), Ct));
        await Ok(await As(Owner).PostAsync(t, Body("""{ "name": "ops", "columns": [ { "name": "target", "type": "text" } ] }"""), Ct));
        Trigger(space, "ops", "function beforeInsert(row, ctx) { ctx.remove('parents', row.target); return row; }");
        var parent = (await Ok(await As(Member).PostAsync($"{t}/parents/rows", Body("""{ "title": "P" }"""), Ct))).GetProperty("id").GetString();
        await Ok(await As(Member).PostAsync($"{t}/kids/rows", Body($$"""{ "title": "K", "parent": "{{parent}}" }"""), Ct));

        await Refused(await As(Member).PostAsync($"{t}/ops/rows", Body($$"""{ "title": "x", "target": "{{parent}}" }"""), Ct), HttpStatusCode.Conflict, "still_linked");
        await Refused(await As(Member).PostAsync($"{t}/ops/rows", Body("""{ "title": "y", "target": "nope" }"""), Ct), HttpStatusCode.NotFound, "row_missing");
        Assert.Equal(0, (await Ok(await As(Owner).PostAsync($"{t}/ops/count", Body("{}"), Ct))).GetProperty("count").GetInt32());
        await Ok(await As(Owner).GetAsync($"{t}/parents/rows/{parent}", Ct));
    }

    [Fact]
    public async Task WholeNumbersBeyond2Pow53_PassABeforeTriggerExactly()
    {
        var space = await ClubAsync();
        var t = $"/api/v1/spaces/{space.Slug}/tables";
        await Ok(await As(Owner).PostAsync(t, Body("""{ "name": "nums", "columns": [ { "name": "big", "type": "integer" }, { "name": "tag", "type": "text" } ] }"""), Ct));
        Trigger(space, "nums", """
            function beforeInsert(row) { row.tag = 'seen'; return row; }
            function beforeUpdate(row) { row.tag = 'again'; }
            """);

        var row = await Ok(await As(Member).PostAsync($"{t}/nums/rows", Body("""{ "title": "n", "big": 9007199254740993 }"""), Ct));
        Assert.Equal("9007199254740993", row.GetProperty("big").GetRawText());
        Assert.Equal("seen", row.GetProperty("tag").GetString());
        var changed = await Ok(await As(Member).PatchAsync($"{t}/nums/rows/{row.GetProperty("id").GetString()}", Body("""{ "big": 9223372036854775807 }"""), Ct));
        Assert.Equal("9223372036854775807", changed.GetProperty("big").GetRawText());
        Assert.Equal("again", changed.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task ATriggerThatBuildsTooMuch_IsRefused()
    {
        var space = await ClubAsync();
        var t = $"/api/v1/spaces/{space.Slug}/tables";
        await Ok(await As(Owner).PostAsync(t, Body("""{ "name": "boom", "columns": [] }"""), Ct));
        Trigger(space, "boom", """
            function nest() { let o = {}; for (let i = 0; i < 100; i++) o = { a: o }; return o; }
            function beforeInsert(row) {
              if (row.title === 'deep') row.additional_data = nest();
              if (row.title === 'big') row.title = 'x'.repeat(5e7);
              if (row.title === 'json') row.title = String(JSON.stringify(nest()).length);
              return row;
            }
            """);

        var deep = await Refused(await As(Member).PostAsync($"{t}/boom/rows", Body("""{ "title": "deep" }"""), Ct), HttpStatusCode.BadRequest, "trigger_failed");
        Assert.Contains("levels deep", deep.GetProperty("message").GetString());
        var big = await Refused(await As(Member).PostAsync($"{t}/boom/rows", Body("""{ "title": "big" }"""), Ct), HttpStatusCode.BadRequest, "trigger_failed");
        Assert.Contains("too much memory", big.GetProperty("message").GetString());
        var json = await Ok(await As(Member).PostAsync($"{t}/boom/rows", Body("""{ "title": "json" }"""), Ct));
        Assert.Equal("602", json.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Trash_KeepsTheTablesRules()
    {
        var space = await ClubAsync();
        var t = $"/api/v1/spaces/{space.Slug}/tables";
        var trash = $"/api/v1/spaces/{space.Slug}/trash";
        await Ok(await As(Owner).PostAsync(t, Body("""{ "name": "plans", "columns": [] }"""), Ct));
        await Ok(await As(Owner).PostAsync(t, Body("""{ "name": "shifts", "ownRows": true, "columns": [] }"""), Ct));
        var theirs = (await Ok(await As(Member2).PostAsync($"{t}/shifts/rows", Body("""{ "title": "theirs" }"""), Ct))).GetProperty("id").GetString();
        var mine = (await Ok(await As(Member).PostAsync($"{t}/shifts/rows", Body("""{ "title": "mine" }"""), Ct))).GetProperty("id").GetString();
        await Ok(await As(Designer).DeleteAsync($"{t}/plans", Ct));
        await Ok(await As(Designer).DeleteAsync($"{t}/shifts/rows/{theirs}", Ct));
        await Ok(await As(Member).DeleteAsync($"{t}/shifts/rows/{mine}", Ct));

        async Task<string> TrashId(string itemId) =>
            (await Ok(await As(Owner).GetAsync(trash, Ct))).EnumerateArray().Single(i => i.GetProperty("itemId").GetString() == itemId).GetProperty("id").GetString()!;
        var plans = await TrashId("plans");
        var theirsInTrash = await TrashId(theirs!);

        // A dropped table: the Designer role, both ways.
        await Refused(await As(Member).PostAsync($"{trash}/{plans}/restore", null, Ct), HttpStatusCode.Forbidden, "role_design");
        await Refused(await As(Member).DeleteAsync($"{trash}/{plans}", Ct), HttpStatusCode.Forbidden, "role_design");
        // Another member's row of an own-rows table: theirs (or a Designer's).
        await Refused(await As(Member).PostAsync($"{trash}/{theirsInTrash}/restore", null, Ct), HttpStatusCode.Forbidden, "not_your_row");
        await Refused(await As(Member).DeleteAsync($"{trash}/{theirsInTrash}", Ct), HttpStatusCode.Forbidden, "not_your_row");

        // Emptying the trash below Designer leaves what the caller may not delete.
        Assert.Equal(1, (await Ok(await As(Member).DeleteAsync(trash, Ct))).GetProperty("deleted").GetInt32());
        var left = (await Ok(await As(Owner).GetAsync(trash, Ct))).EnumerateArray().Select(i => i.GetProperty("itemId").GetString()).ToList();
        Assert.Equal(new[] { "plans", theirs }.OrderBy(x => x), left.OrderBy(x => x));

        await Ok(await As(Member2).PostAsync($"{trash}/{theirsInTrash}/restore", null, Ct));
        await Ok(await As(Designer).PostAsync($"{trash}/{plans}/restore", null, Ct));
        await Ok(await As(Member).GetAsync($"{t}/plans", Ct));
    }

    [Fact]
    public async Task ARowRestoredIntoARetypedColumn_FitsItOrStaysInTheTrash()
    {
        var space = await ClubAsync();
        var t = $"/api/v1/spaces/{space.Slug}/tables";
        var trash = $"/api/v1/spaces/{space.Slug}/trash";
        await Ok(await As(Owner).PostAsync(t, Body("""{ "name": "codes", "columns": [ { "name": "code", "type": "text" }, { "name": "body", "type": "text" } ] }"""), Ct));
        var id = (await Ok(await As(Member).PostAsync($"{t}/codes/rows", Body("""{ "title": "r", "code": "abc", "body": "kept" }"""), Ct))).GetProperty("id").GetString();
        await Ok(await As(Member).DeleteAsync($"{t}/codes/rows/{id}", Ct));
        // Both columns dropped and added again: code as a number, body as long text.
        await Ok(await As(Owner).PatchAsync($"{t}/codes", Body("""{ "dropColumns": [ "code", "body" ] }"""), Ct));
        await Ok(await As(Owner).PatchAsync($"{t}/codes", Body("""{ "addColumns": [ { "name": "code", "type": "integer" }, { "name": "body", "type": "longtext" } ] }"""), Ct));
        var item = (await Ok(await As(Owner).GetAsync(trash, Ct))).EnumerateArray().Single(i => i.GetProperty("itemId").GetString() == id).GetProperty("id").GetString();

        await Refused(await As(Member).PostAsync($"{trash}/{item}/restore", null, Ct), HttpStatusCode.Conflict, "restore_conflict");
        await Ok(await As(Member).GetAsync($"{t}/codes/rows", Ct));   // the table still reads

        // Without the misfit column, the rest fits as it is now.
        await Ok(await As(Owner).PatchAsync($"{t}/codes", Body("""{ "dropColumns": [ "code" ] }"""), Ct));
        await Ok(await As(Member).PostAsync($"{trash}/{item}/restore", null, Ct));
        Assert.Equal("kept", (await Ok(await As(Member).GetAsync($"{t}/codes/rows/{id}", Ct))).GetProperty("body").GetString());
    }
}
