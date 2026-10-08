using Fishbowl.Mail.Auth;
using Fishbowl.Mail.Imap;
using Fishbowl.Mail.Models;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace Fishbowl.Mail.Smtp;

/// <summary>
/// Builds outgoing mail (new or threaded reply), transmits it over SMTP and, for providers that do not
/// file sent mail themselves, appends a copy to the Sent folder.
/// </summary>
public sealed class MailSender(ResolvedAccount account, IAuthenticator auth, MailboxSession session, ILogger<MailSender>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<MailSender>.Instance;

    public async Task<SendResult> SendAsync(SendRequest req, CancellationToken ct)
    {
        var msg = BuildMessage(account, req);
        return await TransmitAsync(msg, ct).ConfigureAwait(false);
    }

    public async Task<SendResult> ReplyAsync(ReplyRequest req, CancellationToken ct)
    {
        var original = await session.GetMimeMessageAsync(req.Folder, req.Uid, ct).ConfigureAwait(false);
        var msg = BuildReply(account, original, req);
        return await TransmitAsync(msg, ct).ConfigureAwait(false);
    }

    private async Task<SendResult> TransmitAsync(MimeMessage msg, CancellationToken ct)
    {
        using var smtp = new SmtpClient();
        _log.LogInformation("[{Account}] SMTP connect {Host}:{Port}", account.Id, account.SmtpHost, account.SmtpPort);
        await smtp.ConnectAsync(account.SmtpHost, account.SmtpPort, MailboxSession.ToSocketOptions(account.SmtpSecurity), ct).ConfigureAwait(false);
        try
        {
            await auth.AuthenticateAsync(smtp, account, ct).ConfigureAwait(false);
        }
        catch (AuthenticationException ex)
        {
            throw new MailException($"Account '{account.Id}': SMTP authentication failed ({ex.Message}).", ex);
        }

        await smtp.SendAsync(msg, ct).ConfigureAwait(false);
        await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);

        // Gmail files sent mail itself; other providers need a copy in Sent.
        var note = "Transmitted.";
        if (!account.IsGmail)
        {
            try
            {
                await session.AppendAsync(SpecialFolder.Sent, msg, MessageFlags.Seen, ct).ConfigureAwait(false);
                note += " Copy stored in Sent.";
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[{Account}] could not append to Sent", account.Id);
                note += " (Could not store a copy in Sent: " + ex.Message + ")";
            }
        }
        return Result(account, msg, note);
    }

    // ---------- message building ----------

    internal static MimeMessage BuildMessage(ResolvedAccount account, SendRequest req)
    {
        if (req.To.Count == 0) throw new SendBlockedException("At least one recipient is required.");
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(account.Config.DisplayName ?? string.Empty, account.Address));
        msg.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(DomainOf(account.Address));
        AddAll(msg.To, req.To);
        AddAll(msg.Cc, req.Cc);
        AddAll(msg.Bcc, req.Bcc);
        msg.Subject = req.Subject;
        msg.Body = BuildBody(req.Text, req.Html, req.AttachmentPaths);
        return msg;
    }

    internal static MimeMessage BuildReply(ResolvedAccount account, MimeMessage original, ReplyRequest req)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(account.Config.DisplayName ?? string.Empty, account.Address));
        msg.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(DomainOf(account.Address));

        var replyTargets = original.ReplyTo.Count > 0 ? original.ReplyTo : original.From;
        msg.To.AddRange(replyTargets);

        if (req.ReplyAll)
        {
            var self = account.Address;
            foreach (var m in original.To.Mailboxes.Concat(original.Cc.Mailboxes))
            {
                if (m.Address.Equals(self, StringComparison.OrdinalIgnoreCase)) continue;
                if (msg.To.Mailboxes.Any(x => x.Address.Equals(m.Address, StringComparison.OrdinalIgnoreCase))) continue;
                msg.Cc.Add(m);
            }
        }

        if (msg.To.Count == 0) throw new SendBlockedException("Original message has no sender to reply to.");

        var subject = original.Subject ?? string.Empty;
        msg.Subject = subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? subject : "Re: " + subject;

        if (!string.IsNullOrEmpty(original.MessageId))
        {
            msg.InReplyTo = original.MessageId;
            foreach (var r in original.References) msg.References.Add(r);
            msg.References.Add(original.MessageId);
        }

        var text = req.Text;
        if (req.QuoteOriginal)
        {
            var quoted = !string.IsNullOrWhiteSpace(original.TextBody)
                ? original.TextBody
                : Text.HtmlToText.Convert(original.HtmlBody ?? string.Empty);
            var sender = original.From.Mailboxes.FirstOrDefault()?.ToString() ?? "unknown";
            var stamp = original.Date == default ? "" : original.Date.ToString("yyyy-MM-dd HH:mm");
            var lines = Text.HtmlToText.Normalize(quoted).Split('\n').Select(l => "> " + l);
            text = req.Text.TrimEnd() + $"\n\nOn {stamp}, {sender} wrote:\n" + string.Join('\n', lines);
        }

        msg.Body = BuildBody(text, null, req.AttachmentPaths);
        return msg;
    }

    private static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1 ? address[(at + 1)..] : "localhost";
    }

    private static MimeEntity BuildBody(string text, string? html, IReadOnlyList<string> attachmentPaths)
    {
        var builder = new BodyBuilder { TextBody = text };
        if (!string.IsNullOrWhiteSpace(html)) builder.HtmlBody = html;
        foreach (var p in attachmentPaths)
        {
            if (!Path.IsPathRooted(p)) throw new SendBlockedException($"Attachment path must be absolute: '{p}'.");
            if (!File.Exists(p)) throw new SendBlockedException($"Attachment not found: '{p}'.");
            builder.Attachments.Add(p);
        }
        return builder.ToMessageBody();
    }

    private static void AddAll(InternetAddressList list, IEnumerable<string> addresses)
    {
        foreach (var a in addresses)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            if (!MailboxAddress.TryParse(a, out var mb))
                throw new SendBlockedException($"Invalid address: '{a}'.");
            list.Add(mb);
        }
    }

    private static SendResult Result(ResolvedAccount account, MimeMessage msg, string note)
        => new(account.Id, msg.From.ToString(),
            msg.To.Mailboxes.Select(m => m.Address).ToList(),
            msg.Cc.Mailboxes.Select(m => m.Address).ToList(),
            msg.Bcc.Mailboxes.Select(m => m.Address).ToList(),
            msg.Subject ?? string.Empty, msg.MessageId, note);
}
