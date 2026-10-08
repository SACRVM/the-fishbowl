using Fishbowl.Mail;
using Fishbowl.Mail.Models;
using Fishbowl.Mail.Smtp;
using MimeKit;

namespace Fishbowl.Mail.Tests;

public class MailSenderBuildTests
{
    private static ResolvedAccount Account(string address = "me@example.org", string? name = "Me") =>
        ResolvedAccount.From(new AccountConfig { Id = "a", Provider = "gmail", User = address, DisplayName = name });

    private static string Lf(string? s) => (s ?? string.Empty).Replace("\r\n", "\n");

    [Fact]
    public void BuildMessage_sets_headers_and_bodies()
    {
        var msg = MailSender.BuildMessage(Account(), new SendRequest
        {
            To = ["Alice <alice@example.org>", "bob@example.org"],
            Cc = ["carol@example.org"],
            Subject = "Hi",
            Text = "plain",
            Html = "<p>rich</p>",
        });

        Assert.Equal("\"Me\" <me@example.org>", msg.From.ToString());
        Assert.Equal(2, msg.To.Count);
        Assert.Single(msg.Cc);
        Assert.Equal("Hi", msg.Subject);
        Assert.Equal("plain", msg.TextBody);
        Assert.Equal("<p>rich</p>", msg.HtmlBody);
    }

    [Fact]
    public void BuildMessage_rejects_empty_or_invalid_recipients()
    {
        Assert.Throws<SendBlockedException>(() => MailSender.BuildMessage(Account(), new SendRequest { To = [], Subject = "s", Text = "t" }));
        Assert.Throws<SendBlockedException>(() => MailSender.BuildMessage(Account(), new SendRequest { To = ["not an address"], Subject = "s", Text = "t" }));
    }

    [Fact]
    public void BuildMessage_requires_absolute_existing_attachments()
    {
        Assert.Throws<SendBlockedException>(() => MailSender.BuildMessage(Account(), new SendRequest
        {
            To = ["a@b.c"],
            Subject = "s",
            Text = "t",
            AttachmentPaths = ["relative.txt"],
        }));
        Assert.Throws<SendBlockedException>(() => MailSender.BuildMessage(Account(), new SendRequest
        {
            To = ["a@b.c"],
            Subject = "s",
            Text = "t",
            AttachmentPaths = [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin")],
        }));
    }

    private static MimeMessage Original()
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Alice", "alice@example.org"));
        m.To.Add(new MailboxAddress("Me", "me@example.org"));
        m.To.Add(new MailboxAddress("Bob", "bob@example.org"));
        m.Cc.Add(new MailboxAddress("Carol", "carol@example.org"));
        m.Subject = "Question";
        m.MessageId = "<orig@example.org>";
        m.References.Add("<root@example.org>");
        m.Date = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        m.Body = new TextPart("plain") { Text = "line one\nline two" };
        return m;
    }

    [Fact]
    public void BuildReply_threads_and_quotes()
    {
        var reply = MailSender.BuildReply(Account(), Original(), new ReplyRequest { Folder = "INBOX", Uid = 1, Text = "Answer." });

        Assert.Equal("Re: Question", reply.Subject);
        // MimeKit stores message ids without the angle brackets.
        Assert.Equal("orig@example.org", reply.InReplyTo);
        Assert.Equal(["root@example.org", "orig@example.org"], reply.References.ToArray());
        Assert.Single(reply.To);
        Assert.Equal("alice@example.org", reply.To.Mailboxes.First().Address);
        Assert.Empty(reply.Cc);

        var expected = "Answer.\n\nOn 2026-09-01 10:00, \"Alice\" <alice@example.org> wrote:\n> line one\n> line two";
        Assert.StartsWith(expected, Lf(reply.TextBody));
    }

    [Fact]
    public void BuildReply_all_excludes_self_and_sender_duplicates()
    {
        var reply = MailSender.BuildReply(Account(), Original(), new ReplyRequest { Folder = "INBOX", Uid = 1, Text = "x", ReplyAll = true, QuoteOriginal = false });
        Assert.Equal(["alice@example.org"], reply.To.Mailboxes.Select(m => m.Address).ToArray());
        Assert.Equal(["bob@example.org", "carol@example.org"], reply.Cc.Mailboxes.Select(m => m.Address).ToArray());
        Assert.Equal("x", reply.TextBody);
    }

    [Fact]
    public void BuildReply_does_not_double_Re()
    {
        var o = Original();
        o.Subject = "RE: Question";
        var reply = MailSender.BuildReply(Account(), o, new ReplyRequest { Folder = "INBOX", Uid = 1, Text = "x" });
        Assert.Equal("RE: Question", reply.Subject);
    }

    [Fact]
    public void BuildReply_prefers_ReplyTo()
    {
        var o = Original();
        o.ReplyTo.Add(new MailboxAddress("List", "list@example.org"));
        var reply = MailSender.BuildReply(Account(), o, new ReplyRequest { Folder = "INBOX", Uid = 1, Text = "x" });
        Assert.Equal("list@example.org", reply.To.Mailboxes.Single().Address);
    }

    [Fact]
    public void Messages_get_a_MessageId_in_the_sender_domain()
    {
        var msg = MailSender.BuildMessage(Account(), new SendRequest { To = ["a@b.c"], Subject = "S", Text = "T" });
        Assert.EndsWith("@example.org", msg.MessageId);
        var reply = MailSender.BuildReply(Account(), Original(), new ReplyRequest { Folder = "INBOX", Uid = 1, Text = "x" });
        Assert.EndsWith("@example.org", reply.MessageId);
        Assert.NotEqual(msg.MessageId, reply.MessageId);
    }
}
