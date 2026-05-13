using Fishbowl.Core.Apps;

namespace Fishbowl.Core.Repositories;

// Schema-side of the Apps platform: owner-defined CREATE/ALTER/DROP against
// an app's own .db file. All operations are transactional and route through
// DatabaseFactory.CreateAppConnection(AppRef). Base columns are server-
// injected by CreateTableAsync and rejected from any caller-visible mutation
// (rename/drop/overlap-on-add) by every method here.
public interface IAppSchemaRepository
{
    Task<IReadOnlyList<string>> ListTablesAsync(AppRef appRef, CancellationToken ct = default);

    Task<AppTable?> DescribeTableAsync(AppRef appRef, string tableName, CancellationToken ct = default);

    Task CreateTableAsync(
        AppRef appRef,
        string tableName,
        IReadOnlyList<AppColumn> userColumns,
        CancellationToken ct = default);

    Task AddColumnAsync(
        AppRef appRef,
        string tableName,
        AppColumn column,
        CancellationToken ct = default);

    Task RenameColumnAsync(
        AppRef appRef,
        string tableName,
        string fromName,
        string toName,
        CancellationToken ct = default);

    Task DropColumnAsync(
        AppRef appRef,
        string tableName,
        string columnName,
        CancellationToken ct = default);

    Task DropTableAsync(AppRef appRef, string tableName, CancellationToken ct = default);

    Task CreateIndexAsync(
        AppRef appRef,
        string tableName,
        string columnName,
        CancellationToken ct = default);
}
