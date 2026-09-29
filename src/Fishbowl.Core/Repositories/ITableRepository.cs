using System.Text.Json;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Tables;

namespace Fishbowl.Core.Repositories;

// A space's own tables (space-apps spec, phase 3). `ctx` is always a space
// (by id); callers check the role — the repository checks what only the
// data can tell (own rows, links, unique values). Every refusal is a
// TableException. Rows travel as JSON objects keyed by column name.
public interface ITableRepository
{
    Task<IReadOnlyList<TableDef>> ListAsync(ContextRef ctx, CancellationToken ct = default);
    Task<TableDef?> DescribeAsync(ContextRef ctx, string table, CancellationToken ct = default);
    Task<long> CountRowsAsync(ContextRef ctx, string table, CancellationToken ct = default);

    Task<TableDef> CreateAsync(ContextRef ctx, TableActor actor, TableDef def, CancellationToken ct = default);
    Task<TableDef> AlterAsync(ContextRef ctx, TableActor actor, string table, TableChange change, CancellationToken ct = default);
    // The table and its rows go to the trash as one item. Refused while
    // another table links to it.
    Task DropAsync(ContextRef ctx, TableActor actor, string table, CancellationToken ct = default);

    Task<JsonElement> InsertAsync(ContextRef ctx, TableActor actor, string table, JsonElement values, CancellationToken ct = default);
    Task<JsonElement?> GetAsync(ContextRef ctx, string table, string id, CancellationToken ct = default);
    // Partial: only the given columns change. `rowVersion`, when given, must
    // match (optimistic concurrency).
    Task<JsonElement> UpdateAsync(ContextRef ctx, TableActor actor, string table, string id, JsonElement values, long? rowVersion, CancellationToken ct = default);
    // To the trash; refused while something links to the row.
    Task DeleteAsync(ContextRef ctx, TableActor actor, string table, string id, CancellationToken ct = default);

    Task<IReadOnlyList<JsonElement>> QueryAsync(ContextRef ctx, string table, QuerySpec spec, CancellationToken ct = default);
    Task<long> CountAsync(ContextRef ctx, string table, QuerySpec spec, CancellationToken ct = default);
    // count / sum / min / max / avg over `column` (count needs none), grouped
    // by one column or not at all.
    Task<IReadOnlyList<AggregateResult>> AggregateAsync(
        ContextRef ctx, string table, string fn, string? column, string? groupBy, JsonElement? where, CancellationToken ct = default);
}

// One ALTER: any mix of these, applied in this order, all or nothing.
public sealed record TableChange(
    string? Description = null,
    bool? OwnRows = null,
    IReadOnlyList<ColumnDef>? AddColumns = null,
    IReadOnlyList<string>? DropColumns = null,
    IReadOnlyDictionary<string, string>? RenameColumns = null,
    IReadOnlyDictionary<string, ColumnUpdate>? UpdateColumns = null);

// What may change on an existing column without rebuilding it.
public sealed record ColumnUpdate(string? Description = null, IReadOnlyList<string>? AddOptions = null, bool? Required = null);
