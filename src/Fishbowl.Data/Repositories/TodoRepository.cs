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

    public TodoRepository(DatabaseFactory dbFactory, ILogger<TodoRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<TodoRepository>.Instance;
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
        sql += " ORDER BY created_at DESC";

        return await db.QueryAsync<TodoItem>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<string> CreateAsync(ContextRef ctx, string actorUserId, TodoItem item, CancellationToken ct = default)
    {
        EnforceLimits(item);

        if (string.IsNullOrEmpty(item.Id))
        {
            item.Id = Ulid.NewUlid().ToString();
        }

        _logger.LogDebug("Creating todo {Id} in context {CtxType}:{CtxId}", item.Id, ctx.Type, ctx.Id);

        item.CreatedAt = DateTime.UtcNow;
        item.UpdatedAt = item.CreatedAt;
        item.CreatedBy = actorUserId;

        using var db = _dbFactory.CreateContextConnection(ctx);
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO todos (id, title, description, due_at, reminder_at, source, created_by, created_at, updated_at, completed_at)
            VALUES (@Id, @Title, @Description, @DueAt, @ReminderAt, @Source, @CreatedBy, @CreatedAt, @UpdatedAt, @CompletedAt)",
            new
            {
                item.Id,
                item.Title,
                item.Description,
                DueAt = item.DueAt?.ToString("o"),
                ReminderAt = item.ReminderAt?.ToString("o"),
                item.Source,
                item.CreatedBy,
                CreatedAt = item.CreatedAt.ToString("o"),
                UpdatedAt = item.UpdatedAt.ToString("o"),
                CompletedAt = item.CompletedAt?.ToString("o")
            }, cancellationToken: ct));

        return item.Id;
    }

    public async Task<bool> UpdateAsync(ContextRef ctx, TodoItem item, CancellationToken ct = default)
    {
        EnforceLimits(item);
        item.UpdatedAt = DateTime.UtcNow;

        using var db = _dbFactory.CreateContextConnection(ctx);
        var affected = await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE todos
            SET title = @Title,
                description = @Description,
                due_at = @DueAt,
                reminder_at = @ReminderAt,
                source = @Source,
                updated_at = @UpdatedAt,
                completed_at = @CompletedAt
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
                item.Id
            }, cancellationToken: ct));

        if (affected == 0)
        {
            _logger.LogWarning("Update of todo {Id} in {CtxType}:{CtxId} matched no rows",
                item.Id, ctx.Type, ctx.Id);
        }

        return affected > 0;
    }

    public async Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        var affected = await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM todos WHERE id = @id", new { id }, cancellationToken: ct));
        return affected > 0;
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
