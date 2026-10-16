using Fishbowl.Core.Models;

namespace Fishbowl.Core.Search;

// Hybrid (semantic + FTS) note search. Implementations live in
// Fishbowl.Data.Search; callers (MCP SearchMemoryTool, future UI search)
// only need this contract. Secrets are stripped by the implementation
// before the result reaches the caller.
//
// Tag filtering: post-rank, not pre-candidate — pre-filtering would
// drop semantic recall before it had a chance to compete. With
// `match="any"` a hit needs at least one of the requested tags;
// `match="all"` needs every one. Empty/null tags = no filter.
public interface ISearchService
{
    Task<HybridSearchResult> HybridSearchAsync(
        ContextRef ctx,
        string query,
        int limit,
        bool includePending,
        IReadOnlyCollection<string>? tags = null,
        string match = "any",
        CancellationToken ct = default);
}

// A single ranked hit. `Score` is the merged vec+FTS rank in [0, 1];
// useful for the UI to show relevance indicators but callers should treat
// the order of the list as authoritative. `Words`: every word of the query
// is in it — such hits come first, ahead of those found by meaning alone,
// so a lower score can stand above a higher one.
public record MemorySearchResult(Note Note, double Score, bool Words = false);

// Response envelope. `Degraded` is true when the embedding model wasn't
// ready and the ranking is FTS-only — UI can surface a "semantic search
// warming up" hint.
public record HybridSearchResult(IReadOnlyList<MemorySearchResult> Hits, bool Degraded);
