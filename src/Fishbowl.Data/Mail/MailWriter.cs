using System.Globalization;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Fishbowl.Mail;
using Fishbowl.Mail.Smtp;
using MimeKit;

namespace Fishbowl.Data.Mail;

/// <summary>A refusal while writing or sending mail — the caller answers with its status and code.</summary>
public sealed class MailWriteException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// Writing mail (mail spec decision 12). A draft is made — a reply's
/// recipients, subject and conversation from the message it answers, a
/// forward's attachments from its original —, filled as it is typed, given
/// files (uploaded or taken from Files), and sent: built from its markdown
/// (<see cref="MailComposer"/>) with the quote or forwarded original under it,
/// handed to the account's server, the copy filed in Sent, and at once in its
/// conversation here. What a person and an agent can do is the same; an
/// agent's key is recorded as <c>sent_by</c>.
/// </summary>
public sealed class MailWriter
{
    /// <summary>What goes with one mail, every file together — most providers' limit.</summary>
    public const long MaxBytes = 25L * 1024 * 1024;

    private static readonly string[] RePrefixes = ["re:", "aw:", "sv:", "antw:"];
    private static readonly string[] FwdPrefixes = ["fwd:", "fw:", "wg:", "wtr:"];

    private readonly MailRepository _mail;
    private readonly MailDraftRepository _drafts;
    private readonly MailServer _server;
    private readonly ISystemRepository _system;
    private readonly IFileService _files;
    private readonly MailRuleRepository? _rules;

    public MailWriter(MailRepository mail, MailDraftRepository drafts, MailServer server, ISystemRepository system, IFileService files,
        MailRuleRepository? rules = null)
    {
        _mail = mail;
        _drafts = drafts;
        _server = server;
        _system = system;
        _files = files;
        _rules = rules;
    }

    public async Task<MailDraft> CreateAsync(ContextRef ctx, string userId, string? kind, string? refMessageId, string? accountId, CancellationToken ct)
    {
        kind ??= MailDraftKinds.New;
        if (!MailDraftKinds.All.Contains(kind))
            throw new MailWriteException(400, "draft_kind_invalid", "A draft is new, reply, reply-all or forward.");
        var accounts = await _mail.ListAccountsAsync(ctx, ct);
        if (accountId is not null && accounts.All(a => a.Id != accountId))
            throw new MailWriteException(400, "mail_account_unknown", "There is no such mail account here.");
        var d = new MailDraft { Kind = kind, CreatedBy = userId, AccountId = accountId ?? accounts.FirstOrDefault()?.Id };
        var forwarded = new List<(string, MailAttachment)>();
        if (kind != MailDraftKinds.New)
        {
            var m = refMessageId is null ? null : await _mail.GetMessageAsync(ctx, refMessageId, ct)
                ?? throw new MailWriteException(404, "mail_message_not_found", "There is no such message here.");
            if (m is null) throw new MailWriteException(400, "draft_needs_message", "A reply or forward names the message it answers.");
            var account = accounts.FirstOrDefault(a => a.Id == m.AccountId);
            if (accountId is null && account is not null) d.AccountId = account.Id;
            d.RefMessageId = m.Id;
            // The conversation it belongs to — a forward joins its original's too.
            d.ThreadId = m.ThreadId;
            if (kind == MailDraftKinds.Forward)
            {
                d.Subject = Prefixed("Fwd: ", m.Subject, FwdPrefixes);
                forwarded.AddRange(m.Attachments.Select(a => (m.Id, a)));
            }
            else
            {
                d.Subject = Prefixed("Re: ", m.Subject, RePrefixes);
                var own = Own(account);
                // Answering what I sent goes to whom I sent it to.
                var outgoing = m.Direction == MailDirections.Out;
                var to = outgoing ? m.ToList
                    : m.ReplyTo.Count > 0 ? m.ReplyTo
                    : m.FromAddress is { } from ? [new MailPerson(m.FromName, from)] : [];
                d.To = Distinct(to, []);
                if (kind == MailDraftKinds.ReplyAll)
                {
                    var taken = to.Select(p => p.Address.ToLowerInvariant()).Concat(own).ToHashSet();
                    d.Cc = Distinct(outgoing ? m.CcList : m.ToList.Concat(m.CcList), taken);
                }
            }
        }
        return await _drafts.CreateAsync(ctx, d, forwarded, ct);
    }

    public async Task<bool> UpdateAsync(ContextRef ctx, string id, MailDraftUpdate u, CancellationToken ct)
    {
        if (u.AccountId is not null && await _mail.GetAccountAsync(ctx, u.AccountId, ct) is null)
            throw new MailWriteException(400, "mail_account_unknown", "There is no such mail account here.");
        return await _drafts.UpdateAsync(ctx, id, u, ct);
    }

