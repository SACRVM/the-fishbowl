using Fishbowl.Mail.Auth;
using Fishbowl.Mail.Imap;
using Fishbowl.Mail.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace Fishbowl.Mail.Sync;

/// <summary>
/// <see cref="IMailbox"/> over one <see cref="MailboxSession"/> (one IMAP
/// connection, reconnecting once when the link drops). It fetches only what
/// the sync stores: headers and flags in batches, then per message the text
/// and HTML parts by section — attachments stay on the server until asked for.
/// </summary>
public sealed class ImapMailbox : IMailbox
{
    private readonly MailboxSession _session;

    public ImapMailbox(MailboxSession session) => _session = session;

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    public Task<IReadOnlyList<FolderInfo>> ListFoldersAsync(CancellationToken ct) => _session.ListFoldersAsync(false, ct);

    /// <summary>The server folder that plays a role — "inbox", "sent" or
    /// "archive" (Gmail: All Mail) — or null when the account has none.</summary>
    public Task<string?> RoleFolderAsync(string role, CancellationToken ct) => _session.RunAsync<string?>(async (c, t) =>
    {
        if (role == "inbox") return c.Inbox.FullName;
        var (special, names) = role switch
        {
            "sent" => (SpecialFolder.Sent, new[] { "Sent", "Sent Messages", "Sent Items", "Sent Mail", "Gesendet", "Gesendete Objekte", "Gesendete Elemente" }),
            "archive" => (SpecialFolder.Archive, new[] { "Archive", "Archiv" }),
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
        };
        var found = await MailboxSession.FindSpecialFolderAsync(c, special, t).ConfigureAwait(false);
        if (found is null && role == "archive" && c.Capabilities.HasFlag(ImapCapabilities.GMailExt1))
            found = await MailboxSession.FindSpecialFolderAsync(c, SpecialFolder.All, t).ConfigureAwait(false);
        if (found is not null) return found.FullName;
        foreach (var ns in c.PersonalNamespaces)
        {
            var all = await c.GetFoldersAsync(ns, false, t).ConfigureAwait(false);
            var hit = all.FirstOrDefault(f => names.Any(n => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                && !f.Attributes.HasFlag(FolderAttributes.NoSelect));
            if (hit is not null) return hit.FullName;
        }
        return null;
    }, ct);

    public Task<SyncFolderStatus> StatusAsync(string folder, CancellationToken ct) => InFolder(folder, (f, c, t) =>
        Task.FromResult(new SyncFolderStatus(
            f.UidValidity,
            f.UidNext?.Id ?? 1,
            c.Capabilities.HasFlag(ImapCapabilities.CondStore) ? f.HighestModSeq : null,
            f.Count)), ct);

    public Task<IReadOnlyList<uint>> UidsAsync(string folder, CancellationToken ct) => InFolder<IReadOnlyList<uint>>(folder, async (f, _, t) =>
        (await f.SearchAsync(SearchQuery.All, t).ConfigureAwait(false)).Select(u => u.Id).ToList(), ct);

    private static readonly HeaderSet ExtraHeaders = new(["List-Id"]);

    public Task<IReadOnlyList<SyncHeader>> HeadersAsync(string folder, IReadOnlyList<uint> uids, CancellationToken ct) =>
        InFolder<IReadOnlyList<SyncHeader>>(folder, async (f, _, t) =>
        {
            if (uids.Count == 0) return [];
            var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags
                | MessageSummaryItems.InternalDate | MessageSummaryItems.Size | MessageSummaryItems.References)
            { Headers = ExtraHeaders };
            var summaries = await f.FetchAsync(uids.Select(u => new UniqueId(u)).ToList(), request, t).ConfigureAwait(false);
            return summaries.Select(ToHeader).ToList();
        }, ct);

    private static SyncHeader ToHeader(IMessageSummary s)
    {
        var e = s.Envelope;
        static IReadOnlyList<MailAddress> List(InternetAddressList? l) =>
            l is null ? [] : l.Mailboxes.Select(m => new MailAddress(string.IsNullOrWhiteSpace(m.Name) ? null : m.Name, m.Address)).ToList();
        var flags = s.Flags ?? MessageFlags.None;
        return new SyncHeader(
            s.UniqueId.Id,
            Clean(e?.MessageId),
            Clean(e?.InReplyTo),
            s.References?.Select(r => r).ToList() ?? [],
            e?.Date,
            s.InternalDate,
            List(e?.From).FirstOrDefault(),
            List(e?.To), List(e?.Cc), List(e?.Bcc), List(e?.ReplyTo),
            e?.Subject,
            flags.HasFlag(MessageFlags.Seen),
            flags.HasFlag(MessageFlags.Flagged),
            (long?)s.Size,
            s.Headers?["List-Id"]);
    }

    /// <summary>Message-IDs travel with or without their angle brackets; stored without.</summary>
    internal static string? Clean(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var v = id.Trim();
        if (v.StartsWith('<') && v.EndsWith('>')) v = v[1..^1];
        return v.Length == 0 ? null : v;
    }

