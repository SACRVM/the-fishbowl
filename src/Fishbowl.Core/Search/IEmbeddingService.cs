namespace Fishbowl.Core.Search;

// Produces a fixed-dimensional vector from arbitrary text. Callers (note
// writes, hybrid search queries) assume the vector is L2-normalised so cosine
// similarity reduces to a dot product.
//
// Why in Fishbowl.Core and not Fishbowl.Search: NoteRepository (Fishbowl.Data)
// calls this on every write, but Data must not depend on Search — the
// dependency direction in CLAUDE.md is `Data/Search → Core`. Putting the
// contract here keeps both sides clean.
public interface IEmbeddingService
{
    // 384 for multilingual-e5-small. Exposed as a property so callers sizing
    // buffers don't have to hard-code it.
    int Dimensions { get; }

    // Which model the vectors come from. A workspace keeps it beside its
    // vectors and embeds everything again when it changes.
    string Model => "unknown";

    // A text that is searched: a note, a table row, a mail.
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    // What someone searches with. A model trained for search embeds the two
    // sides differently; one that isn't needs nothing else.
    Task<float[]> EmbedQueryAsync(string query, CancellationToken ct = default) => EmbedAsync(query, ct);

    // Several texts in one model run — what a backfill wants.
    async Task<IReadOnlyList<float[]>> EmbedManyAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var list = new List<float[]>(texts.Count);
        foreach (var t in texts) list.Add(await EmbedAsync(t, ct));
        return list;
    }
}

// Thrown when the embedding model hasn't finished downloading yet. Callers
// on the hot path (NoteRepository writes, HybridSearchService queries) catch
// this and gracefully degrade — the row gets indexed on the next re-index,
// the query falls back to FTS-only.
public class EmbeddingUnavailableException : Exception
{
    public EmbeddingUnavailableException(string message) : base(message) { }
    public EmbeddingUnavailableException(string message, Exception inner) : base(message, inner) { }
}
