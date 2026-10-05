using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Search;
using Fishbowl.Core.Tables;
using Fishbowl.Data.Repositories;
using Fishbowl.Data.Tables;
using Microsoft.Data.Sqlite;

namespace Fishbowl.Data.Tests.Tables;

// row_search in one table: its candidates come from that table's rows, not
// from a space-wide pool that other tables can fill on their own.
public class TableRowSearchTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _db;
    private readonly ContextRef _space = ContextRef.Space("01JSEARCHSPACE00000000000A");
    private static readonly TableActor Owner = new("search_owner", CanWrite: true, CanDesign: true);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TableRowSearchTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_rowsearch_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _db = new DatabaseFactory(_dataDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private static TableDef Def(string name) => new(name, null, false,
        new[] { new ColumnDef("body", ColumnKind.Text, Searchable: true) }, DateTime.UtcNow, null);

    private static JsonElement Row(string title, string body) =>
        JsonSerializer.SerializeToElement(new { title, body });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchInOneTable_FindsItsRows_WhenOtherTablesFillThePool(bool withVectors)
    {
        var repo = new TableRepository(_db, new SpaceRepository(_db), withVectors ? new HashEmbedder() : null);
        await repo.CreateAsync(_space, Owner, Def("crowd"), Ct);
        await repo.CreateAsync(_space, Owner, Def("few"), Ct);
        // Short rows full of the word rank first space-wide — more than the
        // 50 candidates a search takes.
        for (var i = 0; i < 60; i++) await repo.InsertAsync(_space, Owner, "crowd", Row("apple apple apple", "apple"), Ct);
        for (var i = 0; i < 3; i++)
            await repo.InsertAsync(_space, Owner, "few", Row($"Orchard {i}", "a long note about one apple tree in the garden behind the old house"), Ct);

        var (hits, degraded) = await repo.SearchAsync(_space, "apple", "few", 10, Ct);
        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.Equal("few", h.Table));
        Assert.Equal(!withVectors, degraded);

        var (all, _) = await repo.SearchAsync(_space, "apple", null, 10, Ct);
        Assert.Equal(10, all.Count);
    }

    // Deterministic, no model: the text's SHA-256 spread over 384 floats.
    private sealed class HashEmbedder : IEmbeddingService
    {
        public int Dimensions => 384;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
            var vec = new float[Dimensions];
            for (var i = 0; i < Dimensions; i++) vec[i] = (float)(bytes[i % bytes.Length] / 128.0 - 1.0);
            return Task.FromResult(vec);
        }
    }
}
