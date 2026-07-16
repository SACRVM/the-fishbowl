using System.Data;
using Fishbowl.Core;
using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

public interface ITagRepository
{
    // ContextRef overloads — the canonical shape. Supports both personal
    // (ContextRef.User) and space (ContextRef.Space) callers.
    Task<IEnumerable<Tag>> GetAllAsync(ContextRef ctx, CancellationToken ct = default);
    Task<Tag> UpsertColorAsync(ContextRef ctx, string name, string color, CancellationToken ct = default);
    Task<bool> RenameAsync(ContextRef ctx, string oldName, string newName, CancellationToken ct = default);
    Task<bool> DeleteAsync(ContextRef ctx, string name, CancellationToken ct = default);

    // Legacy (personal-context) aliases. Kept so existing cookie-auth call
    // sites stay minimal-diff. Implementations delegate to the ContextRef
    // versions with ContextRef.User(userId).
    Task<IEnumerable<Tag>> GetAllAsync(string userId, CancellationToken ct = default);
    Task<Tag> UpsertColorAsync(string userId, string name, string color, CancellationToken ct = default);
    Task<bool> RenameAsync(string userId, string oldName, string newName, CancellationToken ct = default);
    Task<bool> DeleteAsync(string userId, string name, CancellationToken ct = default);

    // In-transaction hook used by NoteRepository on save: registers any tag
    // names not yet known with a deterministic default color, and normalises
    // them in place. Returns the normalised list (deduped).
    Task<IReadOnlyList<string>> EnsureExistsAsync(
        IDbConnection db,
        IDbTransaction tx,
        IEnumerable<string> rawNames,
        CancellationToken ct = default);
}
