using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Mail.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Mail;

/// <summary>
/// Talks to an account's server outside the sync: an attachment fetched on
/// demand (decision 7), seen / unseen written back (decision 9). A message's
/// places name the role; the folder state names the server folder.
/// </summary>
public sealed class MailServer
{
    private readonly MailRepository _repo;
    private readonly IMailboxConnector _connector;
    private readonly ILogger<MailServer> _logger;

    public MailServer(MailRepository repo, IMailboxConnector connector, ILogger<MailServer>? logger = null)
    {
        _repo = repo;
        _connector = connector;
        _logger = logger ?? NullLogger<MailServer>.Instance;
    }

    /// <summary>The account's mailbox, or null when the account or its password is gone.</summary>
    public async Task<IMailbox?> OpenAsync(ContextRef ctx, string accountId, CancellationToken ct)
    {
        var account = await _repo.GetAccountAsync(ctx, accountId, ct);
        if (account is null) return null;
        var password = await _repo.PasswordAsync(ctx, accountId, ct);
        return password is null ? null : _connector.Open(MailSyncer.Resolve(account), password);
    }

    public async Task<string?> FolderAsync(ContextRef ctx, string accountId, string role, CancellationToken ct) =>
        (await _repo.GetFolderAsync(ctx, accountId, role, ct))?.Name;

    /// <summary>Moves messages to their account's Trash on the server (decision
    /// 8, "everywhere"). Not best effort: a refusal is the caller's answer,
    /// before anything here is deleted.</summary>
    public async Task TrashAsync(ContextRef ctx, IReadOnlyList<MailLocationRef> places, CancellationToken ct)
    {
        foreach (var account in places.GroupBy(p => p.AccountId))
        {
            await using var box = await OpenAsync(ctx, account.Key, ct)
                ?? throw new InvalidOperationException("The mail account or its password is gone.");
            foreach (var role in account.GroupBy(p => p.Role))
            {
                var folder = await FolderAsync(ctx, account.Key, role.Key, ct);
                if (folder is not null) await box.TrashAsync(folder, role.Select(p => p.Uid).Distinct().ToList(), ct);
            }
        }
    }

    /// <summary>Writes seen / unseen to every place, per account. Best effort:
    /// a failure is logged — the next sync brings the server's state back.</summary>
    public async Task MarkSeenAsync(ContextRef ctx, IReadOnlyList<MailLocationRef> places, bool seen, CancellationToken ct)
    {
        foreach (var account in places.GroupBy(p => p.AccountId))
        {
            try
            {
                await using var box = await OpenAsync(ctx, account.Key, ct);
                if (box is null) continue;
                foreach (var role in account.GroupBy(p => p.Role))
                {
                    var folder = await FolderAsync(ctx, account.Key, role.Key, ct);
                    if (folder is not null) await box.MarkAsync(folder, role.Select(p => p.Uid).ToList(), seen, null, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Mail: seen not written back for account {AccountId}: {Code}", account.Key, MailSyncer.ErrorCode(ex));
            }
        }
    }
}
