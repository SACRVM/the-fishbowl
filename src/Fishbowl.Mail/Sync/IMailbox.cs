using Fishbowl.Mail.Models;

namespace Fishbowl.Mail.Sync;

/// <summary>What a folder holds right now (SELECT / STATUS).</summary>
/// <param name="HighestModSeq">Null when the server has no CONDSTORE.</param>
public sealed record SyncFolderStatus(uint UidValidity, uint UidNext, ulong? HighestModSeq, int Count);

/// <summary>A message's headers and flags — everything the sync needs to place
/// it before its body is fetched.</summary>
public sealed record SyncHeader(
    uint Uid,
    string? MessageId,
    string? InReplyTo,
    IReadOnlyList<string> References,
    DateTimeOffset? Date,
    DateTimeOffset? InternalDate,
    MailAddress? From,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    IReadOnlyList<MailAddress> Bcc,
    IReadOnlyList<MailAddress> ReplyTo,
    string? Subject,
    bool Seen,
    bool Flagged,
    long? Size,
    string? ListId);

/// <summary>One attachment as the body structure names it; <see cref="Part"/>
/// is the IMAP section id the content is fetched by.</summary>
public sealed record SyncAttachment(int Index, string Part, string? FileName, string ContentType, long? Size);

/// <summary>The readable parts of a message, as sent (not converted).</summary>
public sealed record SyncBody(string? Text, string? Html, IReadOnlyList<SyncAttachment> Attachments);

public sealed record SyncFlags(uint Uid, bool Seen, bool Flagged);

/// <summary>
/// One account's mailbox as the sync sees it: folders, UIDs, headers, the
/// readable body parts, flags — and an attachment's bytes on demand. One
/// connection; calls are serialised. The IMAP implementation is
/// <see cref="ImapMailbox"/>; tests use an in-memory one.
/// </summary>
public interface IMailbox : IAsyncDisposable
{
    Task<IReadOnlyList<FolderInfo>> ListFoldersAsync(CancellationToken ct);
    /// <summary>The server folder that plays a role — "inbox", "sent" or
    /// "archive" (Gmail: All Mail) — or null when the account has none.</summary>
    Task<string?> RoleFolderAsync(string role, CancellationToken ct);
    Task<SyncFolderStatus> StatusAsync(string folder, CancellationToken ct);
    /// <summary>Every UID in the folder now.</summary>
    Task<IReadOnlyList<uint>> UidsAsync(string folder, CancellationToken ct);
    Task<IReadOnlyList<SyncHeader>> HeadersAsync(string folder, IReadOnlyList<uint> uids, CancellationToken ct);
    Task<SyncBody> BodyAsync(string folder, uint uid, CancellationToken ct);
    /// <summary>Flags of every message, or (CONDSTORE) only of those changed since a mod-sequence.</summary>
    Task<IReadOnlyList<SyncFlags>> FlagsAsync(string folder, ulong? changedSince, CancellationToken ct);
    /// <summary>Writes one attachment's decoded content to <paramref name="target"/>.</summary>
    Task AttachmentAsync(string folder, uint uid, string part, Stream target, CancellationToken ct);
    /// <summary>Sets or clears seen / flagged on the server (null leaves it).</summary>
    Task MarkAsync(string folder, IReadOnlyList<uint> uids, bool? seen, bool? flagged, CancellationToken ct) =>
        throw new NotSupportedException("This mailbox can't change flags.");
}

/// <summary>Opens a mailbox for an account; the password comes from the caller.</summary>
public interface IMailboxConnector
{
    IMailbox Open(ResolvedAccount account, string password);
}
