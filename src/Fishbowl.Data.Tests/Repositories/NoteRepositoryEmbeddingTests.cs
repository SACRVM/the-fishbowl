using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Search;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

// Covers the vec_notes sync contract added in Task 5.3:
//  * Create → writes a row with non-zero blob.
//  * Update → replaces the existing blob (so the new content drives vector).
//  * Delete → removes the row before the notes row disappears.
//  * EmbeddingUnavailableException is swallowed; the note write still lands.
//  * `::secret ... ::end` content stays out of the text handed to the model.
public class NoteRepositoryEmbeddingTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;
    private readonly ContextRef _ctx;
    private const string UserId = "emb_test_user";

    public NoteRepositoryEmbeddingTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_emb_note_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
        _ctx = ContextRef.User(UserId);
    }

    [Fact]
    public async Task CreateAsync_WithEmbeddingService_WritesVecNotesRow()
    {
        var fake = new FakeEmbeddingService();
        var repo = BuildRepo(fake);

        var id = await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "alpha", Content = "bravo charlie" },
            TestContext.Current.CancellationToken);

        Assert.NotNull(fake.LastText);
        Assert.Contains("alpha", fake.LastText);
        Assert.Contains("bravo charlie", fake.LastText);

        using var db = _factory.CreateContextConnection(_ctx);
        var blob = await db.ExecuteScalarAsync<byte[]?>(
            "SELECT embedding FROM vec_notes WHERE id = @id", new { id });
        Assert.NotNull(blob);
        Assert.Equal(fake.Dimensions * sizeof(float), blob!.Length);
        Assert.True(blob.Any(b => b != 0), "embedding blob should not be all-zero");
    }

    [Fact]
    public async Task UpdateAsync_ReplacesExistingEmbedding()
    {
        var fake = new FakeEmbeddingService();
        var repo = BuildRepo(fake);

        var id = await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "first", Content = "the original body" },
            TestContext.Current.CancellationToken);

        byte[] before;
        using (var db = _factory.CreateContextConnection(_ctx))
        {
            before = await db.ExecuteScalarAsync<byte[]?>(
                "SELECT embedding FROM vec_notes WHERE id = @id", new { id })
                ?? throw new InvalidOperationException("embedding blob missing before update");
        }

        // Pull the note back out and update with new content. Fake hashes
        // the input so different text produces a different blob.
        var note = await repo.GetByIdAsync(_ctx, id, TestContext.Current.CancellationToken);
        Assert.NotNull(note);
        note!.Content = "completely different body";
        await repo.UpdateAsync(_ctx, note, TestContext.Current.CancellationToken);

        // Diagnostic: Update should call EmbedAsync a second time.
        Assert.Equal(2, fake.CallCount);

        byte[] after;
        using (var db = _factory.CreateContextConnection(_ctx))
        {
            after = await db.ExecuteScalarAsync<byte[]?>(
                "SELECT embedding FROM vec_notes WHERE id = @id", new { id })
                ?? throw new InvalidOperationException("embedding blob missing after update");
        }

        Assert.False(before.AsSpan().SequenceEqual(after.AsSpan()),
            "embedding blob should change when content changes");
    }

    [Fact]
    public async Task DeleteAsync_RemovesVecNotesRow()
    {
        var fake = new FakeEmbeddingService();
        var repo = BuildRepo(fake);

        var id = await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "to-be-deleted", Content = "soon gone" },
            TestContext.Current.CancellationToken);

        await repo.DeleteAsync(_ctx, id, TestContext.Current.CancellationToken);

        using var db = _factory.CreateContextConnection(_ctx);
        var remaining = await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM vec_notes WHERE id = @id", new { id });
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task CreateAsync_SwallowsEmbeddingUnavailable_NoteStillLands()
    {
        // Degraded mode: model isn't ready yet. The note row, notes_fts row,
        // and tag rows must still commit; only vec_notes stays empty.
        var throwing = new ThrowingEmbeddingService();
        var repo = BuildRepo(throwing);

        var id = await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "degraded", Content = "model missing" },
            TestContext.Current.CancellationToken);

        var stored = await repo.GetByIdAsync(_ctx, id, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal("degraded", stored!.Title);

        using var db = _factory.CreateContextConnection(_ctx);
        var vecCount = await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM vec_notes WHERE id = @id", new { id });
        Assert.Equal(0, vecCount);
    }

    [Fact]
    public async Task CreateAsync_StripsSecretBlocksFromEmbeddingInput()
    {
        // Non-negotiable: the secret marker inside `::secret`…`::end` must
        // never reach the embedding model — otherwise a semantic query could
        // surface the note on the basis of the hidden text.
        var fake = new FakeEmbeddingService();
        var repo = BuildRepo(fake);

        await repo.CreateAsync(_ctx, UserId,
            new Note
            {
                Title = "public title",
                Content = "preamble\n::secret\nleak-marker-xyz123\n::end\ntail",
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(fake.LastText);
        Assert.DoesNotContain("leak-marker-xyz123", fake.LastText, StringComparison.Ordinal);
        Assert.Contains("preamble", fake.LastText);
        Assert.Contains("tail", fake.LastText);
    }

    [Fact]
    public async Task CreateAsync_StripsEncryptedSecretMarkerFromEmbeddingInput()
    {
        // Vault encryption (commit 71987ad) replaces inline ::secret blocks
        // with `::secret#N::end` markers and stores the ciphertext in
        // content_secret. The marker carries no plaintext, but feeding it
        // to the embedding model would still leak structural information
        // (every note with a marker clusters together in vector space).
        // Strip it for the same reason the MCP wire strips it.
        var fake = new FakeEmbeddingService();
        var repo = BuildRepo(fake);

        await repo.CreateAsync(_ctx, UserId,
            new Note
            {
                Title = "title",
                Content = "before\n::secret#0::end\nbetween\n::secret#1::end\nafter",
                ContentSecret = new byte[] { 1, 2, 3 },
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(fake.LastText);
        Assert.DoesNotContain("::secret", fake.LastText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("before", fake.LastText);
        Assert.Contains("after", fake.LastText);
    }

    [Fact]
    public async Task ReEmbedAllAsync_ProcessesEveryNote()
    {
        var fake = new FakeEmbeddingService();
        var repo = BuildRepo(fake);

        for (var i = 0; i < 5; i++)
        {
            await repo.CreateAsync(_ctx, UserId,
                new Note { Title = $"note-{i}", Content = $"body-{i}" },
                TestContext.Current.CancellationToken);
        }

        // Create wrote 5 vectors → CallCount already at 5. Re-embed bumps
        // it by another 5.
        var baseline = fake.CallCount;
        var result = await repo.ReEmbedAllAsync(_ctx, TestContext.Current.CancellationToken);

        Assert.Equal(5, result.Processed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(baseline + 5, fake.CallCount);

        using var db = _factory.CreateContextConnection(_ctx);
        var rowCount = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vec_notes");
        Assert.Equal(5, rowCount);
    }

    [Fact]
    public async Task ReEmbedAllAsync_WithoutEmbeddingService_IsNoop()
    {
        // Backward compat: repository constructed without an embedding
        // service returns a zeroed result instead of throwing.
        var repo = new NoteRepository(_factory, new TagRepository(_factory), embeddings: null);
        var result = await repo.ReEmbedAllAsync(_ctx, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task CreateAsync_WithoutEmbeddingService_StillSucceeds()
    {
        // Backward compat — cookie-auth call sites constructed before Task 5.3
        // landed don't pass an embedding service. Those writes must keep
        // working; vec_notes just stays empty.
        var repo = new NoteRepository(_factory, new TagRepository(_factory), embeddings: null);

        var id = await repo.CreateAsync(_ctx, UserId,
            new Note { Title = "no-embed", Content = "still works" },
            TestContext.Current.CancellationToken);

        using var db = _factory.CreateContextConnection(_ctx);
        var vecCount = await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM vec_notes WHERE id = @id", new { id });
        Assert.Equal(0, vecCount);
    }

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
    // FakeEmbeddingService: deterministic, cheap, no model download. Hashes
    // the input bytes into a 384-dim float vector so different texts produce
    // different blobs while the same text round-trips identically.
    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 384;
        public string? LastText { get; private set; }
        public int CallCount { get; private set; }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            LastText = text;
            CallCount++;

            // SHA-256 over the input gives us 32 bytes of cryptographically
            // distinct output; repeat to fill 384 dimensions so distinct
            // inputs definitely produce distinct vectors.
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text));
            var vec = new float[Dimensions];
            for (var i = 0; i < Dimensions; i++)
            {
                var b = bytes[i % bytes.Length];
                vec[i] = (float)(b / 128.0 - 1.0);
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
