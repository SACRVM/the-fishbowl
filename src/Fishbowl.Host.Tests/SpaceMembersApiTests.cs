using System.Net;
using System.Net.Http.Json;
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

// Space roles as a staircase, member management and invitation links
// (space-apps spec, phase 1).
public class SpaceMembersApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string Owner = "members_owner";
    private const string Admin = "members_admin";
    private const string Member = "members_member";
    private const string Newbie = "members_newbie";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SpaceMembersApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_members_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _db = new DatabaseFactory(_dataDir);
        using (var db = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[] { Owner, Admin, Member, Newbie }.Select(id => new { id, n = "Name " + id, e = id + "@example.com", now }));
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

    private HttpClient As(string? user)
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (user is not null) c.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, user);
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement;

    private static async Task<string?> ErrorOf(HttpResponseMessage r) =>
        (await Json(r)).TryGetProperty("error", out var e) ? e.GetString() : null;

    private async Task<Space> SpaceWithTeamAsync()
    {
        var repo = new SpaceRepository(_db);
        var space = await repo.CreateAsync(Owner, "Club " + Guid.NewGuid().ToString("N")[..6], Ct);
        await repo.AddMemberAsync(space.Id, Admin, SpaceRole.Admin, Ct);
        await repo.AddMemberAsync(space.Id, Member, SpaceRole.Member, Ct);
        return space;
    }

    private async Task<string> InviteUrlAsync(string slug, string by, string role)
    {
        var r = await As(by).PostAsJsonAsync($"/api/v1/spaces/{slug}/invites", new { role }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("url").GetString()!;
    }

    [Fact]
    public async Task Members_SeeEachOther_WithoutEmail_OwnerFirst()
    {
        var space = await SpaceWithTeamAsync();
        var r = await As(Member).GetAsync($"/api/v1/spaces/{space.Slug}/members", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Json(r);
        Assert.False(body.GetProperty("canManage").GetBoolean());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { "owner", "admin", "member" }, items.Select(i => i.GetProperty("role").GetString()));
        Assert.All(items, i => Assert.False(i.TryGetProperty("email", out _)));
        Assert.True(items[2].GetProperty("you").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Newbie).GetAsync($"/api/v1/spaces/{space.Slug}/members", Ct)).StatusCode);
    }

    [Fact]
    public async Task Roles_AreAStaircase()
    {
        var space = await SpaceWithTeamAsync();
        var url = $"/api/v1/spaces/{space.Slug}/members/";

        // A member manages nobody.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await As(Member).PatchAsJsonAsync(url + Admin, new { role = "reader" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await As(Member).GetAsync($"/api/v1/spaces/{space.Slug}/invites", Ct)).StatusCode);

        // An admin hands out up to admin, never owner, never touches the owner.
        var ok = await As(Admin).PatchAsJsonAsync(url + Member, new { role = "designer" }, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(SpaceRole.Designer, await new SpaceRepository(_db).GetMembershipAsync(space.Id, Member, Ct));
        Assert.Equal("role_invalid", await ErrorOf(await As(Admin).PatchAsJsonAsync(url + Member, new { role = "owner" }, Ct)));
        Assert.Equal("role_invalid", await ErrorOf(await As(Admin).PatchAsJsonAsync(url + Member, new { role = "boss" }, Ct)));
        Assert.Equal("member_above_you", await ErrorOf(await As(Admin).PatchAsJsonAsync(url + Owner, new { role = "reader" }, Ct)));
        Assert.Equal("member_self", await ErrorOf(await As(Admin).PatchAsJsonAsync(url + Admin, new { role = "reader" }, Ct)));
        Assert.Equal("member_missing", await ErrorOf(await As(Admin).PatchAsJsonAsync(url + Newbie, new { role = "reader" }, Ct)));

        // A designer can write but not invite.
        var designer = await As(Member).PostAsJsonAsync($"/api/v1/spaces/{space.Slug}/invites", new { role = "reader" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, designer.StatusCode);

        // Removing: the admin removes the designer; the owner can't leave.
        Assert.Equal(HttpStatusCode.NoContent, (await As(Admin).DeleteAsync(url + Member, Ct)).StatusCode);
        Assert.Null(await new SpaceRepository(_db).GetMembershipAsync(space.Id, Member, Ct));
        Assert.Equal("member_above_you", await ErrorOf(await As(Admin).DeleteAsync(url + Owner, Ct)));
        Assert.Equal("owner_cannot_leave", await ErrorOf(await As(Owner).DeleteAsync(url + Owner, Ct)));
        // Anyone else may leave.
        Assert.Equal(HttpStatusCode.NoContent, (await As(Admin).DeleteAsync(url + Admin, Ct)).StatusCode);
    }

    [Fact]
    public async Task InvitationLink_SignedIn_JoinsOnce()
    {
        var space = await SpaceWithTeamAsync();
        Assert.Equal("role_invalid", await ErrorOf(await As(Admin).PostAsJsonAsync(
            $"/api/v1/spaces/{space.Slug}/invites", new { role = "owner" }, Ct)));
        Assert.Equal("invite_days", await ErrorOf(await As(Admin).PostAsJsonAsync(
            $"/api/v1/spaces/{space.Slug}/invites", new { role = "member", days = 31 }, Ct)));

        var link = await InviteUrlAsync(space.Slug, Admin, "reader");
        var path = new Uri(link).PathAndQuery;
        Assert.StartsWith("/invite/", path);
        var listed = await Json(await As(Owner).GetAsync($"/api/v1/spaces/{space.Slug}/invites", Ct));
        Assert.Equal("reader", Assert.Single(listed.EnumerateArray()).GetProperty("role").GetString());

        var join = await As(Newbie).GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.Redirect, join.StatusCode);
        Assert.Equal($"/#/space/{space.Slug}/notes", join.Headers.Location!.OriginalString);
        Assert.Equal(SpaceRole.Reader, await new SpaceRepository(_db).GetMembershipAsync(space.Id, Newbie, Ct));
        Assert.Empty((await Json(await As(Owner).GetAsync($"/api/v1/spaces/{space.Slug}/invites", Ct))).EnumerateArray());

        var again = await As(Member).GetAsync(path, Ct);
        Assert.Equal("/?invite=invite-used#/", again.Headers.Location!.OriginalString);
        Assert.Null(await new SpaceRepository(_db).GetMembershipAsync(space.Id, Newbie + "x", Ct));
    }

    [Fact]
    public async Task InvitationLink_SignedOut_RemembersTheToken_ForTheSignIn()
    {
        var space = await SpaceWithTeamAsync();
        var path = new Uri(await InviteUrlAsync(space.Slug, Owner, "member")).PathAndQuery;

        var r = await As(null).GetAsync(path, Ct);
        Assert.Equal("/login", r.Headers.Location!.OriginalString);
        var cookie = Assert.Single(r.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("fb_invite=" + path["/invite/".Length..], cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);

        // Revoked: the link now sends a visitor to the login page with the reason.
        var id = (await Json(await As(Owner).GetAsync($"/api/v1/spaces/{space.Slug}/invites", Ct)))[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await As(Owner).DeleteAsync($"/api/v1/spaces/{space.Slug}/invites/{id}", Ct)).StatusCode);
        var gone = await As(null).GetAsync(path, Ct);
        Assert.StartsWith("/login?authError=", gone.Headers.Location!.OriginalString);
    }
}
