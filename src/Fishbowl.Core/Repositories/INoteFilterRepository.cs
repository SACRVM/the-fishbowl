using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

// The Notes app's saved filters of a workspace (user/space schema v31):
// a name and a tag expression, in the order kept by `position`.
public interface INoteFilterRepository
{
    Task<IReadOnlyList<NoteFilter>> ListAsync(ContextRef ctx, CancellationToken ct = default);
    Task<NoteFilter> CreateAsync(ContextRef ctx, string actorUserId, string name, string expr, CancellationToken ct = default);
    Task<NoteFilter?> UpdateAsync(ContextRef ctx, string id, string? name, string? expr, double? position, CancellationToken ct = default);
    Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default);
}