    public Task<SyncBody> BodyAsync(string folder, uint uid, CancellationToken ct) => InFolder(folder, async (f, _, t) =>
    {
        var id = new UniqueId(uid);
        var s = (await f.FetchAsync([id], MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, t).ConfigureAwait(false))
            .FirstOrDefault() ?? throw new MailException($"Message uid {uid} not found.");
        async Task<string?> Read(BodyPartText? part)
        {
            if (part is null) return null;
            var entity = await f.GetBodyPartAsync(id, part, t).ConfigureAwait(false);
            return (entity as TextPart)?.Text;
        }
        var text = await Read(s.TextBody).ConfigureAwait(false);
        var html = await Read(s.HtmlBody).ConfigureAwait(false);
        var attachments = s.Attachments.Select((a, i) => new SyncAttachment(
            i, a.PartSpecifier, a.FileName, a.ContentType.MimeType, a.Octets)).ToList();
        return new SyncBody(text, html, attachments);
    }, ct);

    public Task<IReadOnlyList<SyncFlags>> FlagsAsync(string folder, ulong? changedSince, CancellationToken ct) =>
        InFolder<IReadOnlyList<SyncFlags>>(folder, async (f, _, t) =>
        {
            if (f.Count == 0) return [];
            var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags);
            if (changedSince is { } since) request.ChangedSince = since;
            var all = await f.FetchAsync(new UniqueIdRange(UniqueId.MinValue, UniqueId.MaxValue), request, t).ConfigureAwait(false);
            return all.Select(s => new SyncFlags(s.UniqueId.Id,
                (s.Flags ?? MessageFlags.None).HasFlag(MessageFlags.Seen),
                (s.Flags ?? MessageFlags.None).HasFlag(MessageFlags.Flagged))).ToList();
        }, ct);

    public Task AttachmentAsync(string folder, uint uid, string part, Stream target, CancellationToken ct) => InFolder(folder, async (f, _, t) =>
    {
        var id = new UniqueId(uid);
        var s = (await f.FetchAsync([id], MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, t).ConfigureAwait(false))
            .FirstOrDefault() ?? throw new MailException($"Message uid {uid} not found.");
        var bodyPart = s.BodyParts.FirstOrDefault(b => b.PartSpecifier == part)
            ?? throw new MailException($"Message uid {uid} has no part {part}.");
        var entity = await f.GetBodyPartAsync(id, bodyPart, t).ConfigureAwait(false);
        switch (entity)
        {
            case MimePart p when p.Content is not null:
                await p.Content.DecodeToAsync(target, t).ConfigureAwait(false);
                break;
            case MessagePart m when m.Message is not null:
                await m.Message.WriteToAsync(target, t).ConfigureAwait(false);
                break;
            default:
                throw new MailException($"Part {part} of uid {uid} has no content.");
        }
        return true;
    }, ct);

    public Task<string?> InlineAsync(string folder, uint uid, string contentId, Stream target, CancellationToken ct) =>
        InFolder<string?>(folder, async (f, _, t) =>
        {
            var id = new UniqueId(uid);
            var s = (await f.FetchAsync([id], MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, t).ConfigureAwait(false))
                .FirstOrDefault();
            var part = s?.BodyParts.FirstOrDefault(b => Clean(b.ContentId) == contentId);
            if (part is null) return null;
            if (await f.GetBodyPartAsync(id, part, t).ConfigureAwait(false) is not MimePart { Content: { } content }) return null;
            await content.DecodeToAsync(target, t).ConfigureAwait(false);
            return part.ContentType.MimeType;
        }, ct);

    public Task MarkAsync(string folder, IReadOnlyList<uint> uids, bool? seen, bool? flagged, CancellationToken ct) =>
        uids.Count == 0 ? Task.CompletedTask : _session.MarkAsync(folder, uids, seen, flagged, ct);

    // "trash" is the folder marked \Trash (iCloud: Deleted Messages, Gmail:
    // [Gmail]/Trash), else one named Trash; an account without one refuses.
    public Task TrashAsync(string folder, IReadOnlyList<uint> uids, CancellationToken ct) =>
        uids.Count == 0 ? Task.CompletedTask : _session.MoveAsync(folder, uids, "trash", ct);

    /// <summary>Runs <paramref name="op"/> with the folder opened read-only, closed after.</summary>
    private Task<T> InFolder<T>(string folder, Func<IMailFolder, ImapClient, CancellationToken, Task<T>> op, CancellationToken ct) =>
        _session.RunAsync(async (c, t) =>
        {
            var f = await MailboxSession.ResolveFolderAsync(c, folder, t).ConfigureAwait(false);
            await f.OpenAsync(FolderAccess.ReadOnly, t).ConfigureAwait(false);
            try { return await op(f, c, t).ConfigureAwait(false); }
            finally { if (f.IsOpen) await f.CloseAsync(false, t).ConfigureAwait(false); }
        }, ct);
}

/// <summary>Opens <see cref="ImapMailbox"/>es with a password the caller holds.</summary>
public sealed class ImapMailboxConnector(ILoggerFactory? loggers = null) : IMailboxConnector
{
    public IMailbox Open(ResolvedAccount account, string password) =>
        new ImapMailbox(new MailboxSession(account, new PasswordAuthenticator(_ => password), new MailLimits(),
            (loggers ?? NullLoggerFactory.Instance).CreateLogger<MailboxSession>()));
}
