using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

// A workspace's mail (mail spec): its accounts and the synced messages, read
// as threads. The sync writes through the Data implementation's own methods;
// this is what the API, the MCP tools and the app read and change.
public interface IMailRepository
{
    Task<IReadOnlyList<MailAccount>> ListAccountsAsync(ContextRef ctx, CancellationToken ct = default);
    Task<MailAccount?> GetAccountAsync(ContextRef ctx, string id, CancellationToken ct = default);
    /// <summary>Stores the account with its password encrypted; the sync picks it up.</summary>
    Task<MailAccount> CreateAccountAsync(ContextRef ctx, MailAccount account, string password, CancellationToken ct = default);
    Task<MailAccount?> UpdateAccountAsync(ContextRef ctx, string id, MailAccountUpdate update, CancellationToken ct = default);
    /// <summary>Removes the account and its mail from Fishbowl (the server keeps it).</summary>
    Task<bool> DeleteAccountAsync(ContextRef ctx, string id, CancellationToken ct = default);

    Task<IReadOnlyList<MailThread>> ListThreadsAsync(ContextRef ctx, MailListQuery query, CancellationToken ct = default);
    /// <summary>A thread's messages, oldest first (one per Message-ID).</summary>
    Task<IReadOnlyList<MailMessage>> GetThreadAsync(ContextRef ctx, string threadId, CancellationToken ct = default);
    Task<MailMessage?> GetMessageAsync(ContextRef ctx, string id, CancellationToken ct = default);
    /// <summary>Threads in the list with an unread incoming message.</summary>
    Task<int> UnreadCountAsync(ContextRef ctx, CancellationToken ct = default);

    /// <summary>Gives every message of the thread exactly these tags; null when there is no such thread.</summary>
    Task<IReadOnlyList<string>?> SetThreadTagsAsync(ContextRef ctx, string threadId, IReadOnlyList<string> tags, CancellationToken ct = default);
    /// <summary>Marks the thread's incoming messages (un)read here and returns
    /// where they lie on the server, for the write-back.</summary>
    Task<IReadOnlyList<MailLocationRef>> SetThreadSeenAsync(ContextRef ctx, string threadId, bool seen, CancellationToken ct = default);
    /// <summary>Where a message lies on the server (for its attachments).</summary>
    Task<IReadOnlyList<MailLocationRef>> LocationsAsync(ContextRef ctx, string messageId, CancellationToken ct = default);
}
