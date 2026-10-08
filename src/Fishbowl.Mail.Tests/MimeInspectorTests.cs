using System.Text;
using Fishbowl.Mail.Text;
using MimeKit;

namespace Fishbowl.Mail.Tests;

public class MimeInspectorTests
{
    private const string Dsn = """
        From: Mail Delivery System <mailer-daemon@kundenserver.de>
        To: sender@example.org
        Subject: Mail delivery failed: returning message to sender
        Date: Fri, 25 Sep 2026 10:00:00 +0200
        MIME-Version: 1.0
        Content-Type: multipart/report; report-type=delivery-status; boundary="b1"

        --b1
        Content-Type: text/plain; charset=utf-8

        This message was created automatically by mail delivery software.

        --b1
        Content-Type: message/delivery-status

        Reporting-MTA: dns; mout.kundenserver.de
        Arrival-Date: Fri, 25 Sep 2026 09:59:58 +0200

        Final-Recipient: rfc822; someone@icloud.com
        Action: failed
        Status: 5.7.1
        Diagnostic-Code: smtp; 554 5.7.1 [CS01] Message rejected due to local policy.
         See https://support.apple.com/en-us/HT204137

        --b1
        Content-Type: message/rfc822

        From: waipu.tv <newsletter@waipu.tv>
        To: someone@icloud.com
        Subject: =?utf-8?q?Neu_bei_waipu=2Etv?=
        Date: Thu, 24 Sep 2026 18:30:00 +0200
        Message-Id: <abc123@waipu.tv>
        Content-Type: multipart/alternative; boundary="b2"

        --b2
        Content-Type: text/plain

        plain
        --b2
        Content-Type: text/html

        <p>html</p>
        --b2--

        --b1--
        """;

    private static MimeMessage Parse(string s)
        => MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(s.ReplaceLineEndings("\r\n"))));

    [Fact]
    public void Walk_numbers_parts_like_imap_including_embedded_message()
    {
        var parts = MimeInspector.Describe(Parse(Dsn));
        Assert.Equal(
            [null, "1", "2", "3", null, "3.1", "3.2"],
            parts.Select(p => p.Part));
        Assert.Equal("message/delivery-status", parts[2].ContentType);
        Assert.Equal("message/rfc822", parts[3].ContentType);
        Assert.Equal("text/html", parts[6].ContentType);
    }

    [Fact]
    public void Single_part_message_body_is_part_1()
    {
        var msg = Parse("From: a@b.c\nSubject: x\nContent-Type: text/plain\n\nhello\n");
        var part = Assert.Single(MimeInspector.Describe(msg));
        Assert.Equal("1", part.Part);
        Assert.Equal("hello", MimeInspector.ReadText(MimeInspector.Find(msg, "1")).Trim());
    }

    [Fact]
    public void ParseBounce_extracts_recipient_status_and_original_headers()
    {
        var b = MimeInspector.ParseBounce(Parse(Dsn));
        Assert.NotNull(b);
        Assert.Equal("mout.kundenserver.de", b.ReportingMta);
        var r = Assert.Single(b.Recipients);
        Assert.Equal("someone@icloud.com", r.FinalRecipient);
        Assert.Equal("failed", r.Action);
        Assert.Equal("5.7.1", r.Status);
        Assert.StartsWith("554 5.7.1 [CS01] Message rejected", r.DiagnosticCode);
        Assert.Contains("HT204137", r.DiagnosticCode);

        Assert.NotNull(b.Original);
        Assert.Equal("waipu.tv <newsletter@waipu.tv>", b.Original.From);
        Assert.Equal("Neu bei waipu.tv", b.Original.Subject);
        Assert.Equal("<abc123@waipu.tv>", b.Original.MessageId);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 18, 30, 0, TimeSpan.FromHours(2)), b.Original.Date);
        Assert.False(b.Original.HeadersOnly);
    }

    [Fact]
    public void ParseBounce_reads_text_rfc822_headers_original()
    {
        var s = Dsn.Replace("Content-Type: message/rfc822", "Content-Type: text/rfc822-headers");
        var b = MimeInspector.ParseBounce(Parse(s));
        Assert.Equal("Neu bei waipu.tv", b?.Original?.Subject);
        Assert.True(b?.Original?.HeadersOnly);
        // The header block's own Content-Type must not produce phantom children.
        Assert.Equal([null, "1", "2", "3"], MimeInspector.Describe(Parse(s)).Select(p => p.Part));
    }

    [Fact]
    public void Delivery_status_and_embedded_message_are_readable_as_text()
    {
        var msg = Parse(Dsn);
        Assert.Contains("Final-Recipient: rfc822; someone@icloud.com", MimeInspector.ReadText(MimeInspector.Find(msg, "2")));
        Assert.Contains("Message-Id: <abc123@waipu.tv>", MimeInspector.ReadText(MimeInspector.Find(msg, "3")));
    }

    [Fact]
    public void Plain_message_has_no_bounce()
        => Assert.Null(MimeInspector.ParseBounce(Parse("From: a@b.c\nContent-Type: text/plain\n\nhi\n")));

    [Fact]
    public void Unknown_part_id_throws()
        => Assert.Throws<MailException>(() => MimeInspector.Find(Parse(Dsn), "9"));

    [Theory]
    [InlineData("rfc822; a@b.c", "a@b.c")]
    [InlineData("smtp; 550 no such user", "550 no such user")]
    [InlineData("no prefix here; really", "no prefix here; really")]
    public void StripType_drops_dsn_type_prefix(string input, string expected)
        => Assert.Equal(expected, MimeInspector.StripType(input));
}
