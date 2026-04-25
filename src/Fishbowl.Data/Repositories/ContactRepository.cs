using System.Data;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class ContactRepository : IContactRepository
{
    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<ContactRepository> _logger;

    public ContactRepository(
        DatabaseFactory dbFactory,
        ILogger<ContactRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<ContactRepository>.Instance;
    }

    // ────────── ContextRef overloads (canonical) ──────────

    public async Task<Contact?> GetByIdAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.QuerySingleOrDefaultAsync<Contact>(
            new CommandDefinition("SELECT * FROM contacts WHERE id = @id",
                new { id }, cancellationToken: ct));
    }

    public async Task<IEnumerable<Contact>> GetAllAsync(
        ContextRef ctx, bool includeArchived = false, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);

        // LOWER(name) ASC matches the idx_contacts_name_lower index and makes
        // the list feel like an address book; archived rows move to the back
        // but stay visible when the caller opts in.
        var sql = includeArchived
            ? "SELECT * FROM contacts ORDER BY archived ASC, LOWER(name) ASC"
            : "SELECT * FROM contacts WHERE archived = 0 ORDER BY LOWER(name) ASC";

        return await db.QueryAsync<Contact>(new CommandDefinition(sql, cancellationToken: ct));
    }

    // FTS5 query shape mirrors HybridSearchService.RunFtsAsync: split on
    // non-alphanumerics, lowercase, prefix-match each token with `*`, AND
    // them. Keeps query behaviour consistent with how contacts_fts indexes
    // by default, and avoids `-` being parsed as NOT.
    public async Task<IEnumerable<Contact>> SearchAsync(
        ContextRef ctx, string query, int limit = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<Contact>();

        var tokens = System.Text.RegularExpressions.Regex
            .Matches(query, @"\w+")
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Select(t => t + "*")
            .ToList();
        if (tokens.Count == 0) return Array.Empty<Contact>();
        var ftsQuery = string.Join(" AND ", tokens);

        var clamped = Math.Clamp(limit, 1, 500);

        using var db = _dbFactory.CreateContextConnection(ctx);

        const string sql = @"
            SELECT c.*
            FROM contacts_fts
            JOIN contacts c ON c.rowid = contacts_fts.rowid
            WHERE contacts_fts MATCH @q AND c.archived = 0
            ORDER BY bm25(contacts_fts)
            LIMIT @limit";

        return await db.QueryAsync<Contact>(new CommandDefinition(
            sql, new { q = ftsQuery, limit = clamped }, cancellationToken: ct));
    }

    public async Task<string> CreateAsync(
        ContextRef ctx, string actorUserId, Contact contact, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contact.Name))
            throw new ArgumentException("Contact name is required", nameof(contact));
        EnforceLimits(contact);

        if (string.IsNullOrEmpty(contact.Id))
            contact.Id = Ulid.NewUlid().ToString();

        contact.CreatedAt = DateTime.UtcNow;
        contact.UpdatedAt = contact.CreatedAt;
        contact.CreatedBy = actorUserId;

        _logger.LogDebug("Creating contact {Id} in context {CtxType}:{CtxId}",
            contact.Id, ctx.Type, ctx.Id);

        await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            await db.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO contacts (id, name, email, phone, notes, archived,
                                      created_by, created_at, updated_at)
                VALUES (@Id, @Name, @Email, @Phone, @Notes, @Archived,
                        @CreatedBy, @CreatedAt, @UpdatedAt)",
                new
                {
                    contact.Id,
                    contact.Name,
                    contact.Email,
                    contact.Phone,
                    contact.Notes,
                    Archived = contact.Archived ? 1 : 0,
                    contact.CreatedBy,
                    CreatedAt = contact.CreatedAt.ToString("o"),
                    UpdatedAt = contact.UpdatedAt.ToString("o"),
                }, transaction: tx, cancellationToken: token));

            await SyncFtsInsertAsync(db, tx, contact, token);
        }, ct);

        return contact.Id;
    }

    public async Task<bool> UpdateAsync(ContextRef ctx, Contact contact, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contact.Name))
            throw new ArgumentException("Contact name is required", nameof(contact));
        EnforceLimits(contact);

        contact.UpdatedAt = DateTime.UtcNow;

        return await _dbFactory.WithContextTransactionAsync<bool>(ctx, async (db, tx, token) =>
        {
            var affected = await db.ExecuteAsync(new CommandDefinition(@"
                UPDATE contacts
                SET name = @Name, email = @Email, phone = @Phone, notes = @Notes,
                    archived = @Archived, updated_at = @UpdatedAt
                WHERE id = @Id",
                new
                {
                    contact.Name,
                    contact.Email,
                    contact.Phone,
                    contact.Notes,
                    Archived = contact.Archived ? 1 : 0,
                    UpdatedAt = contact.UpdatedAt.ToString("o"),
                    contact.Id,
                }, transaction: tx, cancellationToken: token));

            if (affected == 0) return false;

            await SyncFtsUpdateAsync(db, tx, contact, token);
            return true;
        }, ct);
    }

    public async Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        return await _dbFactory.WithContextTransactionAsync<bool>(ctx, async (db, tx, token) =>
        {
            // FTS row first — same ordering rule as NoteRepository: strip
            // derived indexes before the authoritative row is gone so we
            // can still resolve rowid via a sub-select.
            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM contacts_fts WHERE rowid = (SELECT rowid FROM contacts WHERE id = @id)",
                new { id }, transaction: tx, cancellationToken: token));

            var affected = await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM contacts WHERE id = @id",
                new { id }, transaction: tx, cancellationToken: token));

            return affected > 0;
        }, ct);
    }

    private static void EnforceLimits(Contact contact)
    {
        var error = ContactLimits.Validate(contact);
        if (error is not null) throw new ResourceValidationException(error);
    }

    private static async Task SyncFtsInsertAsync(
        IDbConnection db, IDbTransaction tx, Contact contact, CancellationToken ct)
    {
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO contacts_fts (rowid, name, email, phone, notes)
            VALUES ((SELECT rowid FROM contacts WHERE id = @Id),
                    @Name, @Email, @Phone, @Notes)",
            new { contact.Id, contact.Name, contact.Email, contact.Phone, contact.Notes },
            transaction: tx, cancellationToken: ct));
    }

    private static async Task SyncFtsUpdateAsync(
        IDbConnection db, IDbTransaction tx, Contact contact, CancellationToken ct)
    {
        await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE contacts_fts
            SET name = @Name, email = @Email, phone = @Phone, notes = @Notes
            WHERE rowid = (SELECT rowid FROM contacts WHERE id = @Id)",
            new { contact.Id, contact.Name, contact.Email, contact.Phone, contact.Notes },
            transaction: tx, cancellationToken: ct));
    }

    // ────────── Legacy personal-context aliases ──────────

    public Task<Contact?> GetByIdAsync(string userId, string id, CancellationToken ct = default)
        => GetByIdAsync(ContextRef.User(userId), id, ct);

    public Task<IEnumerable<Contact>> GetAllAsync(string userId, bool includeArchived = false, CancellationToken ct = default)
        => GetAllAsync(ContextRef.User(userId), includeArchived, ct);

    public Task<string> CreateAsync(string userId, Contact contact, CancellationToken ct = default)
        => CreateAsync(ContextRef.User(userId), userId, contact, ct);

    public Task<bool> UpdateAsync(string userId, Contact contact, CancellationToken ct = default)
        => UpdateAsync(ContextRef.User(userId), contact, ct);

    public Task<bool> DeleteAsync(string userId, string id, CancellationToken ct = default)
        => DeleteAsync(ContextRef.User(userId), id, ct);
}
