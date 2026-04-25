using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Search;
using Fishbowl.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Search;

// Blends semantic (vec_notes via sqlite-vec) with lexical (notes_fts via
// FTS5 bm25) into a single ranked list.
//
// Why hybrid: embeddings catch "how do migrations work" → a note titled
// "Lazy migration pattern" (semantic win, keyword miss); FTS catches
// specific identifiers the embedding smears over ("DatabaseFactoryV3"
// lexical win, semantic miss). 70/30 in favour of semantic reflects the
// CONCEPT — Fishbowl is memory-first, identifier search is a secondary
// affordance.
public sealed class HybridSearchService : ISearchService
{
    private const double VectorWeight = 0.7;
    private const double FtsWeight = 0.3;
    private const int CandidatePool = 50;

    // Hard cap on the query string. A real search query is short — anything
    // past a kilobyte is either a copy-paste accident, a misuse of the
    // endpoint as a fuzzy text-similarity API, or an attempt to overload
    // the embedding model / FTS5 tokenizer. Truncate (don't 400) so the
    // user gets *some* result back; log so the host operator notices.
    public const int MaxQueryLength = 1024;

    private readonly DatabaseFactory _factory;
    private readonly IEmbeddingService _embeddings;
    private readonly ILogger<HybridSearchService> _logger;

    public HybridSearchService(
        DatabaseFactory factory,
        IEmbeddingService embeddings,
        ILogger<HybridSearchService>? logger = null)
    {
        _factory = factory;
        _embeddings = embeddings;
        _logger = logger ?? NullLogger<HybridSearchService>.Instance;
    }

    public async Task<HybridSearchResult> HybridSearchAsync(
        ContextRef ctx, string query, int limit, bool includePending,
        IReadOnlyCollection<string>? tags = null,
        string match = "any",
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        // Normalise once outside the per-hit loop. "all" is the strict
        // mode; everything else (including null/empty/typo) collapses to
        // "any" — same lenient default as NoteRepository.GetAllAsync uses.
        var requireAll = string.Equals(match, "all", StringComparison.OrdinalIgnoreCase);
        var tagFilter = tags is { Count: > 0 } ? new HashSet<string>(tags, StringComparer.Ordinal) : null;
        query = query?.Trim() ?? string.Empty;
        if (query.Length > MaxQueryLength)
        {
            _logger.LogDebug(
                "Search query truncated from {OriginalLength} to {MaxQueryLength} chars",
                query.Length, MaxQueryLength);
            query = query.Substring(0, MaxQueryLength);
        }
        if (string.IsNullOrEmpty(query))
            return new HybridSearchResult(Array.Empty<MemorySearchResult>(), Degraded: false);

        using var db = _factory.CreateContextConnection(ctx);

        // Run FTS first — it's always available; vector search is optional.
        var ftsHits = await RunFtsAsync(db, query, ct);

        // Try the vector side. If the model isn't ready yet, degrade to
        // FTS-only and mark it so callers can surface the hint.
        List<(string Id, double Distance)> vecHits;
        bool degraded;
        try
        {
            var vec = await _embeddings.EmbedAsync(query, ct);
            vecHits = await RunVectorAsync(db, vec, ct);
            degraded = false;
        }
        catch (EmbeddingUnavailableException ex)
        {
            _logger.LogDebug(ex, "Embedding service not ready; falling back to FTS-only ranking");
            vecHits = new List<(string, double)>();
            degraded = true;
        }

        var merged = MergeScores(vecHits, ftsHits, degraded).ToList();

        // Pull the full notes for the top K (after de-dupe + ordering).
        // Larger window when tag-filtering — many candidates may be dropped
        // by the filter, and we want a fair shot at backfilling to `limit`.
        var fetchMultiplier = tagFilter is null ? 2 : 6;
        var topIds = merged.Select(m => m.Id).Take(limit * fetchMultiplier).ToList();
        var notesById = await LoadNotesAsync(db, topIds, ct);

        var results = new List<MemorySearchResult>(limit);
        foreach (var candidate in merged)
        {
            if (results.Count >= limit) break;
            if (!notesById.TryGetValue(candidate.Id, out var note)) continue;
            if (note.Archived) continue;
            if (!includePending && (note.Tags?.Contains("review:pending") ?? false)) continue;
            if (tagFilter is not null && !MatchesTagFilter(note.Tags, tagFilter, requireAll)) continue;

            results.Add(new MemorySearchResult(SecretStripper.StripNote(note), candidate.Score));
        }

        return new HybridSearchResult(results, degraded);
    }

    private static bool MatchesTagFilter(
        IReadOnlyList<string>? noteTags, HashSet<string> required, bool requireAll)
    {
        if (noteTags is null || noteTags.Count == 0) return false;
        if (requireAll)
        {
            foreach (var t in required)
                if (!noteTags.Contains(t)) return false;
            return true;
        }
        foreach (var t in noteTags)
            if (required.Contains(t)) return true;
        return false;
    }

