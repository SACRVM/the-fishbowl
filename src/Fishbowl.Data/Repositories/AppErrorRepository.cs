using System.Globalization;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Desktop;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Data.Repositories;

public class AppErrorRepository : IAppErrorRepository
{
    private readonly DatabaseFactory _dbFactory;

    public AppErrorRepository(DatabaseFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string At { get; set; } = "";
        public string App { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Detail { get; set; }
        public string? UserId { get; set; }
    }

    public async Task AddAsync(ContextRef ctx, string app, string kind, string message, string? detail, string? userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var p = new
        {
            Id = Ulid.NewUlid().ToString(),
            At = now.ToString("o"),
            Since = now.AddHours(-1).ToString("o"),
            App = AppErrors.Clip(app, 100),
            Kind = kind,
            Message = AppErrors.Clip(message, AppErrors.MaxMessage),
            Detail = string.IsNullOrEmpty(detail) ? null : AppErrors.Clip(detail, AppErrors.MaxDetail),
            UserId = userId,
            Keep = AppErrors.Keep,
        };
        await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, c) =>
        {
            var seen = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM app_errors WHERE app = @App AND kind = @Kind AND message = @Message AND at >= @Since",
                p, tx, cancellationToken: c));
            if (seen > 0) return;
            await db.ExecuteAsync(new CommandDefinition(
                "INSERT INTO app_errors(id, at, app, kind, message, detail, user_id) VALUES (@Id, @At, @App, @Kind, @Message, @Detail, @UserId)",
                p, tx, cancellationToken: c));
            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM app_errors WHERE id NOT IN (SELECT id FROM app_errors ORDER BY at DESC, id DESC LIMIT @Keep)",
                p, tx, cancellationToken: c));
        }, ct);
    }

    public async Task<IReadOnlyList<AppError>> ListAsync(ContextRef ctx, string? app, int limit, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<Row>(new CommandDefinition(
            "SELECT id, at, app, kind, message, detail, user_id FROM app_errors WHERE (@App IS NULL OR app = @App) ORDER BY at DESC, id DESC LIMIT @Limit",
            new { App = app, Limit = Math.Clamp(limit, 1, AppErrors.Keep) }, cancellationToken: ct));
        return rows.Select(r => new AppError(r.Id,
            DateTime.Parse(r.At, null, DateTimeStyles.RoundtripKind), r.App, r.Kind, r.Message, r.Detail, r.UserId)).ToList();
    }

    public async Task<int> ClearAsync(ContextRef ctx, string? app, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM app_errors WHERE (@App IS NULL OR app = @App)", new { App = app }, cancellationToken: ct));
    }
}
