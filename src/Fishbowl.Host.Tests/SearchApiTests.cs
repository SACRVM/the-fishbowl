using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Fishbowl.Host.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// Covers POST /api/v1/search/reindex. Bearer rejection is the most
// load-bearing assertion — we do not want API keys to trigger maintenance
// jobs on a user's DB.
public class SearchApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _dbFactory;
    private readonly ApiKeyRepository _keys;
    private readonly string _dataDir;
    private const string UserId = "search_api_user";

    public SearchApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_search_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        _dbFactory = new DatabaseFactory(_dataDir);
        _keys = new ApiKeyRepository(_dbFactory);

        using (var db = _dbFactory.CreateSystemConnection())
        {
            db.Execute(
                "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, 'U', 'u@u', @now)",
                new { id = UserId, now = DateTime.UtcNow.ToString("o") });
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
                        options.ForwardDefaultSelector = ctx =>
                        {
                            var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
                            if (auth is not null &&
                                auth.StartsWith("Bearer fb_", StringComparison.Ordinal))
                            {
                                return ApiKeyAuthenticationOptions.DefaultScheme;
                            }
                            return null;
                        };
                    });
            });
        });
    }

    [Fact]
    public async Task Reindex_CookieAuth_ReturnsProcessedCount()
    {
        // Seed a note so there's something to (re-)embed. No assertion on
        // count beyond "response shape is correct" — the embedding service
        // in tests may not have the model downloaded, in which case each
        // note is skipped without failing.
        var notes = new NoteRepository(_dbFactory, new TagRepository(_dbFactory));
        await notes.CreateAsync(UserId,
            new Note { Title = "reindexable", Content = "body" },
            TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", UserId);

        var resp = await client.PostAsync("/api/v1/search/reindex", content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<ReindexResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.True(body!.Processed >= 0);
        Assert.True(body.Failed >= 0);
    }

    [Fact]
    public async Task Reindex_BearerAuth_Returns403()
    {
        // Maintenance endpoints are cookie-only — an API key should never
        // be able to trigger re-embedding.
        var issued = await _keys.IssueAsync(UserId, ContextRef.User(UserId), "search-probe",
            new[] { "read:notes", "write:notes" }, TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", issued.RawToken);

        var resp = await client.PostAsync("/api/v1/search/reindex", content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Reindex_NoAuth_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/v1/search/reindex", content: null,
            TestContext.Current.CancellationToken);
        Assert.True(
            resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401 or 403 for unauthenticated call, got {(int)resp.StatusCode}");
    }

    // ────────── GET /api/v1/search (hybrid notes search) ──────────

    [Fact]
    public async Task Search_Cookie_ReturnsHitsAndDegradedFlag()
    {
        var notes = new NoteRepository(_dbFactory, new TagRepository(_dbFactory));
        await notes.CreateAsync(UserId,
            new Note { Title = "venue sound check notes", Content = "load-in at 3pm" },
            TestContext.Current.CancellationToken);
        await notes.CreateAsync(UserId,
            new Note { Title = "grocery list", Content = "eggs" },
            TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", UserId);

        var resp = await client.GetAsync("/api/v1/search/?q=venue",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotNull(body!.Notes);

        var titles = body.Notes!.Select(n => n.Title).ToHashSet();
        Assert.Contains("venue sound check notes", titles);
        Assert.DoesNotContain("grocery list", titles);

        // Degraded is a bool — always present in the envelope, even true
        // when the embedding model isn't downloaded (FTS-only fallback).
        Assert.IsType<bool>(body.Degraded);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmptyHits()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", UserId);

        var resp = await client.GetAsync("/api/v1/search/?q=",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Empty(body!.Notes ?? new());
    }

    [Fact]
    public async Task Search_TagFilter_AnyMode_FiltersResults()
    {
        var notes = new NoteRepository(_dbFactory, new TagRepository(_dbFactory));
        await notes.CreateAsync(UserId,
            new Note { Title = "alpha-a", Content = "shared body", Tags = new() { "project-a" } },
            TestContext.Current.CancellationToken);
        await notes.CreateAsync(UserId,
            new Note { Title = "alpha-b", Content = "shared body", Tags = new() { "project-b" } },
            TestContext.Current.CancellationToken);
        await notes.CreateAsync(UserId,
            new Note { Title = "alpha-c", Content = "shared body", Tags = new() { "other" } },
            TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", UserId);

        var resp = await client.GetAsync(
            "/api/v1/search/?q=shared&tag=project-a&tag=project-b",
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(
            TestContext.Current.CancellationToken);
        var titles = body!.Notes!.Select(n => n.Title).ToHashSet();
        Assert.Contains("alpha-a", titles);
        Assert.Contains("alpha-b", titles);
        Assert.DoesNotContain("alpha-c", titles);
    }

    [Fact]
    public async Task Search_TagFilter_AllMode_RequiresEvery()
    {
        var notes = new NoteRepository(_dbFactory, new TagRepository(_dbFactory));
        await notes.CreateAsync(UserId,
            new Note
            {
                Title = "both",
                Content = "shared body",
                Tags = new() { "project-a", "urgent" },
            },
            TestContext.Current.CancellationToken);
        await notes.CreateAsync(UserId,
            new Note
            {
                Title = "only-one",
                Content = "shared body",
                Tags = new() { "project-a" },
            },
            TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", UserId);

        var resp = await client.GetAsync(
            "/api/v1/search/?q=shared&tag=project-a&tag=urgent&match=all",
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(
            TestContext.Current.CancellationToken);
        var titles = body!.Notes!.Select(n => n.Title).ToHashSet();
        Assert.Contains("both", titles);
        Assert.DoesNotContain("only-one", titles);
    }

    [Fact]
    public async Task Search_BearerWithoutReadNotesScope_Returns403()
    {
        var issued = await _keys.IssueAsync(UserId, ContextRef.User(UserId), "search-ro",
            new[] { "read:contacts" }, TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", issued.RawToken);

        var resp = await client.GetAsync("/api/v1/search/?q=x",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private record SearchHit(string Id, string Title, string? Content, List<string> Tags,
        DateTime CreatedAt, DateTime UpdatedAt, bool Pinned, bool Archived, double Score);
    private record SearchResponse(List<SearchHit>? Notes, bool Degraded);

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }

    private record ReindexResponse(int Processed, int Failed);
}
