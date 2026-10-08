using System.Net;
using System.Text;
using Markdig;
using MimeKit;
using MimeKit.Utils;

namespace Fishbowl.Mail.Smtp;

/// <summary>A file that goes with a mail.</summary>
public sealed record ComposeFile(string Name, string ContentType, byte[] Content);

/// <summary>
/// What goes below the new text: a reply's quote (<see cref="Quoted"/> — the
/// original's lines marked "&gt; ", its head one line like "On …, Ada wrote:")
/// or a forward's original (its head the forwarded message's From, Date,
/// Subject and To lines, the text as it was).
/// </summary>
public sealed record ComposeQuote(IReadOnlyList<string> Head, string Text, bool Quoted);

public sealed record ComposeRequest
{
    public required MailboxAddress From { get; init; }
    public IReadOnlyList<string> To { get; init; } = [];
    public IReadOnlyList<string> Cc { get; init; } = [];
    public IReadOnlyList<string> Bcc { get; init; } = [];
    public string Subject { get; init; } = "";
    public string Markdown { get; init; } = "";
    public ComposeQuote? Quote { get; init; }
    /// <summary>The Message-ID answered (without brackets) and the chain before it.</summary>
    public string? InReplyTo { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];
    public IReadOnlyList<ComposeFile> Files { get; init; } = [];
    public DateTimeOffset? Date { get; init; }
}

/// <summary>
/// Builds an outgoing mail from markdown (mail spec decision 12): the markdown
/// is the text/plain part as written — it reads as plain text — and its
/// rendering the text/html part. Raw HTML in the markdown is escaped, never
/// passed on; a line break is a line break, as in any mail client. A reply's
/// quote and a forward's original follow the new text in both parts.
/// </summary>
public static class MailComposer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    private const string Font = "font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif;font-size:14px;line-height:1.5";

    public static MimeMessage Build(ComposeRequest r)
    {
        var msg = new MimeMessage();
        msg.From.Add(r.From);
        Add(msg.To, r.To);
        Add(msg.Cc, r.Cc);
        Add(msg.Bcc, r.Bcc);
        if (msg.To.Count + msg.Cc.Count + msg.Bcc.Count == 0)
            throw new SendBlockedException("At least one recipient is required.", "no_recipients");
        msg.Subject = r.Subject;
        msg.Date = r.Date ?? DateTimeOffset.UtcNow;
        msg.MessageId = MimeUtils.GenerateMessageId(DomainOf(r.From.Address));
        if (!string.IsNullOrWhiteSpace(r.InReplyTo))
        {
            msg.InReplyTo = r.InReplyTo;
            foreach (var id in r.References.Append(r.InReplyTo).Distinct()) msg.References.Add(id);
        }

        var builder = new BodyBuilder { TextBody = Text(r.Markdown, r.Quote), HtmlBody = Html(r.Markdown, r.Quote) };
        foreach (var f in r.Files)
            builder.Attachments.Add(f.Name, f.Content, ContentType.TryParse(f.ContentType, out var type) ? type : new ContentType("application", "octet-stream"));
        msg.Body = builder.ToMessageBody();
        return msg;
    }

    /// <summary>The markdown as written, then the quote ("&gt; " lines) or the forwarded original.</summary>
    internal static string Text(string markdown, ComposeQuote? quote)
    {
        var sb = new StringBuilder(markdown.TrimEnd());
        if (quote is null) return sb.ToString();
        sb.Append("\n\n");
        foreach (var line in quote.Head) sb.Append(line).Append('\n');
        if (quote.Quoted)
            foreach (var line in quote.Text.Replace("\r\n", "\n").Split('\n')) sb.Append("> ").Append(line).Append('\n');
        else
            sb.Append('\n').Append(quote.Text);
        return sb.ToString().TrimEnd() + "\n";
    }

    internal static string Html(string markdown, ComposeQuote? quote)
    {
        var sb = new StringBuilder($"<!doctype html><html><head><meta charset=\"utf-8\"></head><body><div style=\"{Font}\">");
        sb.Append(Markdown.ToHtml(markdown, Pipeline));
        if (quote is not null)
        {
            var head = string.Join("<br>", quote.Head.Select(WebUtility.HtmlEncode));
            var text = WebUtility.HtmlEncode(quote.Text.Replace("\r\n", "\n")).Replace("\n", "<br>");
            sb.Append(quote.Quoted
                ? $"<p style=\"margin:1em 0 .5em\">{head}</p><blockquote style=\"margin:0 0 0 .8ex;border-left:1px solid #ccc;padding-left:1ex\">{text}</blockquote>"
                : $"<p style=\"margin:1em 0 .5em\">{head}</p><div>{text}</div>");
        }
        return sb.Append("</div></body></html>").ToString();
    }

    private static void Add(InternetAddressList list, IEnumerable<string> addresses)
    {
        foreach (var a in addresses)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            if (!MailboxAddress.TryParse(a.Trim(), out var mb) || !mb.Address.Contains('@'))
                throw new SendBlockedException($"Invalid address: '{a}'.", "invalid_address");
            list.Add(mb);
        }
    }

    private static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1 ? address[(at + 1)..] : "localhost";
    }
}
