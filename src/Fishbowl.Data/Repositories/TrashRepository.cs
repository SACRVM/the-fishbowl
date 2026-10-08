using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Fishbowl.Data.Tables;

namespace Fishbowl.Data.Repositories;

// Takes the snapshot of a row that is about to be deleted — called by the
// repositories inside their delete transaction, so an item is either in its
// table or in the trash, never lost in between. The snapshot is the whole
// row, column by column (blobs as base64), so restore puts back exactly
// what was there, whatever the schema has grown since.
public static class TrashSnapshots
{
    // What each kind is: its table and the column that names it.
    internal static readonly IReadOnlyDictionary<string, (string Table, string TitleColumn)> Kinds =
        new Dictionary<string, (string, string)>
        {
            [TrashKinds.Note] = ("notes", "title"),
            [TrashKinds.Todo] = ("todos", "title"),
            [TrashKinds.Event] = ("events", "title"),
            [TrashKinds.Contact] = ("contacts", "name"),
            [TrashKinds.Mail] = ("mail_messages", "subject"),
        };

    public static async Task TakeAsync(
        IDbConnection db, IDbTransaction tx, ContextRef ctx, string kind, string id, string? deletedBy, CancellationToken ct)
    {
        var (table, titleColumn) = Kinds[kind];
        // Links are foreign keys: a note a table row links to stays.
        await LinkGuard.EnsureUnlinkedAsync(db, tx, table, id, ct);
        using var reader = await ((SqliteConnection)db).ExecuteReaderAsync(new CommandDefinition(
            $"SELECT * FROM {table} WHERE id = @id", new { id }, transaction: tx, cancellationToken: ct));
        if (!reader.Read()) return;
        var row = new JsonObject();
        string? title = null;
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
            row[name] = value switch
            {
                null => null,
                byte[] bytes => new JsonObject { ["$b64"] = Convert.ToBase64String(bytes) },
                long l => l,
                double d => d,
                _ => value.ToString(),
            };
            if (name == titleColumn) title = value?.ToString();
        }
        reader.Close();
        await PutAsync(db, tx, ctx, kind, id, title, row, deletedBy, ct);
    }

    // Any snapshot into the trash — also a table row's ("row", with
    // "$table") and a whole dropped table's ("table").
    public static async Task PutAsync(
        IDbConnection db, IDbTransaction tx, ContextRef ctx, string kind, string id, string? title, JsonObject row, string? deletedBy, CancellationToken ct)
    {
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO trash(id, kind, item_id, title, data, deleted_by, deleted_at)
            VALUES (@tid, @kind, @id, @title, @data, @by, @at)",
            new
            {
                tid = Ulid.NewUlid().ToString(),
                kind,
                id,
                title,
                data = row.ToJsonString(),
                by = deletedBy ?? (ctx.Type == ContextType.User ? ctx.Id : null),
                at = DateTime.UtcNow.ToString("o"),
            }, transaction: tx, cancellationToken: ct));
    }
}

public class TrashRepository : ITrashRepository
{
    private readonly DatabaseFactory _dbFactory;
    private readonly INoteRepository _notes;
    private readonly ITableRepository? _tables;
    private readonly ILogger<TrashRepository> _logger;

    public TrashRepository(DatabaseFactory dbFactory, INoteRepository notes, ITableRepository? tables = null, ILogger<TrashRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _notes = notes;
        _tables = tables;
        _logger = logger ?? NullLogger<TrashRepository>.Instance;
    }

    private sealed class TrashRow : TrashItem
    {
        public string Data { get; set; } = "{}";
    }

