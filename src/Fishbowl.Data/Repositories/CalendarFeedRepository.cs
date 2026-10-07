using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Data.Repositories;

// Calendar subscription links in system.db (system schema v18): only the
// token's SHA-256 is kept, so a database copy opens no calendar.
public class CalendarFeedRepository : ICalendarFeedRepository
{
    private const string TokenPrefix = "fbcal_";
    private readonly DatabaseFactory _db;

    public CalendarFeedRepository(DatabaseFactory db) => _db = db;

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string ContextType { get; set; } = "";
        public string ContextId { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public string CreatedAt { get; set; } = "";

        public CalendarFeed ToFeed() => new(Id, ContextType, ContextId, CreatedBy,
            DateTime.Parse(CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static string TypeOf(ContextRef ctx) => ctx.Type == ContextType.Space ? "space" : "user";

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<(CalendarFeed Feed, string Token)> CreateAsync(ContextRef ctx, string userId, CancellationToken ct = default)
    {
        var token = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var row = new Row
        {
            Id = Ulid.NewUlid().ToString(),
            ContextType = TypeOf(ctx),
            ContextId = ctx.Id,
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow.ToString("o"),
        };
        using var db = _db.CreateSystemConnection();
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO calendar_feeds (id, token_hash, context_type, context_id, created_by, created_at)
            VALUES (@Id, @hash, @ContextType, @ContextId, @CreatedBy, @CreatedAt)",
            new { row.Id, hash = Hash(token), row.ContextType, row.ContextId, row.CreatedBy, row.CreatedAt }, cancellationToken: ct));
        return (row.ToFeed(), token);
    }

    public async Task<IReadOnlyList<CalendarFeed>> ListAsync(ContextRef ctx, string userId, CancellationToken ct = default)
    {
        using var db = _db.CreateSystemConnection();
        var rows = await db.QueryAsync<Row>(new CommandDefinition(@"
            SELECT id, context_type, context_id, created_by, created_at FROM calendar_feeds
            WHERE context_type = @type AND context_id = @id AND created_by = @userId
            ORDER BY created_at DESC",
            new { type = TypeOf(ctx), id = ctx.Id, userId }, cancellationToken: ct));
        return rows.Select(r => r.ToFeed()).ToList();
    }

    public async Task<bool> DeleteAsync(ContextRef ctx, string userId, string id, CancellationToken ct = default)
    {
        using var db = _db.CreateSystemConnection();
        return await db.ExecuteAsync(new CommandDefinition(@"
            DELETE FROM calendar_feeds
            WHERE id = @id AND context_type = @type AND context_id = @ctxId AND created_by = @userId",
            new { id, type = TypeOf(ctx), ctxId = ctx.Id, userId }, cancellationToken: ct)) > 0;
    }

    public async Task<CalendarFeed?> FindByTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal) || token.Length > 128) return null;
        using var db = _db.CreateSystemConnection();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(@"
            SELECT id, context_type, context_id, created_by, created_at FROM calendar_feeds WHERE token_hash = @hash",
            new { hash = Hash(token) }, cancellationToken: ct));
        return row?.ToFeed();
    }
}
