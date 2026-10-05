using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Search;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

// The bulk re-embed runs the model with no transaction open: other writes to
// the context go through while it works, and a note changed meanwhile keeps
// the vector its own write gave it.
public class NoteReEmbedLockTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;
    private readonly ContextRef _ctx = ContextRef.User("reembed_lock_user");

    public NoteReEmbedLockTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_reembed_lock_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
    }

    [Fact]
    public async Task ReEmbedAll_LetsOtherWritesThrough_AndSkipsNotesChangedMeanwhile()
    {
        var ct = TestContext.Current.CancellationToken;
        var plain = new NoteRepository(_factory, new TagRepository(_factory));
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
            ids.Add(await plain.CreateAsync(_ctx, "u", new Note { Title = $"n{i}", Content = $"body {i}" }, ct));

        // While the model works on the first note, another request writes —
        // and changes the text of a note still waiting for its turn.
        var embeddings = new WritingEmbeddingService(() =>
        {
            using var other = _factory.CreateContextConnection(_ctx);
            other.Execute(new CommandDefinition("UPDATE notes SET content = 'changed' WHERE id = @id",
                new { id = ids[2] }, commandTimeout: 2));
        });
        var repo = new NoteRepository(_factory, new TagRepository(_factory), embeddings);

        var result = await repo.ReEmbedAllAsync(_ctx, ct);

        Assert.Null(embeddings.WriteError);
        Assert.Equal(2, result.Processed);
        Assert.Equal(0, result.Failed);
        using var db = _factory.CreateContextConnection(_ctx);
        Assert.Equal(0, db.ExecuteScalar<long>("SELECT COUNT(*) FROM vec_notes WHERE id = @id", new { id = ids[2] }));
    }

    private sealed class WritingEmbeddingService(Action write) : IEmbeddingService
    {
        private bool _written;
        public Exception? WriteError { get; private set; }
        public int Dimensions => 384;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            if (!_written)
            {
                _written = true;
                try { write(); } catch (Exception ex) { WriteError = ex; }
            }
            var vec = new float[Dimensions];
            vec[0] = 1;
            return Task.FromResult(vec);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}