    public async Task<IReadOnlyList<TrashItem>> ListAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<TrashItem>(new CommandDefinition(
            "SELECT id, kind, item_id, title, deleted_by, deleted_at FROM trash ORDER BY deleted_at DESC",
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<(TrashRestore Result, TrashItem? Item)> RestoreAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        TrashRow? item = null;
        TrashRestore result;
        try
        {
            result = await _dbFactory.WithContextTransactionAsync<TrashRestore>(ctx, async (db, tx, token) =>
            {
                item = await db.QuerySingleOrDefaultAsync<TrashRow>(new CommandDefinition(
                    "SELECT * FROM trash WHERE id = @id", new { id }, transaction: tx, cancellationToken: token));
                if (item is null) return TrashRestore.NotFound;
                if (item.Kind is "row" or "table")
                {
                    var data = JsonNode.Parse(item.Data)!.AsObject();
                    var back = item.Kind == "row"
                        ? await TableRepository.RestoreRowAsync(db, tx, data["$table"]!.GetValue<string>(), data, token)
                        : await TableRepository.RestoreTableAsync(db, tx, data, token);
                    if (!back) throw new RestoreRefused();   // roll back what was put back so far
                    await db.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM trash WHERE id = @id", new { id }, transaction: tx, cancellationToken: token));
                    return TrashRestore.Restored;
                }
                if (!TrashSnapshots.Kinds.TryGetValue(item.Kind, out var kind)) return TrashRestore.NotFound;

                var columns = (await db.QueryAsync<string>(new CommandDefinition(
                    $"SELECT name FROM pragma_table_info('{kind.Table}')", transaction: tx, cancellationToken: token))).ToHashSet();
                var snapshot = JsonNode.Parse(item.Data)!.AsObject();
                var values = new DynamicParameters();
                var names = new List<string>();
                foreach (var (name, node) in snapshot)
                {
                    if (!columns.Contains(name)) continue;   // a column dropped since
                    names.Add(name);
                    values.Add("p" + names.Count, ValueOf(node));
                }
                if (await db.ExecuteScalarAsync<long>(new CommandDefinition(
                        $"SELECT COUNT(*) FROM {kind.Table} WHERE id = @id", new { id = item.ItemId },
                        transaction: tx, cancellationToken: token)) > 0)
                    return TrashRestore.Conflict;
                try
                {
                    await db.ExecuteAsync(new CommandDefinition(
                        $"INSERT INTO {kind.Table} ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select((_, i) => "@p" + (i + 1)))})",
                        values, transaction: tx, cancellationToken: token));
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)   // a unique value or a link that is gone
                {
                    return TrashRestore.Conflict;
                }

                // A note's full-text row comes back with it (the vector after commit).
                if (item.Kind == TrashKinds.Note)
                {
                    var tags = snapshot["tags"] is JsonValue tv && tv.TryGetValue<string>(out var tj)
                        ? string.Join(' ', JsonSerializer.Deserialize<List<string>>(tj) ?? new()) : "";
                    // Indexed secret-stripped, like every note write (NoteRepository.FtsContent).
                    var content = await db.ExecuteScalarAsync<string?>(new CommandDefinition(
                        "SELECT content FROM notes WHERE id = @id", new { id = item.ItemId },
                        transaction: tx, cancellationToken: token));
                    await db.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO notes_fts (rowid, title, content, tags)
                    SELECT rowid, title, @content, @tags FROM notes WHERE id = @id",
                        new { id = item.ItemId, content = NoteRepository.FtsContent(content), tags },
                        transaction: tx, cancellationToken: token));
                }

                if (item.Kind == TrashKinds.Contact)
                {
                    // A person whose organisation went to the trash after them
                    // comes back without it — a dangling organisation_id would
                    // refuse every later save (organisation_invalid).
                    await db.ExecuteAsync(new CommandDefinition(@"
                    UPDATE contacts SET organisation_id = NULL
                    WHERE id = @id AND organisation_id IS NOT NULL
                      AND organisation_id NOT IN (SELECT id FROM contacts WHERE kind = 'organisation')",
                        new { id = item.ItemId }, transaction: tx, cancellationToken: token));
                    // The same search row every contact write builds (all the
                    // emails and phones, the organisation's name, the rest).
                    await ContactRepository.ReindexAsync(db, tx, item.ItemId, token);
                }

                // A mail message: its references, addresses and full-text row.
                if (item.Kind == TrashKinds.Mail)
                    await Fishbowl.Data.Mail.MailRepository.ReindexRestoredAsync(db, tx, item.ItemId, token);

                await db.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM trash WHERE id = @id", new { id }, transaction: tx, cancellationToken: token));
                return TrashRestore.Restored;
            }, ct);
        }
        catch (RestoreRefused) { result = TrashRestore.Conflict; }

        // Notes also need their search index (full text + vector) back.
        if (result == TrashRestore.Restored && item!.Kind == TrashKinds.Note)
            await _notes.ReindexAsync(ctx, item.ItemId, ct);
        // Table rows get their search index back the same way.
        if (result == TrashRestore.Restored && _tables is not null && item!.Kind is TrashKinds.Row or TrashKinds.Table)
        {
            var data = JsonNode.Parse(item.Data)!.AsObject();
            if (item.Kind == TrashKinds.Row)
                await _tables.ReindexAsync(ctx, data["$table"]!.GetValue<string>(), item.ItemId, ct);
            else
                await _tables.ReindexAsync(ctx, item.ItemId, null, ct);
        }
        if (result == TrashRestore.Restored)
            _logger.LogInformation("Restored {Kind} {ItemId} from the trash in {CtxType}:{CtxId}", item!.Kind, item.ItemId, ctx.Type, ctx.Id);
        return (result, item);
    }

    private static object? ValueOf(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o when o["$b64"] is JsonValue b => Convert.FromBase64String(b.GetValue<string>()),
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v => v.GetValue<string>(),
        _ => node.ToJsonString(),
    };

    public async Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM trash WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    public async Task<int> EmptyAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM trash", cancellationToken: ct));
    }

    public async Task<int> PurgeAsync(ContextRef ctx, DateTime cutoff, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM trash WHERE deleted_at < @cutoff", new { cutoff = cutoff.ToUniversalTime().ToString("o") }, cancellationToken: ct));
    }
}

internal sealed class RestoreRefused : Exception { }
