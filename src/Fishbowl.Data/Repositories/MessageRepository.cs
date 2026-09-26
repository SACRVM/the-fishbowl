using Dapper;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Data.Repositories;

// The system inbox in system.db (schema v11). Every read is keyed on the
// recipient, so nobody sees somebody else's messages.
public class MessageRepository : IMessageRepository
{
    private readonly DatabaseFactory _dbFactory;

    public MessageRepository(DatabaseFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<string>> CreateAsync(
        IEnumerable<string> recipientIds, string kind, string? subjectType, string? subjectId,
        string? data = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow.ToString("o");
        var ids = new List<string>();
        using var db = _dbFactory.CreateSystemConnection();
        foreach (var recipientId in recipientIds.Distinct(StringComparer.Ordinal))
        {
            var id = Ulid.NewUlid().ToString();
            await db.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO messages (id, recipient_id, kind, subject_type, subject_id, data, created_at)
                VALUES (@id, @recipientId, @kind, @subjectType, @subjectId, @data, @now)",
                new { id, recipientId, kind, subjectType, subjectId, data, now }, cancellationToken: ct));
            ids.Add(id);
        }
        return ids;
    }

    public async Task<IReadOnlyList<SystemMessage>> ListAsync(string recipientId, bool unreadOnly = false, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var sql = @"SELECT id, recipient_id, kind, subject_type, subject_id, data, created_at, read_at, done_at
                    FROM messages WHERE recipient_id = @recipientId"
                  + (unreadOnly ? " AND read_at IS NULL" : "")
                  + " ORDER BY created_at DESC, id DESC LIMIT 200";
        var rows = await db.QueryAsync<SystemMessage>(new CommandDefinition(sql, new { recipientId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<int> CountUnreadAsync(string recipientId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM messages WHERE recipient_id = @recipientId AND read_at IS NULL",
            new { recipientId }, cancellationToken: ct));
    }

    public async Task<bool> MarkReadAsync(string id, string recipientId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var exists = await db.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM messages WHERE id = @id AND recipient_id = @recipientId",
            new { id, recipientId }, cancellationToken: ct));
        if (exists == 0) return false;
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE messages SET read_at = @now WHERE id = @id AND recipient_id = @recipientId AND read_at IS NULL",
            new { id, recipientId, now = DateTime.UtcNow.ToString("o") }, cancellationToken: ct));
        return true;
    }

    public async Task<int> ResolveAsync(string kind, string subjectType, string subjectId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var db = _dbFactory.CreateSystemConnection();
        return await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE messages
            SET done_at = @now, read_at = COALESCE(read_at, @now)
            WHERE kind = @kind AND subject_type = @subjectType AND subject_id = @subjectId AND done_at IS NULL",
            new { kind, subjectType, subjectId, now }, cancellationToken: ct));
    }

    public async Task<int> DeleteForRecipientAsync(string recipientId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM messages WHERE recipient_id = @recipientId", new { recipientId }, cancellationToken: ct));
    }
}
