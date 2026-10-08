using System;
using System.Collections.Generic;

namespace Fishbowl.Core.Models;

// Mail (spec 2026-10-07-mail-design): a workspace's mailboxes, synced into
// its database (user/space schema v22). IMAP carries the mail; Fishbowl keeps
// a copy and is where it is organised. One message per account and
// Message-ID, wherever it lies on the server (MailLocation) — so a move is a
// change of place, and Gmail's labels are several places of one message.

public static class MailRoles
{
    public const string Inbox = "inbox";
    public const string Sent = "sent";
    public const string Archive = "archive";
    public static readonly IReadOnlyList<string> All = new[] { Inbox, Sent, Archive };
}

public static class MailDirections
{
    public const string In = "in";
    public const string Out = "out";
}

// Where a message stands, from the folders it lies in: in the INBOX → inbox;
// else in Sent → sent; else archived. The one list shows inbox and sent.
public static class MailStates
{
    public const string Inbox = "inbox";
    public const string Sent = "sent";
    public const string Archived = "archived";
}

public static class MailAccountStates
{
    public const string New = "new";         // added, not synced yet
    public const string Ok = "ok";
    public const string Failed = "failed";   // the last sync failed (LastError says how)
}

public record MailPerson(string? Name, string Address);

public record MailAttachment(int Index, string Part, string? Name, string Type, long? Size);

// An account as the API shows it — never its password.
public class MailAccount
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;          // "iCloud", "Club"
    public string Address { get; set; } = string.Empty;       // the From address
    public string? DisplayName { get; set; }                  // the From name
    public List<string> Aliases { get; set; } = new();         // further own addresses (direction)
    public string Provider { get; set; } = "custom";          // icloud | gmail | ionos | custom
    public string Username { get; set; } = string.Empty;
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; }
    public string ImapSecurity { get; set; } = "ssl";         // ssl | starttls | none
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; }
    public string SmtpSecurity { get; set; } = "starttls";
    public string State { get; set; } = MailAccountStates.New;
    public string? LastError { get; set; }                    // a code: auth_failed | unreachable | …
    public DateTime? LastSyncAt { get; set; }
    public bool BackfillDone { get; set; }                    // the whole history is in
    // Where a message came from is a tag too — system-given, never assigned,
    // removed or renamed by hand: the account's name ("icloud", "gmail").
    public string SourceTag => SourceTagFor(Name);
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>The account's source tag: its name in tag letters (a–z, 0–9,
    /// - and _), at most 40 of them — no prefix, a tag like any other to read.</summary>
    public static string SourceTagFor(string? name)
    {
        var slug = System.Text.RegularExpressions.Regex.Replace((name ?? "").Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return slug.Length == 0 ? "mail" : slug;
    }
}

public class MailMessage
{
    public string Id { get; set; } = string.Empty;            // ULID
    public string AccountId { get; set; } = string.Empty;
    // The Message-ID without brackets, or a hash of the headers when it has none.
    public string MessageKey { get; set; } = string.Empty;
    public string ThreadId { get; set; } = string.Empty;
    public string? InReplyTo { get; set; }
    public List<string> Refs { get; set; } = new();
    public string Direction { get; set; } = MailDirections.In;
    public string State { get; set; } = MailStates.Inbox;
    public string? FromName { get; set; }
    public string? FromAddress { get; set; }
    public List<MailPerson> ToList { get; set; } = new();
    public List<MailPerson> CcList { get; set; } = new();
    public List<MailPerson> BccList { get; set; } = new();
    public List<MailPerson> ReplyTo { get; set; } = new();
    public string? Subject { get; set; }
    public DateTime SentAt { get; set; }
    public string? Snippet { get; set; }
    public string? BodyText { get; set; }
    public string? BodyHtml { get; set; }                     // as sent; the client sanitises
    public List<MailAttachment> Attachments { get; set; } = new();
    public long? Size { get; set; }
    public bool Seen { get; set; }
    public bool Flagged { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? ListId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// One row of the list: a conversation.
public class MailThread
{
    public string ThreadId { get; set; } = string.Empty;
    public string? Subject { get; set; }                      // the first message's
    public DateTime LatestAt { get; set; }
    public string LatestDirection { get; set; } = MailDirections.In;
    public string? Snippet { get; set; }                      // the latest message's
    public int Count { get; set; }
    public int Unread { get; set; }
    public bool Flagged { get; set; }
    public bool Archived { get; set; }                        // no message in the inbox or sent
    public bool HasAttachments { get; set; }
    public List<MailPerson> Participants { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public List<string> AccountIds { get; set; } = new();
}

public class MailListQuery
{
    public string? Text { get; set; }          // full text
    public string? AccountId { get; set; }
    public string? Tag { get; set; }
    public List<string> Tags { get; set; } = new();   // all of them (AND); a source tag means its account
    public bool UnreadOnly { get; set; }
    public bool Archived { get; set; }         // the archived threads instead of the list
    public string? Address { get; set; }       // threads with this person (contacts)
    public DateTime? Before { get; set; }      // paging: threads whose latest is older
    public int Limit { get; set; } = 50;
}

// Where a message lies on the server: what a write-back (seen, flagged,
// archive, delete) acts on.
public record MailLocationRef(string AccountId, string Role, uint Uid);

// A partial account update: null leaves a field as it is.
public class MailAccountUpdate
{
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public List<string>? Aliases { get; set; }
    public string? Password { get; set; }
}