    /// <summary>A file for the draft; null when there is no such draft.</summary>
    public async Task<MailDraftFile?> AddFileAsync(ContextRef ctx, string id, string? name, string? type, byte[] data, CancellationToken ct)
    {
        if (await _drafts.FilesBytesAsync(ctx, id, ct) + data.LongLength > MaxBytes)
            throw new MailWriteException(413, "mail_too_large", "A mail's files may be 25 MB together.");
        var clean = Path.GetFileName((name ?? "").Replace('\\', '/').Trim());
        if (clean.Length == 0) clean = "attachment";
        return await _drafts.AddFileAsync(ctx, id, clean, string.IsNullOrWhiteSpace(type) ? "application/octet-stream" : type, data, ct);
    }

    /// <summary>A file of the workspace's Files for the draft; null when there is no such draft.</summary>
    public async Task<MailDraftFile?> AddFromFilesAsync(ContextRef ctx, string id, string path, CancellationToken ct)
    {
        var left = MaxBytes - await _drafts.FilesBytesAsync(ctx, id, ct);
        var handle = await _files.OpenReadAsync(ctx, path, ct);
        await using (handle.Stream)
        {
            if (handle.Entry.Size is { } size && size > left)
                throw new MailWriteException(413, "mail_too_large", "A mail's files may be 25 MB together.");
            using var buffer = new MemoryStream();
            await handle.Stream.CopyToAsync(buffer, ct);
            return await AddFileAsync(ctx, id, handle.Entry.Name, handle.Mime, buffer.ToArray(), ct);
        }
    }

    /// <summary>
    /// Sends the draft and deletes it; returns the sent message here and its
    /// conversation. The quote's words follow <paramref name="language"/> (else
    /// the sender's), its time <paramref name="timeZone"/> (an IANA zone; else UTC).
    /// </summary>
    public async Task<(string MessageId, string ThreadId)> SendAsync(ContextRef ctx, string userId, string draftId, string? sentBy,
        string? timeZone, string? language, CancellationToken ct)
    {
        var d = await _drafts.GetAsync(ctx, draftId, ct)
            ?? throw new MailWriteException(404, "draft_not_found", "There is no such draft.");
        var account = d.AccountId is null ? null : await _mail.GetAccountAsync(ctx, d.AccountId, ct)
            ?? throw new MailWriteException(400, "draft_no_account", "The draft has no mail account to send from.");
        if (account is null) throw new MailWriteException(400, "draft_no_account", "The draft has no mail account to send from.");

        var original = d.RefMessageId is null ? null : await _mail.GetMessageAsync(ctx, d.RefMessageId, ct);
        string? inReplyTo = null;
        var refs = new List<string>();
        ComposeQuote? quote = null;
        if (original is not null)
        {
            var user = await _system.GetUserAsync(userId, ct);
            var words = Words(language ?? user?.Language);
            var when = When(original.SentAt, timeZone, user?.DateFormat);
            var sender = Person(original.FromName, original.FromAddress ?? "");
            var text = original.BodyText ?? "";
            if (d.Kind == MailDraftKinds.Forward)
            {
                var head = new List<string> { words.Forwarded, $"{words.From}: {sender}", $"{words.Date}: {when}", $"{words.Subject}: {original.Subject}" };
                if (original.ToList.Count > 0) head.Add($"{words.To}: {string.Join(", ", original.ToList.Select(p => Person(p.Name, p.Address)))}");
                quote = new ComposeQuote(head, text, Quoted: false);
            }
            else
            {
                quote = new ComposeQuote([string.Format(CultureInfo.InvariantCulture, words.Wrote, when, sender)], text, Quoted: true);
                // A header hash ("fb-…@fishbowl") is ours, not a Message-ID anyone else knows.
                if (!original.MessageKey.StartsWith("fb-", StringComparison.Ordinal))
                {
                    inReplyTo = original.MessageKey;
                    refs.AddRange(original.Refs.Where(r => !r.StartsWith("fb-", StringComparison.Ordinal)));
                }
            }
        }

        var files = new List<ComposeFile>();
        foreach (var f in await _drafts.FileContentsAsync(ctx, draftId, ct))
        {
            var bytes = f.Data;
            if (f.SourceMessageId is not null)
            {
                var from = await _mail.GetMessageAsync(ctx, f.SourceMessageId, ct);
                var attachment = from?.Attachments.FirstOrDefault(a => a.Index == f.SourceIndex);
                bytes = attachment is null ? null : await _server.AttachmentAsync(ctx, from!.Id, attachment, ct);
                if (bytes is null)
                    throw new MailWriteException(410, "attachment_gone", "A forwarded attachment is no longer on the mail server.");
            }
            files.Add(new ComposeFile(f.File.Name, f.File.Type, bytes ?? []));
        }
        if (files.Sum(f => (long)f.Content.Length) > MaxBytes)
            throw new MailWriteException(413, "mail_too_large", "A mail's files may be 25 MB together.");

        MimeMessage msg;
        try
        {
            msg = MailComposer.Build(new ComposeRequest
            {
                From = new MailboxAddress(account.DisplayName ?? "", account.Address),
                To = d.To,
                Cc = d.Cc,
                Bcc = d.Bcc,
                Subject = d.Subject,
                Markdown = d.Body,
                Quote = quote,
                InReplyTo = inReplyTo,
                References = refs,
                Files = files,
            });
        }
        catch (SendBlockedException ex)
        {
            throw new MailWriteException(400, ex.Code, ex.Message);
        }

        var sentUid = await _server.SendAsync(ctx, account.Id, msg, ct);

        // Out: at once in its conversation (the sync may already have found it in Sent).
        var key = MailThreading.CleanId(msg.MessageId)!;
        string id, threadId;
        if (await _mail.FindByKeyAsync(ctx, account.Id, key, ct) is { } found)
        {
            id = found;
            threadId = (await _mail.GetMessageAsync(ctx, found, ct))?.ThreadId ?? found;
        }
        else
        {
            var message = new MailMessage
            {
                AccountId = account.Id,
                MessageKey = key,
                InReplyTo = inReplyTo,
                Refs = refs,
                Direction = MailDirections.Out,
                FromName = account.DisplayName,
                FromAddress = account.Address,
                ToList = People(msg.To),
                CcList = People(msg.Cc),
                BccList = People(msg.Bcc),
                Subject = msg.Subject,
                SentAt = msg.Date.UtcDateTime,
                Snippet = MailThreading.Snippet(d.Body),
                BodyText = msg.TextBody,
                BodyHtml = msg.HtmlBody,
                // As the server's body structure numbers them: the text and
                // HTML are part 1, the files 2, 3, …
                Attachments = files.Select((f, i) => new MailAttachment(i, (i + 2).ToString(CultureInfo.InvariantCulture), f.Name, f.ContentType, f.Content.LongLength)).ToList(),
                Seen = true,
            };
            // What goes out meets the rules too — their tags; it is sent, not in an inbox.
            if (_rules is not null)
            {
                var tags = MailRules.For(await _rules.ListAsync(ctx, ct), message).Tags;
                if (tags.Count > 0) message.Tags = (await _rules.EnsureTagsAsync(ctx, tags, ct)).ToList();
            }
            id = await _mail.InsertSentAsync(ctx, message, sentUid, sentBy, ct);
            threadId = message.ThreadId;
        }
        await _drafts.DeleteAsync(ctx, draftId, ct);
        return (id, threadId);
    }

