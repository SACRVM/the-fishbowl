using Fishbowl.Core;
using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

public interface ITodoRepository
{
    // ContextRef overloads — the canonical shape. Supports both personal
    // (ContextRef.User) and space (ContextRef.Space) callers.
    Task<TodoItem?> GetByIdAsync(ContextRef ctx, string id, CancellationToken ct = default);
    Task<IEnumerable<TodoItem>> GetAllAsync(ContextRef ctx, bool includeCompleted = false, CancellationToken ct = default);
    Task<string> CreateAsync(ContextRef ctx, string actorUserId, TodoItem item, CancellationToken ct = default);
    Task<bool> UpdateAsync(ContextRef ctx, TodoItem item, CancellationToken ct = default);
    Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default);

    // Legacy (personal-context) aliases. Kept so existing cookie-auth call
    // sites stay minimal-diff. Implementations delegate to the ContextRef
    // versions with ContextRef.User(userId); `actorUserId` for Create is
    // the same string in the legacy shape.
    Task<TodoItem?> GetByIdAsync(string userId, string id, CancellationToken ct = default);
    Task<IEnumerable<TodoItem>> GetAllAsync(string userId, bool includeCompleted = false, CancellationToken ct = default);
    Task<string> CreateAsync(string userId, TodoItem item, CancellationToken ct = default);
    Task<bool> UpdateAsync(string userId, TodoItem item, CancellationToken ct = default);
    Task<bool> DeleteAsync(string userId, string id, CancellationToken ct = default);
}
