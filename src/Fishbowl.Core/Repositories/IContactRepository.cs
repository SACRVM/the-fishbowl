using Fishbowl.Core;
using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

public interface IContactRepository
{
    // ContextRef overloads — canonical shape. Personal and team contacts
    // share identical schema (per CONCEPT.md § Teams "identical to user
    // DBs"); the context picks the file.

    Task<Contact?> GetByIdAsync(ContextRef ctx, string id, CancellationToken ct = default);
    Task<IEnumerable<Contact>> GetAllAsync(ContextRef ctx, bool includeArchived = false, CancellationToken ct = default);

    // FTS5-backed search across name/email/phone/notes, ranked by bm25.
    // Empty/whitespace query returns an empty list. Archived rows are
    // excluded — the user wants to *find someone*, not archaeology.
    Task<IEnumerable<Contact>> SearchAsync(ContextRef ctx, string query, int limit = 50, CancellationToken ct = default);

    Task<string> CreateAsync(ContextRef ctx, string actorUserId, Contact contact, CancellationToken ct = default);
    Task<bool> UpdateAsync(ContextRef ctx, Contact contact, CancellationToken ct = default);
    // The row goes to the workspace trash (TrashSnapshots) in the same
    // transaction. `deletedBy` defaults to the personal workspace's owner.
    Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default, string? deletedBy = null);

    // An organisation's people.
    Task<IReadOnlyList<Contact>> GetMembersAsync(ContextRef ctx, string organisationId, CancellationToken ct = default);
    // What points at a contact from the workspace's tables (newest first).
    Task<IReadOnlyList<ContactLink>> GetLinksAsync(ContextRef ctx, string id, CancellationToken ct = default);
    // `keep` takes what it lacks from `merge`; links move; `merge` goes to the trash.
    Task<Contact> MergeAsync(ContextRef ctx, string keepId, string mergeId, string actor, CancellationToken ct = default);

    // Legacy personal-context aliases. Cookie-auth callers that only hold
    // a userId keep a one-line signature.
    Task<Contact?> GetByIdAsync(string userId, string id, CancellationToken ct = default);
    Task<IEnumerable<Contact>> GetAllAsync(string userId, bool includeArchived = false, CancellationToken ct = default);
    Task<string> CreateAsync(string userId, Contact contact, CancellationToken ct = default);
    Task<bool> UpdateAsync(string userId, Contact contact, CancellationToken ct = default);
    Task<bool> DeleteAsync(string userId, string id, CancellationToken ct = default);
}

// A table row that links to a contact: its table, the link column, the row.
public sealed record ContactLink(string Kind, string Table, string Column, string RowId, string Title, string At);
