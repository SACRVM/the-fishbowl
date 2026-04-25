using System.Net;
using System.Net.Http.Json;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// Closes the coverage gap for /api/v1/todos. Team-nested todos are already
// covered in TeamsApiTests; this file targets the personal route.
public class TodosApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dataDir;
    private const string UserA = "todos_user_a";
    private const string UserB = "todos_user_b";

    public TodosApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_todos_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (dbDescriptor != null) services.Remove(dbDescriptor);
                services.AddSingleton<DatabaseFactory>(new DatabaseFactory(_dataDir));

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.AuthenticationScheme, _ => { });
            });
        });
    }

    private HttpRequestMessage Req(HttpMethod method, string path, string? user, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (user is not null) req.Headers.Add(TestAuthHandler.UserIdHeader, user);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private async Task<TodoItem> CreateAsync(HttpClient client, string user, string title, DateTime? dueAt = null)
    {
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/todos", user, new TodoItem { Title = title, DueAt = dueAt }),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var item = await resp.Content.ReadFromJsonAsync<TodoItem>(TestContext.Current.CancellationToken);
        Assert.NotNull(item);
        return item!;
    }

    [Fact]
    public async Task TodosApi_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/todos",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Create_Returns201_WithLocationHeader()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/todos", UserA, new TodoItem { Title = "buy milk" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.NotNull(resp.Headers.Location);
        Assert.StartsWith("/api/v1/todos/", resp.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task List_ReturnsCreatedItems_AndHidesCompletedByDefault()
    {
        var client = _factory.CreateClient();
        await CreateAsync(client, UserA, "alpha");
        var beta = await CreateAsync(client, UserA, "beta");

        // Complete 'beta' via PUT. TodoItem.CompletedAt distinguishes open vs
        // done; UpdateAsync persists the field.
        beta.CompletedAt = DateTime.UtcNow;
        var upd = await client.SendAsync(
            Req(HttpMethod.Put, $"/api/v1/todos/{beta.Id}", UserA, beta),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, upd.StatusCode);

        // Default list (includeCompleted=false) hides beta.
        var list = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/todos", UserA),
            TestContext.Current.CancellationToken);
        list.EnsureSuccessStatusCode();
        var open = await list.Content.ReadFromJsonAsync<List<TodoItem>>(
            TestContext.Current.CancellationToken);
        Assert.Single(open!);
        Assert.Equal("alpha", open![0].Title);

        // includeCompleted=true shows both.
        var listAll = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/todos?includeCompleted=true", UserA),
            TestContext.Current.CancellationToken);
        listAll.EnsureSuccessStatusCode();
        var all = await listAll.Content.ReadFromJsonAsync<List<TodoItem>>(
            TestContext.Current.CancellationToken);
        Assert.Equal(2, all!.Count);
    }

    [Fact]
    public async Task GetById_Returns404_WhenMissing()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Get, "/api/v1/todos/01HZ_NOT_A_REAL_ULID", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetById_Returns200_WhenPresent()
    {
        var client = _factory.CreateClient();
        var created = await CreateAsync(client, UserA, "single");

        var resp = await client.SendAsync(
            Req(HttpMethod.Get, $"/api/v1/todos/{created.Id}", UserA),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var fetched = await resp.Content.ReadFromJsonAsync<TodoItem>(
            TestContext.Current.CancellationToken);
        Assert.Equal(created.Id, fetched!.Id);
        Assert.Equal("single", fetched.Title);
    }

    [Fact]
    public async Task Update_ChangesTitle_PersistsAcrossGet()
    {
        var client = _factory.CreateClient();
        var created = await CreateAsync(client, UserA, "original");

        created.Title = "rewritten";
        var put = await client.SendAsync(
            Req(HttpMethod.Put, $"/api/v1/todos/{created.Id}", UserA, created),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var fetched = await client.SendAsync(
            Req(HttpMethod.Get, $"/api/v1/todos/{created.Id}", UserA),
            TestContext.Current.CancellationToken);
        var item = await fetched.Content.ReadFromJsonAsync<TodoItem>(
            TestContext.Current.CancellationToken);
        Assert.Equal("rewritten", item!.Title);
    }

    [Fact]
    public async Task Update_Returns404_ForMissingId()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Put, "/api/v1/todos/01HZ_GHOST", UserA,
                new TodoItem { Title = "ghost" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_Returns204_AndItemGone()
    {
        var client = _factory.CreateClient();
        var created = await CreateAsync(client, UserA, "short-lived");

        var del = await client.SendAsync(
            Req(HttpMethod.Delete, $"/api/v1/todos/{created.Id}", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await client.SendAsync(
            Req(HttpMethod.Get, $"/api/v1/todos/{created.Id}", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Delete_Returns404_ForMissingId()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Delete, "/api/v1/todos/01HZ_GHOST", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Isolation_BetweenUsers()
    {
        var client = _factory.CreateClient();
        await CreateAsync(client, UserA, "a-todo");
        await CreateAsync(client, UserB, "b-todo");

        var aResp = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/todos", UserA),
            TestContext.Current.CancellationToken);
        var aList = await aResp.Content.ReadFromJsonAsync<List<TodoItem>>(
            TestContext.Current.CancellationToken);
        Assert.Single(aList!);
        Assert.Equal("a-todo", aList![0].Title);

        var bResp = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/todos", UserB),
            TestContext.Current.CancellationToken);
        var bList = await bResp.Content.ReadFromJsonAsync<List<TodoItem>>(
            TestContext.Current.CancellationToken);
        Assert.Single(bList!);
        Assert.Equal("b-todo", bList![0].Title);
    }

    [Fact]
    public async Task UserA_Cannot_Access_UserB_TodoById()
    {
        var client = _factory.CreateClient();
        var bItem = await CreateAsync(client, UserB, "b-only");

        // UserA probes UserB's todo by its exact id — must be 404 (not 403,
        // not data leak).
        var resp = await client.SendAsync(
            Req(HttpMethod.Get, $"/api/v1/todos/{bItem.Id}", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task CreateTodo_OversizedTitle_413()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/todos", UserA,
            new TodoItem
            {
                Title = new string('x', Fishbowl.Core.Util.TodoLimits.MaxTitleLength + 1),
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("title", body);
        Assert.Contains("todo", body);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }
}
