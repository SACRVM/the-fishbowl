using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

// The workspace trash for records (notes, todos, events, contacts). Items
// get in through the repositories' DeleteAsync, in the same transaction as
// the delete.
public interface ITrashRepository
{
    // Newest first.
    Task<IReadOnlyList<TrashItem>> ListAsync(ContextRef ctx, CancellationToken ct = default);

    // Puts the snapshot back (and its search index) and drops the trash row.
    Task<(TrashRestore Result, TrashItem? Item)> RestoreAsync(ContextRef ctx, string id, CancellationToken ct = default);

    Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default);

    Task<int> EmptyAsync(ContextRef ctx, CancellationToken ct = default);

    // Drops what was deleted before `cutoff` (the daily maintenance).
    Task<int> PurgeAsync(ContextRef ctx, DateTime cutoff, CancellationToken ct = default);
}
