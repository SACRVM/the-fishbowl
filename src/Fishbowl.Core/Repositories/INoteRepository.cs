using Fishbowl.Core;
using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

// Return shape for ReEmbedAllAsync. `Processed` = notes whose vector was
// (re-)written; `Failed` = notes where the embedding call threw something
// other than EmbeddingUnavailableException (see NoteRepository for the
// swallow policy). Skipped-because-unavailable rows fall into neither
// bucket and land on the next retry.
public record ReEmbedResult(int Processed, int Failed);

public interface INoteRepository
{
    // Context-aware overloads. `actorUserId` is the user writing/creating —
    // stored as created_by. For personal context it usually equals ctx.Id;
    // for team context it's the logged-in user, not the team id.

    Task<Note?> GetByIdAsync(ContextRef ctx, string id, CancellationToken ct = default);
    Task<IEnumerable<Note>> GetAllAsync(ContextRef ctx, CancellationToken ct = default);

    Task<IEnumerable<Note>> GetAllAsync(
        ContextRef ctx,
        IReadOnlyCollection<string>? tags,
        string match,
        CancellationToken ct = default);

    // Pagination overload. `limit` null → no LIMIT clause (existing
    // behaviour, used by the SPA which renders all notes today). When
    // set, `offset` defaults to 0. Both clamped at the repo so a typo'd
    // ?limit=999999 can't tip over Dapper or the JSON serialiser.
    Task<IEnumerable<Note>> GetAllAsync(
        ContextRef ctx,
        IReadOnlyCollection<string>? tags,
        string match,
        int? limit,
        int offset,
        CancellationToken ct = default);

    Task<string> CreateAsync(ContextRef ctx, string actorUserId, Note note, CancellationToken ct = default);
    Task<string> CreateAsync(ContextRef ctx, string actorUserId, Note note, NoteSource source, CancellationToken ct = default);
    Task<bool> UpdateAsync(ContextRef ctx, Note note, CancellationToken ct = default);
    Task<bool> UpdateAsync(ContextRef ctx, Note note, NoteSource source, CancellationToken ct = default);
    Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default);

    // Bulk re-embed: iterates every note in the context and re-runs the
    // vec_notes upsert. Returns a result with processed + failed counts.
    // Used by POST /api/v1/search/reindex on model upgrade or the first
    // time embeddings become available for a pre-existing personal DB.
    Task<ReEmbedResult> ReEmbedAllAsync(ContextRef ctx, CancellationToken ct = default);

    // Legacy (personal-context) aliases. Kept so existing cookie-auth call
    // sites stay minimal-diff. Implementations delegate to the ContextRef
    // versions with ContextRef.User(userId).

    Task<Note?> GetByIdAsync(string userId, string id, CancellationToken ct = default);
    Task<IEnumerable<Note>> GetAllAsync(string userId, CancellationToken ct = default);

    Task<IEnumerable<Note>> GetAllAsync(
        string userId,
        IReadOnlyCollection<string>? tags,
        string match,
        CancellationToken ct = default);

    Task<string> CreateAsync(string userId, Note note, CancellationToken ct = default);
    Task<bool> UpdateAsync(string userId, Note note, CancellationToken ct = default);
    Task<bool> DeleteAsync(string userId, string id, CancellationToken ct = default);
}
