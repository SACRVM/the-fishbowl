namespace Fishbowl.Mail.Models;

public sealed record MailAddress(string? Name, string Address)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

public sealed record FolderInfo(
    string Path,
    string Name,
    int? Unread,
    int? Total,
    string? SpecialUse);

public sealed record AttachmentInfo(
    int Index,
    string? FileName,
    string ContentType,
    long? Size);

/// <summary>Lightweight message header for listings. No body.</summary>
public sealed record MessageSummary(
    uint Uid,
    string Folder,
    DateTimeOffset? Date,
    MailAddress? From,
    IReadOnlyList<MailAddress> To,
    string? Subject,
    bool Seen,
    bool Flagged,
    bool Answered,
    long? Size,
    bool HasAttachments,
    IReadOnlyList<string> Labels,
    string? ThreadId);

/// <summary>Full message with body converted to plain text for agents.</summary>
public sealed record MessageContent(
    uint Uid,
    string Folder,
    string? MessageId,
    DateTimeOffset? Date,
    MailAddress? From,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    IReadOnlyList<MailAddress> ReplyTo,
    string? Subject,
    string Text,
    bool Truncated,
    string TextSource,
    IReadOnlyList<AttachmentInfo> Attachments,
    IReadOnlyList<string> Labels,
    IReadOnlyList<MimePartInfo> Parts,
    BounceInfo? Bounce);

/// <summary>One entity of the MIME tree. Part is the IMAP section id, null for the multipart body of a message.</summary>
public sealed record MimePartInfo(
    string? Part,
    string ContentType,
    int Depth,
    long? Size,
    string? FileName,
    string? Disposition);

/// <summary>Content of one MIME part: inline text, or the path it was saved to.</summary>
public sealed record PartContent(
    uint Uid,
    string Folder,
    string Part,
    string ContentType,
    string? FileName,
    string? Text,
    bool Truncated,
    string? SavedPath);

/// <summary>Full RFC822 source of a message, inline or written to an .eml file.</summary>
public sealed record RawMessage(
    uint Uid,
    string Folder,
    long Size,
    string? Raw,
    bool Truncated,
    string? SavedPath);

/// <summary>Machine-readable part of a delivery status notification (RFC 3464).</summary>
public sealed record BounceInfo(
    string? ReportingMta,
    string? ArrivalDate,
    IReadOnlyList<BounceRecipient> Recipients,
    OriginalHeaders? Original);

public sealed record BounceRecipient(
    string? FinalRecipient,
    string? OriginalRecipient,
    string? Action,
    string? Status,
    string? DiagnosticCode,
    string? RemoteMta);

/// <summary>Headers of the returned original. HeadersOnly is true for text/rfc822-headers.</summary>
public sealed record OriginalHeaders(
    string? From,
    string? To,
    string? Subject,
    DateTimeOffset? Date,
    string? MessageId,
    bool HeadersOnly);

public sealed record ExportedMessage(uint Uid, string Path, long Size);

public sealed record ExportResult(
    string Folder,
    string TargetDir,
    IReadOnlyList<ExportedMessage> Files,
    IReadOnlyList<uint> Missing,
    bool LimitReached);

public sealed record SearchOptions
{
    public string Folder { get; init; } = "INBOX";
    public string? From { get; init; }
    public string? To { get; init; }
    public string? Subject { get; init; }
    public string? Text { get; init; }
    public DateTimeOffset? Since { get; init; }
    public DateTimeOffset? Before { get; init; }
    public bool? Unread { get; init; }
    public bool? Flagged { get; init; }
    /// <summary>Gmail-only raw search syntax (X-GM-RAW), e.g. "from:amazon newer_than:7d has:attachment".</summary>
    public string? GmailQuery { get; init; }
    public int? Limit { get; init; }
}

public sealed record SendRequest
{
    public required IReadOnlyList<string> To { get; init; }
    public IReadOnlyList<string> Cc { get; init; } = [];
    public IReadOnlyList<string> Bcc { get; init; } = [];
    public required string Subject { get; init; }
    public required string Text { get; init; }
    public string? Html { get; init; }
    public IReadOnlyList<string> AttachmentPaths { get; init; } = [];
}

public sealed record ReplyRequest
{
    public required string Folder { get; init; }
    public required uint Uid { get; init; }
    public required string Text { get; init; }
    public bool ReplyAll { get; init; }
    public bool QuoteOriginal { get; init; } = true;
    public IReadOnlyList<string> AttachmentPaths { get; init; } = [];
}

public sealed record SendResult(
    string AccountId,
    string From,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string? MessageId,
    string Note);

public sealed record AccountStatus(
    string Id,
    string Provider,
    string User,
    string Auth,
    bool ReadOnly,
    bool Connected,
    string? Error);
