using System.Collections.Generic;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fishbowl.Host.Tests;

// A custom authentication handler that reads a header and turns it into a Claim
public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string AuthenticationScheme = "TestScheme";
    public const string UserIdHeader = "X-Test-User-Id";

    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserIdHeader, out var userId))
        {
            return Task.FromResult(AuthenticateResult.Fail("No User ID provided in test header"));
        }

        var claims = new[] {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("fishbowl_user_id", userId.ToString())
        };
        var identity = new ClaimsIdentity(claims, AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AuthenticationScheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private const string UserA = "user_a";
    private const string UserB = "user_b";

    private readonly string _testDataDir;

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "fishbowl_api_integration_tests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_testDataDir);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                // Remove the production DatabaseFactory
                var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (dbDescriptor != null) services.Remove(dbDescriptor);
                services.AddSingleton<DatabaseFactory>(new DatabaseFactory(_testDataDir));

                // Override Authentication to use our Test Scheme as the default
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.AuthenticationScheme, options => { });
            });
        });
    }

    [Fact]
    public async Task PostAndGet_IsolatesDataByUser_Test()
    {
        // Arrange
        var client = _factory.CreateClient();

        var noteA = new Note { Title = "User A Note" };
        var noteB = new Note { Title = "User B Note" };

        // Act 1: Post as User A
        var requestA = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notes");
        requestA.Headers.Add(TestAuthHandler.UserIdHeader, UserA);
        requestA.Content = JsonContent.Create(noteA);
        var responseA = await client.SendAsync(requestA, TestContext.Current.CancellationToken);
        Assert.True(responseA.IsSuccessStatusCode);

        // Act 2: Post as User B
        var requestB = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notes");
        requestB.Headers.Add(TestAuthHandler.UserIdHeader, UserB);
        requestB.Content = JsonContent.Create(noteB);
        var responseB = await client.SendAsync(requestB, TestContext.Current.CancellationToken);
        Assert.True(responseB.IsSuccessStatusCode);

        // Act 3: Get notes for User A
        var getRequestA = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notes");
        getRequestA.Headers.Add(TestAuthHandler.UserIdHeader, UserA);
        var getResponseA = await client.SendAsync(getRequestA, TestContext.Current.CancellationToken);
        var notesA = await getResponseA.Content.ReadFromJsonAsync<IEnumerable<Note>>(TestContext.Current.CancellationToken);

        // Act 4: Get notes for User B
        var getRequestB = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notes");
        getRequestB.Headers.Add(TestAuthHandler.UserIdHeader, UserB);
        var getResponseB = await client.SendAsync(getRequestB, TestContext.Current.CancellationToken);
        var notesB = await getResponseB.Content.ReadFromJsonAsync<IEnumerable<Note>>(TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(notesA!);
        Assert.Equal("User A Note", notesA!.First().Title);

        Assert.Single(notesB!);
        Assert.Equal("User B Note", notesB!.First().Title);
    }

    [Fact]
    public async Task Get_ReturnsUnauthorized_IfAuthenticatedUserMissing_Test()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        // Without the X-Test-User-Id header, TestAuthHandler will fail authentication
        var response = await client.GetAsync("/api/v1/notes", TestContext.Current.CancellationToken);

        // Assert
        // In Minimal APIs with RequireAuthorization, it returns 401 Unauthorized if using a direct API client
        // or redirects to /login if it thinks it's a browser.
        // Our TestAuthHandler returns Fail, which results in 401.
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_UnversionedPath_Returns404_Test()
    {
        var client = _factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/notes");
        request.Headers.Add(TestAuthHandler.UserIdHeader, UserA);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListNotes_PaginationParams_RestrictReturnedSlice_Test()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notes");
            req.Headers.Add(TestAuthHandler.UserIdHeader, UserA);
            req.Content = JsonContent.Create(new Note { Title = $"page-{i}" });
            (await client.SendAsync(req, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        }

        var pageReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notes?limit=2&offset=1");
        pageReq.Headers.Add(TestAuthHandler.UserIdHeader, UserA);
        var pageResp = await client.SendAsync(pageReq, TestContext.Current.CancellationToken);
        pageResp.EnsureSuccessStatusCode();

        var page = await pageResp.Content.ReadFromJsonAsync<List<Note>>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Equal(2, page!.Count);
        // Newest first → offset 1 starts at page-3, then page-2.
        Assert.Equal("page-3", page[0].Title);
        Assert.Equal("page-2", page[1].Title);
    }

    [Fact]
    public async Task Health_Anonymous_ReturnsOk()
    {
        // No X-Test-User-Id header. Liveness must be reachable from
        // unauthenticated probes (orchestrator health checks, monitors).
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/health",
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal("ok", body!["status"]);
        Assert.True(body.ContainsKey("time"));
    }

    [Fact]
    public async Task Get_Version_ReturnsVersionString_Test()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/version", TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.True(body!.ContainsKey("version"));
        Assert.False(string.IsNullOrEmpty(body["version"]));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }
}
