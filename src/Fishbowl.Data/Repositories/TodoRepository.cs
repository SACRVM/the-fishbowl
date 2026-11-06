using System.Data;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class TodoRepository : ITodoRepository
{
    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<TodoRepository> _logger;
    private readonly TagRepository _tags;

    public TodoRepository(DatabaseFactory dbFactory, ILogger<TodoRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<TodoRepository>.Instance;
        _tags = new TagRepository(dbFactory);
    }

    // ────────── ContextRef overloads (canonical implementation) ──────────

    public async Task<TodoItem?> GetByIdAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.QuerySingleOrDefaultAsync<TodoItem>(
            new CommandDefinition("SELECT * FROM todos WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<IEnumerable<TodoItem>> GetAllAsync(ContextRef ctx, bool includeCompleted = false, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        string sql = "SELECT * FROM todos";
        if (!includeCompleted)
        {
            sql += " WHERE completed_at IS NULL";
        }
        sql += " ORDER BY position, created_at, id";

        return await db.QueryAsync<TodoItem>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public Task<string> CreateAsync(ContextRef ctx, string actorUserId, TodoItem item, CancellationToken ct = default)
        => CreateAsync(ctx, actorUserId, item, NoteSource.Human, ct);

    public async Task<string> CreateAsync(ContextRef ctx, string actorUserId, TodoItem item, NoteSource source, CancellationToken ct = default)
    {
        item.Tags = SourceTags(item.Tags ?? new(), stored: null, source);
        EnforceLimits(item);

        if (string.IsNullOrEmpty(item.Id))
        {
            item.Id = Ulid.NewUlid().ToString();
        }
        if (string.IsNullOrWhiteSpace(item.Uid)) item.Uid = item.Id + "@fishbowl";

        _logger.LogDebug("Creating todo {Id} in context {CtxType}:{CtxId}", item.Id, ctx.Type, ctx.Id);

        item.CreatedAt = DateTime.UtcNow;
        item.UpdatedAt = item.CreatedAt;
        item.CreatedBy = actorUserId;

        await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            item.Tags = (await _tags.EnsureExistsAsync(db, tx, item.Tags, token)).ToList();
            // No position given: append after the last todo.
            item.Position ??= await db.ExecuteScalarAsync<double>(new CommandDefinition(
                "SELECT COALESCE(MAX(position), 0) + 1 FROM todos", transaction: tx, cancellationToken: token));
            await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO todos (id, uid, title, description, due_at, reminder_at, source, created_by, created_at, updated_at, completed_at, position, tags)
            VALUES (@Id, @Uid, @Title, @Description, @DueAt, @ReminderAt, @Source, @CreatedBy, @CreatedAt, @UpdatedAt, @CompletedAt, @Position, @Tags)",
                new
                {
                    item.Id,
                    item.Uid,
                    item.Title,
                    item.Description,
                    DueAt = item.DueAt?.ToString("o"),
                    ReminderAt = item.ReminderAt?.ToString("o"),
                    item.Source,
                    item.CreatedBy,
                    CreatedAt = item.CreatedAt.ToString("o"),
                    UpdatedAt = item.UpdatedAt.ToString("o"),
                    CompletedAt = item.CompletedAt?.ToString("o"),
                    item.Position,
                    Tags = System.Text.Json.JsonSerializer.Serialize(item.Tags),
                }, transaction: tx, cancellationToken: token));
        }, ct);

        return item.Id;
    }

    public async Task<bool> UpdateAsync(ContextRef ctx, TodoItem item, CancellationToken ct = default)
    {
        EnforceLimits(item);
        item.UpdatedAt = DateTime.UtcNow;

        var affected = await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            // Tags: null keeps what is stored (a client from before tags).
            string? tagsJson = null;
            if (item.Tags is not null)
            {
                var stored = await StoredTagsAsync(db, tx, item.Id, token);
                item.Tags = (await _tags.EnsureExistsAsync(db, tx, SourceTags(item.Tags, stored, NoteSource.Human), token)).ToList();
                tagsJson = System.Text.Json.JsonSerializer.Serialize(item.Tags);
            }
            return await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE todos
            SET title = @Title,
                description = @Description,
                due_at = @DueAt,
                reminder_at = @ReminderAt,
                source = @Source,
                updated_at = @UpdatedAt,
                completed_at = @CompletedAt,
                position = COALESCE(@Position, position),
                tags = COALESCE(@Tags, tags)
            WHERE id = @Id",
                new
                {
                    item.Title,
                    item.Description,
                    DueAt = item.DueAt?.ToString("o"),
                    ReminderAt = item.ReminderAt?.ToString("o"),
                    item.Source,
                    UpdatedAt = item.UpdatedAt.ToString("o"),
                    CompletedAt = item.CompletedAt?.ToString("o"),
                    item.Position,
                    Tags = tagsJson,
                    item.Id
                }, transaction: tx, cancellationToken: token));
        }, ct);

        if (affected == 0)
        {
            _logger.LogWarning("Update of todo {Id} in {CtxType}:{CtxId} matched no rows",
                item.Id, ctx.Type, ctx.Id);
        }

        return affected > 0;
    }

    public async Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default, string? deletedBy = null)
    {
        return await _dbFactory.WithContextTransactionAsync<bool>(ctx, async (db, tx, token) =>
        {
            await TrashSnapshots.TakeAsync(db, tx, ctx, TrashKinds.Todo, id, deletedBy, token);
            return await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM todos WHERE id = @id", new { id }, transaction: tx, cancellationToken: token)) > 0;
        }, ct);
    }

    public async Task<TodoItem?> PatchAsync(ContextRef ctx, string id, TodoPatch patch, NoteSource source, CancellationToken ct = default)
    {
        return await _dbFactory.WithContextTransactionAsync<TodoItem?>(ctx, async (db, tx, token) =>
        {
            var item = await db.QuerySingleOrDefaultAsync<TodoItem>(new CommandDefinition(
                "SELECT * FROM todos WHERE id = @id", new { id }, transaction: tx, cancellationToken: token));
            if (item is null) return null;
            var stored = item.Tags ?? new();

            if (patch.Title is not null) item.Title = patch.Title;
            if (patch.Description is not null) item.Description = patch.Description.Length == 0 ? null : patch.Description;
            if (patch.ClearDue) item.DueAt = null;
            else if (patch.DueAt is { } due) item.DueAt = due;
            if (patch.Done is { } done) item.CompletedAt = done ? item.CompletedAt ?? DateTime.UtcNow : null;

            static string Norm(string t) => TagName.IsValid(t) ? TagName.Normalize(t) : t.Trim();
            var tags = (patch.Tags ?? stored.ToList()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(Norm).ToList();
            foreach (var t in patch.AddTags ?? new())
                if (!string.IsNullOrWhiteSpace(t) && !tags.Contains(Norm(t))) tags.Add(Norm(t));
            var remove = (patch.RemoveTags ?? new()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(Norm).ToHashSet();
            tags.RemoveAll(remove.Contains);
            item.Tags = SourceTags(tags, stored, source);

            EnforceLimits(item);
            item.Tags = (await _tags.EnsureExistsAsync(db, tx, item.Tags, token)).ToList();
            item.UpdatedAt = DateTime.UtcNow;
            await db.ExecuteAsync(new CommandDefinition(@"
                UPDATE todos SET title = @Title, description = @Description, due_at = @DueAt,
                    completed_at = @CompletedAt, tags = @Tags, updated_at = @UpdatedAt
                WHERE id = @Id",
                new
                {
                    item.Title,
                    item.Description,
                    DueAt = item.DueAt?.ToUniversalTime().ToString("o"),
                    CompletedAt = item.CompletedAt?.ToUniversalTime().ToString("o"),
                    Tags = System.Text.Json.JsonSerializer.Serialize(item.Tags),
                    UpdatedAt = item.UpdatedAt.ToString("o"),
                    item.Id,
                }, transaction: tx, cancellationToken: token));
            return item;
        }, ct);
    }

    // A todo's tags under the notes' rules: an agent's carries source:mcp; a
    // person adds no tag only the system gives and takes none it keeps.
    private static List<string> SourceTags(IEnumerable<string> tags, IReadOnlyCollection<string>? stored, NoteSource source)
    {
        var list = tags.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => TagName.IsValid(t) ? TagName.Normalize(t) : t.Trim()).Distinct().ToList();
        var given = SystemTags.Seeds.ToDictionary(x => x.Name);
        if (source == NoteSource.Mcp)
        {
            if (!list.Contains("source:mcp")) list.Add("source:mcp");
        }
        else
        {
            list.RemoveAll(t => given.TryGetValue(t, out var spec) && !spec.UserAssignable && !(stored?.Contains(t) ?? false));
        }
        foreach (var t in stored ?? Array.Empty<string>())
            if (given.TryGetValue(t, out var spec) && !spec.UserRemovable && !list.Contains(t)) list.Add(t);
        return list;
    }

    private static async Task<List<string>> StoredTagsAsync(IDbConnection db, IDbTransaction tx, string id, CancellationToken ct)
    {
        var json = await db.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT tags FROM todos WHERE id = @id", new { id }, transaction: tx, cancellationToken: ct));
        try { return json is null ? new() : System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (System.Text.Json.JsonException) { return new(); }
    }

    private static void EnforceLimits(TodoItem item)
    {
        var error = TodoLimits.Validate(item);
        if (error is not null) throw new ResourceValidationException(error);
    }

    // ────────── Legacy (personal-context) aliases ──────────

    public Task<TodoItem?> GetByIdAsync(string userId, string id, CancellationToken ct = default)
        => GetByIdAsync(ContextRef.User(userId), id, ct);

    public Task<IEnumerable<TodoItem>> GetAllAsync(string userId, bool includeCompleted = false, CancellationToken ct = default)
        => GetAllAsync(ContextRef.User(userId), includeCompleted, ct);

    public Task<string> CreateAsync(string userId, TodoItem item, CancellationToken ct = default)
        => CreateAsync(ContextRef.User(userId), userId, item, ct);

    public Task<bool> UpdateAsync(string userId, TodoItem item, CancellationToken ct = default)
        => UpdateAsync(ContextRef.User(userId), item, ct);

    public Task<bool> DeleteAsync(string userId, string id, CancellationToken ct = default)
        => DeleteAsync(ContextRef.User(userId), id, ct);
}
