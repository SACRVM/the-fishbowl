using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Mail.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Mail;

/// <summary>
/// Talks to an account's server outside the sync: an attachment fetched on
/// demand (decision 7), seen and flagged written back (decision 9), archive
/// and back (decision 3). A message's
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
    public Task TrashAsync(ContextRef ctx, IReadOnlyList<MailLocationRef> places, CancellationToken ct) =>
        // Each account is its own server, and a visit is seconds: side by side.
        Task.WhenAll(places.GroupBy(p => p.AccountId).Select(async account =>
        {
            await using var box = await OpenAsync(ctx, account.Key, ct)
                ?? throw new InvalidOperationException("The mail account or its password is gone.");
            foreach (var role in account.GroupBy(p => p.Role))
            {
                var folder = await FolderAsync(ctx, account.Key, role.Key, ct);
                if (folder is not null) await box.TrashAsync(folder, role.Select(p => p.Uid).Distinct().ToList(), ct);
            }
        }));

    /// <summary>
    /// Deletes messages (decision 8): <paramref name="everywhere"/> moves them
    /// to their server's Trash first — a refusal is thrown and nothing here is
    /// deleted —, else only here, marked so the sync never brings them back.
    /// Either way into the trash here; returns how many went.
    /// </summary>
    public async Task<int> DeleteAsync(ContextRef ctx, IReadOnlyList<string> ids, bool everywhere, string? deletedBy, CancellationToken ct)
    {
        if (everywhere) await TrashAsync(ctx, await _repo.LocationsOfAsync(ctx, ids, ct), ct);
        return await _repo.DeleteMessagesAsync(ctx, ids, tombstone: !everywhere, deletedBy, ct);
    }

    // ---- spam (read live, never stored) ----

    public sealed record SpamItem(string AccountId, uint Uid, uint UidValidity, string? FromName, string? FromAddress,
        string? Subject, DateTimeOffset Date, bool Seen);

    public sealed record SpamMessage(SpamItem Item, IReadOnlyList<string> To, string? Text, IReadOnlyList<string> Attachments);

    /// <summary>
    /// The newest mail in every account's spam folder, read live from the
    /// server and never stored — spam stays out of search, tags, threads, the
    /// agents and the trash. An account whose server can't be read is named
    /// in <c>Failed</c>; one without a spam folder has nothing.
    /// </summary>
    public async Task<(IReadOnlyList<SpamItem> Items, IReadOnlyList<string> Failed)> SpamAsync(ContextRef ctx, int perAccount, CancellationToken ct)
    {
        // The accounts side by side: each server takes seconds to sign in to.
        var accounts = await _repo.ListAccountsAsync(ctx, ct);
        var reads = await Task.WhenAll(accounts.Select(a => ReadAsync(a.Id)));
        return (reads.SelectMany(r => r ?? []).OrderByDescending(i => i.Date).ToList(),
            accounts.Where((_, i) => reads[i] is null).Select(a => a.Id).ToList());

        // An account's newest spam; null when its server can't be read.
        async Task<List<SpamItem>?> ReadAsync(string accountId)
        {
            try
            {
                await using var box = await OpenAsync(ctx, accountId, ct);
                if (box is null) return null;
                var folder = await box.RoleFolderAsync(MailRoles.Junk, ct);
                if (folder is null) return [];
                var status = await box.StatusAsync(folder, ct);
                var uids = (await box.UidsAsync(folder, ct)).OrderByDescending(u => u).Take(perAccount).ToList();
                if (uids.Count == 0) return [];
                return (await box.HeadersAsync(folder, uids, ct)).Select(h => new SpamItem(accountId, h.Uid, status.UidValidity,
                    h.From?.Name, h.From?.Address, h.Subject, h.Date ?? h.InternalDate ?? DateTimeOffset.MinValue, h.Seen)).ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Mail: spam not read for account {AccountId}: {Code}", accountId, MailSyncer.ErrorCode(ex));
                return null;
            }
        }
    }

    /// <summary>One spam message as text (an HTML one converted — no HTML is
    /// shown for spam), its attachments by name only; null when it's gone.</summary>
    public async Task<SpamMessage?> SpamMessageAsync(ContextRef ctx, string accountId, uint uid, CancellationToken ct)
    {
        await using var box = await OpenAsync(ctx, accountId, ct);
        var folder = box is null ? null : await box.RoleFolderAsync(MailRoles.Junk, ct);
        if (box is null || folder is null) return null;
        var status = await box.StatusAsync(folder, ct);
        var h = (await box.HeadersAsync(folder, [uid], ct)).FirstOrDefault();
        if (h is null) return null;
        var body = await box.BodyAsync(folder, uid, ct);
        var text = body.Text is { Length: > 0 } t ? Fishbowl.Mail.Text.HtmlToText.Normalize(t)
            : body.Html is { Length: > 0 } html ? Fishbowl.Mail.Text.HtmlToText.Convert(html) : null;
        return new SpamMessage(
            new SpamItem(accountId, h.Uid, status.UidValidity, h.From?.Name, h.From?.Address, h.Subject, h.Date ?? h.InternalDate ?? DateTimeOffset.MinValue, h.Seen),
            h.To.Select(a => string.IsNullOrEmpty(a.Name) ? a.Address : $"{a.Name} <{a.Address}>").ToList(),
            text,
            body.Attachments.Select(a => a.FileName ?? a.ContentType).ToList());
    }

    /// <summary>
    /// Marks every unread message in every account's spam folder read on the
    /// server — the whole folder, not only what the Spam view lists. How many,
    /// and the accounts whose server couldn't be reached (the others are done).
    /// </summary>
    public async Task<(int Messages, IReadOnlyList<string> Failed)> SpamReadAllAsync(ContextRef ctx, CancellationToken ct)
    {
        var accounts = await _repo.ListAccountsAsync(ctx, ct);
        var marked = await Task.WhenAll(accounts.Select(a => ReadAllAsync(a.Id)));
        return (marked.Sum(n => n ?? 0), accounts.Where((_, i) => marked[i] is null).Select(a => a.Id).ToList());

        // An account's unread spam marked read; null when its server can't be reached.
        async Task<int?> ReadAllAsync(string accountId)
        {
            try
            {
                await using var box = await OpenAsync(ctx, accountId, ct);
                if (box is null) return null;
                var folder = await box.RoleFolderAsync(MailRoles.Junk, ct);
                if (folder is null) return 0;
                var unread = (await box.FlagsAsync(folder, null, ct)).Where(f => !f.Seen).Select(f => f.Uid).ToList();
                if (unread.Count > 0) await box.MarkAsync(folder, unread, true, null, ct);
                return unread.Count;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Mail: spam not marked read for account {AccountId}: {Code}", accountId, MailSyncer.ErrorCode(ex));
                return null;
            }
        }
    }

    /// <summary>"Not spam": moved from the spam folder into the inbox on the
    /// server (the provider's filter learns from it); the next sync brings it
    /// in like any mail. False — nothing moved — when the spam folder changed
    /// under the UID (another UIDVALIDITY) or the account has no spam folder.</summary>
    public async Task<bool> NotSpamAsync(ContextRef ctx, string accountId, uint uid, uint uidValidity, CancellationToken ct)
    {
        await using var box = await OpenAsync(ctx, accountId, ct)
            ?? throw new InvalidOperationException("The mail account or its password is gone.");
        var junk = await box.RoleFolderAsync(MailRoles.Junk, ct);
        var inbox = await box.RoleFolderAsync(MailRoles.Inbox, ct);
        if (junk is null || inbox is null || (await box.StatusAsync(junk, ct)).UidValidity != uidValidity) return false;
        await box.MoveAsync(junk, [uid], inbox, ct);
        return true;
    }

    /// <summary>Sends a message from an account (decision 12) and files the
    /// copy in its Sent folder; returns the copy's UID there when it can be
    /// told. Not best effort: a refusal is thrown and nothing went out.</summary>
    public async Task<uint?> SendAsync(ContextRef ctx, string accountId, MimeKit.MimeMessage message, CancellationToken ct)
    {
        await using var box = await OpenAsync(ctx, accountId, ct)
            ?? throw new InvalidOperationException("The mail account or its password is gone.");
        return await box.SendAsync(message, await FolderAsync(ctx, accountId, MailRoles.Sent, ct), ct);
    }

    /// <summary>One attachment of a message, fetched from where it lies on the
    /// server; null when it lies nowhere any more.</summary>
    public async Task<byte[]?> AttachmentAsync(ContextRef ctx, string messageId, MailAttachment attachment, CancellationToken ct)
    {
        var place = (await _repo.LocationsAsync(ctx, messageId, ct)).FirstOrDefault();
        var folder = place is null ? null : await FolderAsync(ctx, place.AccountId, place.Role, ct);
        if (place is null || folder is null) return null;
        await using var box = await OpenAsync(ctx, place.AccountId, ct);
        if (box is null) return null;
        using var buffer = new MemoryStream();
        await box.AttachmentAsync(folder, place.Uid, attachment.Part, buffer, ct);
        return buffer.ToArray();
    }

    /// <summary>Writes seen and / or flagged (null leaves it) to every place,
    /// per account. Best effort: a failure is logged — the next sync brings the
    /// server's state back.</summary>
    public async Task MarkAsync(ContextRef ctx, IReadOnlyList<MailLocationRef> places, bool? seen, bool? flagged, CancellationToken ct)
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
                    if (folder is not null) await box.MarkAsync(folder, role.Select(p => p.Uid).ToList(), seen, flagged, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Mail: flags not written back for account {AccountId}: {Code}", account.Key, MailSyncer.ErrorCode(ex));
            }
        }
    }

    /// <summary>
    /// Archives messages (decision 3), or brings them back to the inbox — on
    /// the server first, then here (<see cref="MailRepository.ApplyArchiveAsync"/>).
    /// Archiving moves whatever lies in an inbox to its account's archive
    /// folder; bringing back moves the archived ones that lie neither in an
    /// inbox nor in Sent. Not best effort: false — nothing done — when an
    /// account has no archive folder; a server's refusal is thrown, after what
    /// moved before it was recorded here.
    /// </summary>
    public async Task<bool> ArchiveAsync(ContextRef ctx, IReadOnlyList<string> ids, bool archived, CancellationToken ct)
    {
        var places = await _repo.PlacesOfAsync(ctx, ids, ct);
        var moving = archived
            ? places.Where(p => p.Role == MailRoles.Inbox).ToList()
            : places.GroupBy(p => p.MessageId)
                .Where(g => !g.Any(p => p.Role is MailRoles.Inbox or MailRoles.Sent))
                .SelectMany(g => g.Where(p => p.Role == MailRoles.Archive)).ToList();
        var (fromRole, toRole) = archived ? (MailRoles.Inbox, MailRoles.Archive) : (MailRoles.Archive, MailRoles.Inbox);
        var folders = new Dictionary<string, (string From, string To)>();
        foreach (var accountId in moving.Select(p => p.AccountId).Distinct())
        {
            var from = await FolderAsync(ctx, accountId, fromRole, ct);
            var to = await FolderAsync(ctx, accountId, toRole, ct);
            if (from is null || to is null) return false;
            folders[accountId] = (from, to);
        }

        var moves = new List<MailMove>();
        try
        {
            foreach (var account in moving.GroupBy(p => p.AccountId))
            {
                await using var box = await OpenAsync(ctx, account.Key, ct)
                    ?? throw new InvalidOperationException("The mail account or its password is gone.");
                var (from, to) = folders[account.Key];
                var uids = account.Select(p => p.Uid).Distinct().ToList();
                var done = archived ? await box.ArchiveAsync(from, uids, to, ct) : await box.UnarchiveAsync(from, uids, to, ct);
                moves.AddRange(account.Select(p => new MailMove(p.MessageId, p.AccountId, fromRole, p.Uid, toRole,
                    done.Uids.TryGetValue(p.Uid, out var uid) ? uid : null, done.SourceKept)));
            }
        }
        catch
        {
            if (moves.Count > 0) await _repo.ApplyArchiveAsync(ctx, [], archived, moves, CancellationToken.None);
            throw;
        }
        await _repo.ApplyArchiveAsync(ctx, ids, archived, moves, ct);
        return true;
    }
}
