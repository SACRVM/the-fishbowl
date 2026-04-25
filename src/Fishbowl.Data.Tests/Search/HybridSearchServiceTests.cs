using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Search;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Fishbowl.Data.Search;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Search;

// Integration-ish tests against a real SQLite DB (user-scoped context).
// The embedding service is a deterministic Fake so the ranking logic is
// tested without a model download; semantic parity with MiniLM-L6-v2 is
// exercised separately in Fishbowl.Search.Tests.
public class HybridSearchServiceTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;
    private readonly ContextRef _ctx;
    private const string UserId = "hybrid_search_user";

    public HybridSearchServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_hybrid_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
        _ctx = ContextRef.User(UserId);
    }

    [Fact]
    public async Task HybridSearch_Empty_Query_ReturnsNoHits()
    {
        var svc = BuildService(new ScriptedEmbeddingService());
        var result = await svc.HybridSearchAsync(_ctx, "", 10, true,
            TestContext.Current.CancellationToken);
        Assert.Empty(result.Hits);
        Assert.False(result.Degraded);
    }

    [Fact]
    public async Task HybridSearch_LexicalMatch_RankedFirst()
    {
        var fake = new ScriptedEmbeddingService();
        var repo = BuildRepo(fake);
        await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "DatabaseFactory refactor", Content = "v3 migration details" },
            TestContext.Current.CancellationToken);
        await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "lunch menu", Content = "salad and soup" },
            TestContext.Current.CancellationToken);

        var svc = BuildService(fake);
        var result = await svc.HybridSearchAsync(_ctx, "DatabaseFactory", 5, true,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Hits);
        Assert.Equal("DatabaseFactory refactor", result.Hits[0].Note.Title);
    }

    [Fact]
    public async Task HybridSearch_SemanticMatch_RanksInTop5()
    {
        // Fake maps "how do migrations work" close to "Lazy migration
        // pattern" even though there's zero lexical overlap — the note
        // must still surface in the top 5 purely on the semantic side.
        var fake = new ScriptedEmbeddingService();
        fake.Map("how do migrations work", new[] { 1.0f, 0, 0 });
        fake.Map("DatabaseFactory refactor v3 migration details", new[] { 0.3f, 0.9f, 0 });
        fake.Map("Lazy migration pattern semantic-only hit", new[] { 0.95f, 0.3f, 0 });  // close to query
        fake.Map("lunch menu salad and soup", new[] { 0, 0, 1.0f });

        var repo = BuildRepo(fake);
        await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "DatabaseFactory refactor", Content = "v3 migration details" },
            TestContext.Current.CancellationToken);
        await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "Lazy migration pattern", Content = "semantic-only hit" },
            TestContext.Current.CancellationToken);
        await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "lunch menu", Content = "salad and soup" },
            TestContext.Current.CancellationToken);

        // Add more notes to reach a >5 baseline so "top 5" is meaningful.
        for (var i = 0; i < 17; i++)
        {
            var t = $"filler note {i}";
            fake.Map(t, new[] { 0, 0, 0.1f });
            await repo.CreateAsync(_ctx, UserId,
                new Note { Title = t, Content = "" },
                TestContext.Current.CancellationToken);
        }

        var svc = BuildService(fake);
        var result = await svc.HybridSearchAsync(_ctx, "how do migrations work", 5, true,
            TestContext.Current.CancellationToken);

        var topIds = result.Hits.Select(h => h.Note.Title).ToList();
        Assert.Contains("Lazy migration pattern", topIds);
    }

    [Fact]
    public async Task HybridSearch_WhenEmbeddingUnavailable_DegradesToFtsOnly()
    {
        var throwing = new ThrowingEmbeddingService();
        var repo = BuildRepo(throwing);
        await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "unique-term-alpha", Content = "body" },
            TestContext.Current.CancellationToken);

        var svc = BuildService(throwing);
        var result = await svc.HybridSearchAsync(_ctx, "unique-term-alpha", 5, true,
            TestContext.Current.CancellationToken);

        Assert.True(result.Degraded, "should flag degraded when embedding unavailable");
        Assert.NotEmpty(result.Hits);
        Assert.Equal("unique-term-alpha", result.Hits[0].Note.Title);
    }

    [Fact]
    public async Task HybridSearch_TruncatesOversizedQuery_StillReturnsHits()
    {
        // A pasted megabyte shouldn't 500 the embedding pipeline. Confirm
        // the service silently truncates and still returns sensible results.
        var fake = new ScriptedEmbeddingService();
        var repo = BuildRepo(fake);
        await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "needle", Content = "body" },
            TestContext.Current.CancellationToken);

        var bigQuery = "needle " + new string('x', HybridSearchService.MaxQueryLength * 4);

        var svc = BuildService(fake);
        var result = await svc.HybridSearchAsync(_ctx, bigQuery, 5, true,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Hits);
        Assert.Equal("needle", result.Hits[0].Note.Title);
    }

    [Fact]
    public async Task HybridSearch_ExcludesArchivedNotes()
    {
        var fake = new ScriptedEmbeddingService();
        var repo = BuildRepo(fake);
        var archivedId = await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "archived-needle", Content = "body", Archived = true },
            TestContext.Current.CancellationToken);

        // Ensure the archive flag persists (CreateAsync writes it).
        using (var db = _factory.CreateContextConnection(_ctx))
        {
            var archived = await db.ExecuteScalarAsync<long>(
                "SELECT archived FROM notes WHERE id = @id", new { id = archivedId });
            Assert.Equal(1, archived);
        }

        var svc = BuildService(fake);
        var result = await svc.HybridSearchAsync(_ctx, "archived-needle", 5, true,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Hits, h => h.Note.Id == archivedId);
    }

    [Fact]
    public async Task HybridSearch_FiltersPending_WhenIncludePendingFalse()
    {
        var fake = new ScriptedEmbeddingService();
        var repo = BuildRepo(fake);

        // Mcp source auto-adds review:pending — this is the same shape
        // pending notes have in production.
        await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "pending-note", Content = "secret alpha" },
            NoteSource.Mcp, TestContext.Current.CancellationToken);
        await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "approved-note", Content = "secret alpha" },
            NoteSource.Human, TestContext.Current.CancellationToken);

        var svc = BuildService(fake);
        var includePending = await svc.HybridSearchAsync(_ctx, "alpha", 10, true,
            TestContext.Current.CancellationToken);
        var excludePending = await svc.HybridSearchAsync(_ctx, "alpha", 10, false,
            TestContext.Current.CancellationToken);

        Assert.Contains(includePending.Hits, h => h.Note.Title == "pending-note");
        Assert.DoesNotContain(excludePending.Hits, h => h.Note.Title == "pending-note");
        Assert.Contains(excludePending.Hits, h => h.Note.Title == "approved-note");
    }

    private HybridSearchService BuildService(IEmbeddingService embeddings)
        => new(_factory, embeddings);

    private NoteRepository BuildRepo(IEmbeddingService embeddings)
        => new(_factory, new TagRepository(_factory), embeddings);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }

    // ────────── Test doubles ──────────
    //
    // ScriptedEmbeddingService: returns a prewired vector for specific
    // inputs, else a zeroed 384-dim vector. The first three dims are
    // hand-crafted; the rest stay 0 so cosine similarity reduces to the
    // inner product of the first three components — which is what the
    // individual tests actually reason about.
    private sealed class ScriptedEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 384;
        private readonly Dictionary<string, float[]> _map = new();

        public void Map(string text, float[] leadingDims)
        {
            _map[text] = leadingDims;
        }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var vec = new float[Dimensions];
            if (_map.TryGetValue(text, out var leading))
            {
                for (var i = 0; i < Math.Min(leading.Length, Dimensions); i++)
                    vec[i] = leading[i];
            }
            return Task.FromResult(vec);
        }
    }

    private sealed class ThrowingEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 384;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromException<float[]>(new EmbeddingUnavailableException("model not ready"));
    }
}
