using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

// System messages (system.db). Recipients only ever see their own rows.
public interface IMessageRepository
{
    // One copy per recipient, same kind/subject/data. Returns the new ids.
    Task<IReadOnlyList<string>> CreateAsync(
        IEnumerable<string> recipientIds, string kind, string? subjectType, string? subjectId,
        string? data = null, CancellationToken ct = default);

    Task<IReadOnlyList<SystemMessage>> ListAsync(string recipientId, bool unreadOnly = false, CancellationToken ct = default);

    Task<int> CountUnreadAsync(string recipientId, CancellationToken ct = default);

    // Marks one of the recipient's own messages read. False if it isn't theirs.
    Task<bool> MarkReadAsync(string id, string recipientId, CancellationToken ct = default);

    // Marks every open message of this kind about this subject done (and
    // read), for every recipient — "another admin already handled it".
    Task<int> ResolveAsync(string kind, string subjectType, string subjectId, CancellationToken ct = default);

    // Drops every message addressed to a user (their account is gone).
    Task<int> DeleteForRecipientAsync(string recipientId, CancellationToken ct = default);
}
