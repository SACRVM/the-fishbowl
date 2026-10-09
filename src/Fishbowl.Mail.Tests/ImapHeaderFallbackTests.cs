using System.Text;
using Fishbowl.Mail.Sync;
using MailKit;
using MimeKit;

namespace Fishbowl.Mail.Tests;

// When a server sends a message's ENVELOPE broken, the sync reads its header
// block instead (ImapMailbox.HeadersAsync) — it must say what the ENVELOPE would.
public class ImapHeaderFallbackTests
{
    private static HeaderList Load(string block) =>
        HeaderList.Load(new MemoryStream(Encoding.UTF8.GetBytes(block.ReplaceLineEndings("\r\n") + "\r\n\r\n")));

    [Fact]
    public void HeaderBlock_GivesWhatTheEnvelopeWould()
    {
        var headers = Load("""
            Message-ID: <reply-1@example.org>
            In-Reply-To: <first-1@example.org>
            References: <first-1@example.org> <second-1@example.org>
            From: "Ada Lovelace" <ada@example.org>
            To: Grace <grace@example.org>, bob@example.org
            Cc: carol@example.org
            Reply-To: list@example.org
            Subject: =?utf-8?q?Gr=C3=BC=C3=9Fe?=
            Date: Fri, 25 Sep 2026 10:00:00 +0200
            List-Id: <news.example.org>
            """);
        var h = ImapMailbox.FromHeaders(7, headers, MessageFlags.Seen, DateTimeOffset.Parse("2026-09-25T08:01:00Z"), 1234);

        Assert.Equal(7u, h.Uid);
        Assert.Equal("reply-1@example.org", h.MessageId);
        Assert.Equal("first-1@example.org", h.InReplyTo);
        Assert.Equal(new[] { "first-1@example.org", "second-1@example.org" }, h.References);
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T10:00:00+02:00"), h.Date);
        Assert.Equal(("Ada Lovelace", "ada@example.org"), (h.From!.Name, h.From.Address));
        Assert.Equal(new[] { "grace@example.org", "bob@example.org" }, h.To.Select(a => a.Address));
        Assert.Equal("carol@example.org", Assert.Single(h.Cc).Address);
        Assert.Equal("list@example.org", Assert.Single(h.ReplyTo).Address);
        Assert.Equal("Grüße", h.Subject);
        Assert.True(h.Seen);
        Assert.False(h.Flagged);
        Assert.Equal(1234, h.Size);
        Assert.Equal("<news.example.org>", h.ListId);
    }

    [Fact]
    public void HeaderBlock_ThatIsBroken_StillGivesTheAddress_AndNoDate()
    {
        // The kind of address line that breaks a server's ENVELOPE.
        var h = ImapMailbox.FromHeaders(9, Load("""
            From: thu@mac.com <thu@mac.com>
            Subject: old
            """), MessageFlags.None, null, null);

        Assert.Equal("thu@mac.com", h.From?.Address);
        Assert.Null(h.Date);
        Assert.Null(h.MessageId);
        Assert.Empty(h.References);
    }
}
