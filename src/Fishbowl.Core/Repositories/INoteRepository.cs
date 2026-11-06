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

    // Every note in change order (updated_at, then id — both ascending) for
    // a copy kept in sync (MCP note_list): the tag filter, only what changed
    // after `Since`, past the previous page's keyset. Limit 1–500.
    Task<IReadOnlyList<NoteChange>> ListChangesAsync(ContextRef ctx, NoteChangeQuery query, CancellationToken ct = default);

    // The note whose slug this is, in any case (user/space schema v31).
    Task<Note?> GetBySlugAsync(ContextRef ctx, string slug, CancellationToken ct = default);

    // One change applied to what is stored, in one transaction (NotePatch):
    // the tags added and removed in place, an append, the lock. A locked note
    // refuses a new title or text (409 note_locked) but takes an append.
    // Source tags as for an update — except that a human change of tags
    // alone (a board move) is no approval and keeps review:pending. An
    // agent's change keeps the note as it was (note_versions). Null when the
    // note isn't there.
    Task<Note?> PatchAsync(ContextRef ctx, string id, NotePatch patch, NoteSource source, string? actorUserId, CancellationToken ct = default);

    // What agents changed: the note before each of their writes, newest first.
    Task<IReadOnlyList<NoteVersion>> ListVersionsAsync(ContextRef ctx, string noteId, CancellationToken ct = default);
    Task<NoteVersion?> GetVersionAsync(ContextRef ctx, string noteId, string versionId, CancellationToken ct = default);

    Task<string> CreateAsync(ContextRef ctx, string actorUserId, Note note, CancellationToken ct = default);
    Task<string> CreateAsync(ContextRef ctx, string actorUserId, Note note, NoteSource source, CancellationToken ct = default);
    Task<bool> UpdateAsync(ContextRef ctx, Note note, CancellationToken ct = default);
    Task<bool> UpdateAsync(ContextRef ctx, Note note, NoteSource source, CancellationToken ct = default);
    // The row goes to the workspace trash (TrashSnapshots) in the same
    // transaction. `deletedBy` defaults to the personal workspace's owner.
    Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default, string? deletedBy = null);

    // Rebuilds one note's full-text row and vector — after a restore from
    // the trash put the notes row back.
    Task ReindexAsync(ContextRef ctx, string id, CancellationToken ct = default);

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
