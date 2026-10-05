using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// A space's settings over the wire: the list carries appMessageText like the
// single GET, and a PATCH value of the wrong type is refused, not read as
// "clear it".
public class SpaceSettingsPatchTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Owner = "settings_owner";
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dir;

    public SpaceSettingsPatchTests(WebApplicationFactory<Program> factory)
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_space_settings_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        var db = new DatabaseFactory(_dir);
        using (var sys = db.CreateSystemConnection())
            sys.Execute("INSERT INTO users(id, name, email, created_at) VALUES (@id, 'O', 'o@o', @now)",
                new { id = Owner, now = DateTime.UtcNow.ToString("o") });

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton(db);
                services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    o.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    o.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.AuthenticationScheme, _ => { });
            });
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, Owner);
        return c;
    }

    private static Task<HttpResponseMessage> Patch(HttpClient c, string slug, string json) =>
        c.PatchAsync($"/api/v1/spaces/{slug}", new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    [Fact]
    public async Task List_CarriesAppMessageText()
    {
        var c = Client();
        await c.PostAsJsonAsync("/api/v1/spaces", new { name = "Chatty" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, (await Patch(c, "chatty", "{\"appMessageText\":true}")).StatusCode);

        var list = await c.GetFromJsonAsync<JsonElement>("/api/v1/spaces", Ct);

        Assert.Contains(list.EnumerateArray(), s => s.GetProperty("slug").GetString() == "chatty"
                                                  && s.GetProperty("appMessageText").GetBoolean());
    }

    [Theory]
    [InlineData("{\"color\":5}")]
    [InlineData("{\"color\":true}")]
    [InlineData("{\"color\":{}}")]
    [InlineData("{\"appMessageText\":\"yes\"}")]
    public async Task Patch_AValueOfTheWrongType_Is400_AndChangesNothing(string json)
    {
        var c = Client();
        await c.PostAsJsonAsync("/api/v1/spaces", new { name = "Painted" }, Ct);
        await Patch(c, "painted", "{\"color\":\"teal\"}");

        var resp = await Patch(c, "painted", json);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_value", (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        var space = await c.GetFromJsonAsync<JsonElement>("/api/v1/spaces/painted", Ct);
        Assert.Equal("teal", space.GetProperty("color").GetString());
    }
}