    private static HashSet<string> Own(MailAccount? account) =>
        account is null ? [] : account.Aliases.Append(account.Address).Select(a => a.Trim().ToLowerInvariant()).ToHashSet();

    private static List<string> Distinct(IEnumerable<MailPerson> people, HashSet<string> leaveOut)
    {
        var seen = new HashSet<string>(leaveOut);
        return people.Where(p => !string.IsNullOrWhiteSpace(p.Address) && seen.Add(p.Address.Trim().ToLowerInvariant()))
            .Select(p => Person(p.Name, p.Address)).ToList();
    }

    // "Ada Lovelace <ada@example.org>" as a person reads it; a name with a
    // comma or other address punctuation in quotes, so it parses back.
    private static string Person(string? name, string address)
    {
        if (string.IsNullOrWhiteSpace(name)) return address;
        var n = name.Trim();
        return n.IndexOfAny([',', ';', '<', '>', '@', '"', '(', ')', '[', ']', ':', '\\']) < 0
            ? $"{n} <{address}>"
            : $"\"{n.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" <{address}>";
    }

    private static List<MailPerson> People(InternetAddressList list) =>
        list.Mailboxes.Select(m => new MailPerson(string.IsNullOrEmpty(m.Name) ? null : m.Name, m.Address)).ToList();

    private static string Prefixed(string prefix, string? subject, string[] known)
    {
        var s = (subject ?? "").Trim();
        return known.Any(k => s.StartsWith(k, StringComparison.OrdinalIgnoreCase)) ? s : prefix + s;
    }

    private sealed record QuoteWords(string Wrote, string Forwarded, string From, string Date, string Subject, string To);

    private static QuoteWords Words(string? language) => language?.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true
        ? new("Am {0} schrieb {1}:", "---------- Weitergeleitete Nachricht ----------", "Von", "Datum", "Betreff", "An")
        : new("On {0}, {1} wrote:", "---------- Forwarded message ----------", "From", "Date", "Subject", "To");

    /// <summary>The original's time in the writer's zone and date format.</summary>
    internal static string When(DateTime utc, string? timeZone, string? dateFormat)
    {
        var zone = timeZone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var z) ? z : TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTimeFromUtc(TimeUtil.AsUtc(utc), zone);
        var pattern = dateFormat switch
        {
            "de" => "dd.MM.yyyy HH:mm",
            "uk" => "dd/MM/yyyy HH:mm",
            "us" => "MM/dd/yyyy h:mm tt",
            _ => "yyyy-MM-dd HH:mm",
        };
        return local.ToString(pattern, CultureInfo.InvariantCulture);
    }
}
