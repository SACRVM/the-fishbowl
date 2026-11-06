using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Data.Repositories;

// The Notes app's saved filters (user/space schema v31, note_filters). The
// expression is the client's to read (fb-notes-view's tag expressions); the
// server keeps it as text, at most 1000 characters, with a name of up to 100.
public class NoteFilterRepository : INoteFilterRepository
{
    public const int MaxName = 100;
    public const int MaxExpr = 1000;

    private readonly DatabaseFactory _dbFactory;

    public NoteFilterRepository(DatabaseFactory dbFactory) { _dbFactory = dbFactory; }

    public async Task<IReadOnlyList<NoteFilter>> ListAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return (await db.QueryAsync<NoteFilter>(new CommandDefinition(
            "SELECT * FROM note_filters ORDER BY position, created_at", cancellationToken: ct))).ToList();
    }

    public async Task<NoteFilter> CreateAsync(ContextRef ctx, string actorUserId, string name, string expr, CancellationToken ct = default)
    {
        var filter = new NoteFilter
        {
            Id = Ulid.NewUlid().ToString(),
            Name = Checked(name, MaxName, "name"),
            Expr = Checked(expr, MaxExpr, "expr"),
            CreatedBy = actorUserId,
            CreatedAt = DateTime.UtcNow,
        };
        using var db = _dbFactory.CreateContextConnection(ctx);
        filter.Position = await db.ExecuteScalarAsync<double>(new CommandDefinition(
            "SELECT COALESCE(MAX(position), 0) + 1 FROM note_filters", cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO note_filters (id, name, expr, position, created_by, created_at)
            VALUES (@Id, @Name, @Expr, @Position, @CreatedBy, @CreatedAt)",
            new { filter.Id, filter.Name, filter.Expr, filter.Position, filter.CreatedBy, CreatedAt = filter.CreatedAt.ToString("o") },
            cancellationToken: ct));
        return filter;
    }

    public async Task<NoteFilter?> UpdateAsync(ContextRef ctx, string id, string? name, string? expr, double? position, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        var affected = await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE note_filters SET name = COALESCE(@name, name), expr = COALESCE(@expr, expr),
                position = COALESCE(@position, position)
            WHERE id = @id",
            new
            {
                id,
                name = name is null ? null : Checked(name, MaxName, "name"),
                expr = expr is null ? null : Checked(expr, MaxExpr, "expr"),
                position,
            }, cancellationToken: ct));
        if (affected == 0) return null;
        return await db.QuerySingleAsync<NoteFilter>(new CommandDefinition(
            "SELECT * FROM note_filters WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM note_filters WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    private static string Checked(string value, int max, string field)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) throw new ArgumentException($"`{field}` is required.");
        if (trimmed.Length > max) throw new ArgumentException($"`{field}` is at most {max} characters.");
        return trimmed;
    }
}