    private static async Task<List<(string Id, double Bm25)>> RunFtsAsync(
        System.Data.IDbConnection db, string query, CancellationToken ct)
    {
        // FTS5's default tokenizer splits on non-alphanumeric. Matching the
        // same rule here keeps query behaviour aligned with how notes are
        // indexed — so "distinctive-search" finds "distinctive-search-target"
        // because both sides resolve to tokens `distinctive`, `search`.
        // Each token becomes a prefix match (`tok*`) and the group is AND'd
        // so every token must appear.
        var tokens = System.Text.RegularExpressions.Regex
            .Matches(query, @"\w+")
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Select(t => t + "*")
            .ToList();
        if (tokens.Count == 0) return new();
        var ftsQuery = string.Join(" AND ", tokens);

        const string sql = @"
            SELECT n.id AS Id, bm25(notes_fts) AS Bm25
            FROM notes_fts
            JOIN notes n ON n.rowid = notes_fts.rowid
            WHERE notes_fts MATCH @q
            ORDER BY bm25(notes_fts)
            LIMIT @limit";

        var rows = await db.QueryAsync<(string Id, double Bm25)>(
            new CommandDefinition(sql, new { q = ftsQuery, limit = CandidatePool }, cancellationToken: ct));
        return rows.ToList();
    }

    private static async Task<List<(string Id, double Distance)>> RunVectorAsync(
        System.Data.IDbConnection db, float[] queryVec, CancellationToken ct)
    {
        // sqlite-vec expects the query vector as a blob with the same float
        // layout the table stores.
        var blob = new byte[queryVec.Length * sizeof(float)];
        Buffer.BlockCopy(queryVec, 0, blob, 0, blob.Length);

        const string sql = @"
            SELECT id AS Id, distance AS Distance
            FROM vec_notes
            WHERE embedding MATCH @q AND k = @k
            ORDER BY distance";

        var rows = await db.QueryAsync<(string Id, double Distance)>(
            new CommandDefinition(sql, new { q = blob, k = CandidatePool }, cancellationToken: ct));
        return rows.ToList();
    }

    // Normalises both score lists to [0, 1] via min-max, then linearly
    // combines them. FTS bm25 is "lower is better" — flip sign before
    // normalising. Vec distance (cosine distance on L2-normalised vectors
    // ranges [0, 2]) is also "lower is better" — flip too.
    //
    // When degraded (no vec hits), FTS carries the full signal; its
    // effective weight becomes 1.0 so the absolute numeric scores stay
    // comparable to the hybrid case at the top of the ranking.
    private static IEnumerable<(string Id, double Score)> MergeScores(
        List<(string Id, double Distance)> vec,
        List<(string Id, double Bm25)> fts,
        bool degraded)
    {
        var vecScore = NormaliseAscending(vec.Select(v => (v.Id, v.Distance)));
        var ftsScore = NormaliseAscending(fts.Select(f => (f.Id, f.Bm25)));

        var ids = new HashSet<string>(vecScore.Keys);
        ids.UnionWith(ftsScore.Keys);

        var wVec = degraded ? 0.0 : VectorWeight;
        var wFts = degraded ? 1.0 : FtsWeight;

        return ids
            .Select(id =>
            {
                var v = vecScore.TryGetValue(id, out var vs) ? vs : 0.0;
                var f = ftsScore.TryGetValue(id, out var fs) ? fs : 0.0;
                return (Id: id, Score: wVec * v + wFts * f);
            })
            .OrderByDescending(x => x.Score);
    }

    // Given a list of (id, rawScore) where lower raw == better, returns
    // (id, normalised) where 1.0 is best and 0.0 is worst. Single-item or
    // all-equal lists collapse to 1.0 so any hit beats no-hit in the merge.
    private static Dictionary<string, double> NormaliseAscending(IEnumerable<(string Id, double Raw)> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return new();
        var min = list.Min(x => x.Raw);
        var max = list.Max(x => x.Raw);
        var span = max - min;
        if (span <= double.Epsilon)
        {
            return list.ToDictionary(x => x.Id, _ => 1.0);
        }
        return list.ToDictionary(x => x.Id, x => 1.0 - (x.Raw - min) / span);
    }

    private static async Task<Dictionary<string, Note>> LoadNotesAsync(
        System.Data.IDbConnection db, List<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();

        // Dapper's automatic IN-expansion doesn't fire for ExecuteScalar /
        // QueryAsync with an IEnumerable parameter under every connection
        // type — the safer path is to emit one placeholder per id. ULIDs
        // are fixed-length and alphanumeric, so there's nothing to escape.
        var sb = new System.Text.StringBuilder("SELECT * FROM notes WHERE id IN (");
        var parms = new DynamicParameters();
        for (var i = 0; i < ids.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("@p").Append(i);
            parms.Add($"p{i}", ids[i]);
        }
        sb.Append(')');

        var rows = await db.QueryAsync<Note>(
            new CommandDefinition(sb.ToString(), parms, cancellationToken: ct));
        return rows.ToDictionary(n => n.Id, n => n);
    }
}
