using Fishbowl.Core.Apps;

namespace Fishbowl.Core.Repositories;

// Row-level CRUD against an app's owner-defined table. All operations route
// through DatabaseFactory.CreateAppConnection(AppRef). Base columns are
// server-managed: callers cannot pass `id`, `created_at`, `last_modified`,
// `row_version`, `is_deleted`, `deleted_at`, or `author` — those are filled
// (insert) or bumped (update) by the repository.
//
// Field dictionaries use case-sensitive ordinal keys matching the column
// names exactly. Values are native CLR types (string / long / double / bool);
// the wire layer (MCP/REST) is responsible for JSON-element → CLR coercion
// before reaching this surface.
public interface IAppRowRepository
{
    Task<IReadOnlyDictionary<string, object?>> InsertAsync(
        AppRef appRef,
        string tableName,
        IReadOnlyDictionary<string, object?> fields,
        string? actorId,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, object?>?> GetAsync(
        AppRef appRef,
        string tableName,
        string id,
        bool includeDeleted = false,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, object?>> UpdateAsync(
        AppRef appRef,
        string tableName,
        string id,
        IReadOnlyDictionary<string, object?> fields,
        string? actorId,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(
        AppRef appRef,
        string tableName,
        string id,
        bool hard,
        string? actorId,
        CancellationToken ct = default);

    Task<bool> RestoreAsync(
        AppRef appRef,
        string tableName,
        string id,
        string? actorId,
        CancellationToken ct = default);

    // Compiles the QuerySpec via QueryDsl against the table's live schema,
    // then executes. Rejects unknown columns, additional_data filters, and
    // DSL safety-cap violations before any SQL runs.
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        AppRef appRef,
        string tableName,
        QuerySpec spec,
        CancellationToken ct = default);

    Task<long> CountAsync(
        AppRef appRef,
        string tableName,
        QuerySpec spec,
        CancellationToken ct = default);
}
