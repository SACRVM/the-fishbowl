using System.Data;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Tables;
using Fishbowl.Core.Util;
using Fishbowl.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

// Persons and organisations (user/space schema v18). The display name, the
// first email/phone and the search index are derived on every write; a
// person's organisation must be an organisation of the same workspace, and
// an organisation that people belong to can't be deleted (still_linked).
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

    private const string Columns = @"kind, name, salutation, honorific, first_name, last_name, organisation_id, role, birthday,
        legal_form, industry, customer_number, vat_id, tax_number, trade_register,
        emails, phones, addresses, website, iban, bic, account_holder, language, tags, photo,
        email, phone, notes, archived";

    private const string Values = @"@Kind, @Name, @Salutation, @Honorific, @FirstName, @LastName, @OrganisationId, @Role, @Birthday,
        @LegalForm, @Industry, @CustomerNumber, @VatId, @TaxNumber, @TradeRegister,
        @Emails, @Phones, @Addresses, @Website, @Iban, @Bic, @AccountHolder, @Language, @Tags, @Photo,
        @Email, @Phone, @Notes, @Archived";

    private const string Sets = @"kind = @Kind, name = @Name, salutation = @Salutation, honorific = @Honorific,
        first_name = @FirstName, last_name = @LastName, organisation_id = @OrganisationId, role = @Role, birthday = @Birthday,
        legal_form = @LegalForm, industry = @Industry, customer_number = @CustomerNumber, vat_id = @VatId,
        tax_number = @TaxNumber, trade_register = @TradeRegister, emails = @Emails, phones = @Phones, addresses = @Addresses,
        website = @Website, iban = @Iban, bic = @Bic, account_holder = @AccountHolder, language = @Language, tags = @Tags,
        photo = @Photo, email = @Email, phone = @Phone, notes = @Notes, archived = @Archived";

    // ────────── reads ──────────

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
        // An address book's order: by name; archived rows at the back when asked for.
        var sql = includeArchived
            ? "SELECT * FROM contacts ORDER BY archived ASC, LOWER(name) ASC"
            : "SELECT * FROM contacts WHERE archived = 0 ORDER BY LOWER(name) ASC";
        return await db.QueryAsync<Contact>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Contact>> GetMembersAsync(ContextRef ctx, string organisationId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return (await db.QueryAsync<Contact>(new CommandDefinition(
            "SELECT * FROM contacts WHERE organisation_id = @organisationId ORDER BY LOWER(name)",
            new { organisationId }, cancellationToken: ct))).ToList();
    }

    // FTS5 query shape mirrors HybridSearchService: split on non-alphanumerics,
    // prefix-match each token, AND them (a `-` never turns into NOT).
    public async Task<IEnumerable<Contact>> SearchAsync(
        ContextRef ctx, string query, int limit = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<Contact>();
        var tokens = System.Text.RegularExpressions.Regex
            .Matches(query, @"\w+")
            .Select(m => m.Value.ToLowerInvariant() + "*")
            .ToList();
        if (tokens.Count == 0) return Array.Empty<Contact>();

        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.QueryAsync<Contact>(new CommandDefinition(@"
            SELECT c.*
            FROM contacts_fts
            JOIN contacts c ON c.rowid = contacts_fts.rowid
            WHERE contacts_fts MATCH @q AND c.archived = 0
            ORDER BY bm25(contacts_fts)
            LIMIT @limit",
            new { q = string.Join(" AND ", tokens), limit = Math.Clamp(limit, 1, 500) }, cancellationToken: ct));
    }

    // What points at a contact: its people (an organisation's) and the rows
    // of the workspace's tables whose link columns name it.
    public async Task<IReadOnlyList<ContactLink>> GetLinksAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        var links = new List<ContactLink>();
        var hasTables = await db.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM sqlite_master WHERE name = 'db_columns'", cancellationToken: ct)) > 0;
        if (!hasTables) return links;
        var columns = await db.QueryAsync<(string TableName, string Name, long Multiple)>(new CommandDefinition(
            "SELECT table_name, name, multiple FROM db_columns WHERE kind = 'link' AND link_target = 'contacts'", cancellationToken: ct));
        foreach (var c in columns)
        {
            var sql = c.Multiple != 0
                ? $"SELECT r.id AS RowId, r.title AS Title, r.last_modified AS At FROM \"{TableNames.Physical(c.TableName)}\" r JOIN \"{TableNames.Join(c.TableName, c.Name)}\" j ON j.row_id = r.id WHERE j.target_id = @id"
                : $"SELECT id AS RowId, title AS Title, last_modified AS At FROM \"{TableNames.Physical(c.TableName)}\" WHERE \"{c.Name}\" = @id";
            foreach (var r in await db.QueryAsync<(string RowId, string Title, string At)>(new CommandDefinition(sql, new { id }, cancellationToken: ct)))
                links.Add(new ContactLink("row", c.TableName, c.Name, r.RowId, r.Title, r.At));
        }
        return links.OrderByDescending(l => l.At, StringComparer.Ordinal).ToList();
    }

    // ────────── writes ──────────

    public async Task<string> CreateAsync(
        ContextRef ctx, string actorUserId, Contact contact, CancellationToken ct = default)
    {
        Normalize(contact);
        EnforceLimits(contact);
        if (string.IsNullOrEmpty(contact.Id)) contact.Id = Ulid.NewUlid().ToString();
        contact.CreatedAt = DateTime.UtcNow;
        contact.UpdatedAt = contact.CreatedAt;
        contact.CreatedBy = actorUserId;

        _logger.LogDebug("Creating contact {Id} in context {CtxType}:{CtxId}", contact.Id, ctx.Type, ctx.Id);
        await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            await CheckOrganisationAsync(db, tx, contact, token);
            await db.ExecuteAsync(new CommandDefinition(
                $"INSERT INTO contacts (id, {Columns}, created_by, created_at, updated_at) VALUES (@Id, {Values}, @CreatedBy, @CreatedAt, @UpdatedAt)",
                Params(contact), transaction: tx, cancellationToken: token));
            await IndexAsync(db, tx, contact, insert: true, token);
        }, ct);
        return contact.Id;
    }

    public async Task<bool> UpdateAsync(ContextRef ctx, Contact contact, CancellationToken ct = default)
    {
        Normalize(contact);
        EnforceLimits(contact);
        contact.UpdatedAt = DateTime.UtcNow;

        return await _dbFactory.WithContextTransactionAsync<bool>(ctx, async (db, tx, token) =>
        {
            await CheckOrganisationAsync(db, tx, contact, token);
            if (contact.Kind == ContactKinds.Person)
            {
                // An organisation that people belong to stays one.
                var members = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COUNT(*) FROM contacts WHERE organisation_id = @Id", new { contact.Id }, tx, cancellationToken: token));
                if (members > 0)
                    throw TableException.Conflict("contact_has_people", "People belong to this organisation — move them first.", new { count = members });
            }
            var oldName = await db.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT name FROM contacts WHERE id = @Id", new { contact.Id }, tx, cancellationToken: token));
            var affected = await db.ExecuteAsync(new CommandDefinition(
                $"UPDATE contacts SET {Sets}, updated_at = @UpdatedAt WHERE id = @Id",
                Params(contact), transaction: tx, cancellationToken: token));
            if (affected == 0) return false;
            await IndexAsync(db, tx, contact, insert: false, token);
            // Its people are found by the organisation's name too.
            if (contact.Kind == ContactKinds.Organisation && oldName != contact.Name)
                await ReindexMembersAsync(db, tx, contact.Id, token);
            return true;
        }, ct);
    }

    public async Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default, string? deletedBy = null)
    {
        return await _dbFactory.WithContextTransactionAsync<bool>(ctx, async (db, tx, token) =>
        {
            var people = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM contacts WHERE organisation_id = @id", new { id }, tx, cancellationToken: token));
            if (people > 0)
                throw TableException.Conflict("still_linked",
                    $"It can't be deleted while {people} {(people == 1 ? "person belongs" : "people belong")} to it — move them first.",
                    new { links = $"contacts.organisation ({people})" });

            await TrashSnapshots.TakeAsync(db, tx, ctx, TrashKinds.Contact, id, deletedBy, token);
            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM contacts_fts WHERE rowid = (SELECT rowid FROM contacts WHERE id = @id)",
                new { id }, transaction: tx, cancellationToken: token));
            var affected = await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM contacts WHERE id = @id", new { id }, transaction: tx, cancellationToken: token));
            return affected > 0;
        }, ct);
    }

    // Two records of one contact: `keep` takes what it lacks from `merge`
    // (empty fields, more emails/phones/addresses/tags, the notes appended),
    // everything that pointed at `merge` points at `keep`, and `merge` goes
    // to the trash. One transaction.
    public async Task<Contact> MergeAsync(ContextRef ctx, string keepId, string mergeId, string actor, CancellationToken ct = default)
    {
        if (keepId == mergeId) throw new TableException("merge_same", "A contact can't be merged with itself.");
        return await _dbFactory.WithContextTransactionAsync<Contact>(ctx, async (db, tx, token) =>
        {
            var keep = await db.QuerySingleOrDefaultAsync<Contact>(new CommandDefinition(
                "SELECT * FROM contacts WHERE id = @keepId", new { keepId }, tx, cancellationToken: token))
                ?? throw TableException.NotFound("contact_missing", "There is no such contact.");
            var merge = await db.QuerySingleOrDefaultAsync<Contact>(new CommandDefinition(
                "SELECT * FROM contacts WHERE id = @mergeId", new { mergeId }, tx, cancellationToken: token))
                ?? throw TableException.NotFound("contact_missing", "There is no such contact.");
            if (keep.Kind != merge.Kind)
                throw new TableException("merge_kinds", "A person and an organisation can't be merged.");

            Fill(keep, merge);
            Normalize(keep);
            // Lists and notes add up: two valid contacts can make one that
            // isn't. Refused before anything is written.
            if (ContactLimits.Validate(keep) is { } tooMuch)
                throw new TableException("merge_too_large",
                    $"Together the two contacts are more than one contact holds ({tooMuch.Field}: {tooMuch.Reason}) — trim the duplicate first.",
                    args: new { field = tooMuch.Field });
            keep.UpdatedAt = DateTime.UtcNow;
            await db.ExecuteAsync(new CommandDefinition($"UPDATE contacts SET {Sets}, updated_at = @UpdatedAt WHERE id = @Id",
                Params(keep), transaction: tx, cancellationToken: token));
            await IndexAsync(db, tx, keep, insert: false, token);

            // Re-point: people of the merged organisation, table links.
            await db.ExecuteAsync(new CommandDefinition(
                "UPDATE contacts SET organisation_id = @keepId WHERE organisation_id = @mergeId",
                new { keepId, mergeId }, tx, cancellationToken: token));
            if (keep.Kind == ContactKinds.Organisation)
                await ReindexMembersAsync(db, tx, keepId, token);
            var hasTables = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'db_columns'", transaction: tx, cancellationToken: token)) > 0;
            if (hasTables)
            {
                var columns = await db.QueryAsync<(string TableName, string Name, long Multiple)>(new CommandDefinition(
                    "SELECT table_name, name, multiple FROM db_columns WHERE kind = 'link' AND link_target = 'contacts'",
                    transaction: tx, cancellationToken: token));
                foreach (var c in columns)
                {
                    if (c.Multiple != 0)
                    {
                        var join = TableNames.Join(c.TableName, c.Name);
                        await db.ExecuteAsync(new CommandDefinition(
                            $"UPDATE OR IGNORE \"{join}\" SET target_id = @keepId WHERE target_id = @mergeId; DELETE FROM \"{join}\" WHERE target_id = @mergeId;",
                            new { keepId, mergeId }, tx, cancellationToken: token));
                    }
                    else
                    {
                        await db.ExecuteAsync(new CommandDefinition(
                            $"UPDATE \"{TableNames.Physical(c.TableName)}\" SET \"{c.Name}\" = @keepId WHERE \"{c.Name}\" = @mergeId",
                            new { keepId, mergeId }, tx, cancellationToken: token));
                    }
                }
            }

            await TrashSnapshots.TakeAsync(db, tx, ctx, TrashKinds.Contact, mergeId, actor, token);
            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM contacts_fts WHERE rowid = (SELECT rowid FROM contacts WHERE id = @mergeId)",
                new { mergeId }, transaction: tx, cancellationToken: token));
            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM contacts WHERE id = @mergeId", new { mergeId }, transaction: tx, cancellationToken: token));
            return keep;
        }, ct);
    }

    // ────────── helpers ──────────

    // The derived fields: display name, the first email/phone, an empty
    // list where nothing came.
    public static void Normalize(Contact c)
    {
        c.Kind = string.IsNullOrWhiteSpace(c.Kind) ? ContactKinds.Person : c.Kind.Trim().ToLowerInvariant();
        c.Emails ??= new();
        c.Phones ??= new();
        c.Addresses ??= new();
        c.Tags ??= new();
        c.Emails = c.Emails.Where(e => !string.IsNullOrWhiteSpace(e?.Value)).Select(e => new ContactValue { Label = Blank(e.Label), Value = e.Value.Trim() }).ToList();
        c.Phones = c.Phones.Where(p => !string.IsNullOrWhiteSpace(p?.Value)).Select(p => new ContactValue { Label = Blank(p.Label), Value = p.Value.Trim() }).ToList();
        c.Addresses = c.Addresses.Where(a => a is not null && new[] { a.Street, a.PostalCode, a.City, a.Region, a.Country }.Any(v => !string.IsNullOrWhiteSpace(v))).ToList();
        c.Tags = c.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // A client that knows only `email` / `phone` (older clients, the MCP
        // tools) changes the first entry; the lists win when they agree.
        First(c.Emails, c.Email);
        First(c.Phones, c.Phone);
        c.Email = c.Emails.FirstOrDefault()?.Value;
        c.Phone = c.Phones.FirstOrDefault()?.Value;

        if (c.Kind == ContactKinds.Organisation) c.OrganisationId = null;
        else c.OrganisationId = Blank(c.OrganisationId);
        // A person's name is their first and last name whenever they have one.
        var full = string.Join(' ', new[] { c.FirstName, c.LastName }.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()));
        if (c.Kind == ContactKinds.Person && full.Length > 0) c.Name = full;
        c.Name = c.Name?.Trim() ?? "";
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void First(List<ContactValue> list, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        value = value.Trim();
        if (list.Any(v => v.Value == value)) return;
        if (list.Count == 0) list.Add(new ContactValue { Value = value });
        else list[0].Value = value;
    }

    // `keep` takes what it lacks from `other`.
    private static void Fill(Contact keep, Contact other)
    {
        foreach (var p in typeof(Contact).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite))
        {
            if (p.Name is nameof(Contact.Id) or nameof(Contact.Kind) or nameof(Contact.CreatedBy) or nameof(Contact.Notes)) continue;
            if (string.IsNullOrWhiteSpace((string?)p.GetValue(keep)) && !string.IsNullOrWhiteSpace((string?)p.GetValue(other)))
                p.SetValue(keep, p.GetValue(other));
        }
        keep.Emails = keep.Emails.Concat(other.Emails).DistinctBy(e => e.Value.ToLowerInvariant()).ToList();
        keep.Phones = keep.Phones.Concat(other.Phones).DistinctBy(p => new string(p.Value.Where(char.IsDigit).ToArray())).ToList();
        keep.Addresses = keep.Addresses.Concat(other.Addresses)
            .DistinctBy(a => $"{a.Street}|{a.PostalCode}|{a.City}".ToLowerInvariant()).ToList();
        keep.Tags = keep.Tags.Concat(other.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!string.IsNullOrWhiteSpace(other.Notes))
            keep.Notes = string.IsNullOrWhiteSpace(keep.Notes) ? other.Notes : keep.Notes + "\n\n" + other.Notes;
        if (keep.OrganisationId == other.Id) keep.OrganisationId = null;
    }

    private static async Task CheckOrganisationAsync(IDbConnection db, IDbTransaction tx, Contact c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Name))
            throw new ArgumentException(c.Kind == ContactKinds.Person
                ? "Contact name is required — a name, or a first or last name"
                : "Contact name is required for an organisation", nameof(c));
        if (c.OrganisationId is null) return;
        if (c.OrganisationId == c.Id)
            throw new TableException("organisation_invalid", "A person can't belong to themselves.");
        var kind = await db.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT kind FROM contacts WHERE id = @OrganisationId", new { c.OrganisationId }, tx, cancellationToken: ct));
        if (kind != ContactKinds.Organisation)
            throw new TableException("organisation_invalid", "organisationId must be an organisation in this workspace.");
    }

    private static object Params(Contact c) => new
    {
        c.Id,
        c.Kind,
        c.Name,
        c.Salutation,
        c.Honorific,
        c.FirstName,
        c.LastName,
        c.OrganisationId,
        c.Role,
        c.Birthday,
        c.LegalForm,
        c.Industry,
        c.CustomerNumber,
        c.VatId,
        c.TaxNumber,
        c.TradeRegister,
        c.Emails,
        c.Phones,
        c.Addresses,
        c.Website,
        c.Iban,
        c.Bic,
        c.AccountHolder,
        c.Language,
        c.Tags,
        c.Photo,
        c.Email,
        c.Phone,
        c.Notes,
        Archived = c.Archived ? 1 : 0,
        c.CreatedBy,
        CreatedAt = c.CreatedAt.ToString("o"),
        UpdatedAt = c.UpdatedAt.ToString("o"),
    };

    private static void EnforceLimits(Contact contact)
    {
        var error = ContactLimits.Validate(contact);
        if (error is not null) throw new ResourceValidationException(error);
    }

    // The search index: names (and the organisation's), every email and
    // phone, and the rest worth finding by in the notes column.
    private static async Task IndexAsync(IDbConnection db, IDbTransaction tx, Contact c, bool insert, CancellationToken ct)
    {
        var org = c.OrganisationId is null ? null : await db.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT name FROM contacts WHERE id = @OrganisationId", new { c.OrganisationId }, tx, cancellationToken: ct));
        var p = new
        {
            c.Id,
            Name = Join(c.Name, c.FirstName, c.LastName, org),
            Email = Join(c.Emails.Select(e => e.Value).ToArray()),
            Phone = Join(c.Phones.Select(e => e.Value).ToArray()),
            Notes = Join(new[] { c.Notes, c.Role, c.Industry, c.CustomerNumber, c.VatId, c.LegalForm, c.Website }
                .Concat(c.Addresses.SelectMany(a => new[] { a.Street, a.City, a.Country })).Concat(c.Tags).ToArray()),
        };
        await db.ExecuteAsync(new CommandDefinition(insert
            ? @"INSERT INTO contacts_fts (rowid, name, email, phone, notes)
                VALUES ((SELECT rowid FROM contacts WHERE id = @Id), @Name, @Email, @Phone, @Notes)"
            : @"UPDATE contacts_fts SET name = @Name, email = @Email, phone = @Phone, notes = @Notes
                WHERE rowid = (SELECT rowid FROM contacts WHERE id = @Id)",
            p, transaction: tx, cancellationToken: ct));
    }

    // One stored contact's search row, rebuilt from the row itself with the
    // routine every write uses — for trash restore and for an organisation's
    // people when its name changes.
    internal static async Task ReindexAsync(IDbConnection db, IDbTransaction tx, string id, CancellationToken ct)
    {
        var c = await db.QuerySingleOrDefaultAsync<Contact>(new CommandDefinition(
            "SELECT * FROM contacts WHERE id = @id", new { id }, tx, cancellationToken: ct));
        if (c is null) return;
        c.Emails ??= new();
        c.Phones ??= new();
        c.Addresses ??= new();
        c.Tags ??= new();
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM contacts_fts WHERE rowid = (SELECT rowid FROM contacts WHERE id = @id)",
            new { id }, transaction: tx, cancellationToken: ct));
        await IndexAsync(db, tx, c, insert: true, ct);
    }

    private static async Task ReindexMembersAsync(IDbConnection db, IDbTransaction tx, string organisationId, CancellationToken ct)
    {
        var people = await db.QueryAsync<string>(new CommandDefinition(
            "SELECT id FROM contacts WHERE organisation_id = @organisationId", new { organisationId }, tx, cancellationToken: ct));
        foreach (var id in people.ToList())
            await ReindexAsync(db, tx, id, ct);
    }

    private static string Join(params string?[] parts) => string.Join(' ', parts.Where(v => !string.IsNullOrWhiteSpace(v)));

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
