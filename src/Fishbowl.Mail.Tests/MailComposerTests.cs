using Fishbowl.Mail.Smtp;
using MimeKit;

namespace Fishbowl.Mail.Tests;

public class MailComposerTests
{
    private static ComposeRequest Request(string markdown, ComposeQuote? quote = null, params string[] to) => new()
    {
        From = new MailboxAddress("Me", "me@example.org"),
        To = to.Length == 0 ? ["you@example.org"] : to,
        Subject = "Hi",
        Markdown = markdown,
        Quote = quote,
    };

    [Fact]
    public void Markdown_is_the_text_part_and_its_rendering_the_html_part()
    {
        var msg = MailComposer.Build(Request("A **bold** line\nand the next"));
        Assert.Equal("A **bold** line\nand the next", msg.TextBody.Replace("\r\n", "\n"));
        Assert.Contains("<strong>bold</strong>", msg.HtmlBody);
        Assert.Contains("line<br />", msg.HtmlBody);
        Assert.EndsWith("@example.org>", "<" + msg.MessageId + ">");
    }

    [Fact]
    public void Raw_html_in_the_markdown_is_escaped_never_passed_on()
    {
        var msg = MailComposer.Build(Request("<img src=x onerror=alert(1)> and <b>tags</b>"));
        Assert.DoesNotContain("<img", msg.HtmlBody);
        Assert.DoesNotContain("<b>", msg.HtmlBody);
        Assert.Contains("&lt;img", msg.HtmlBody);
    }

    [Fact]
    public void A_reply_quotes_the_original_in_both_parts_escaped()
    {
        var quote = new ComposeQuote(["On 2026-10-08 14:37, Ada <ada@example.org> wrote:"], "First\n<script>x</script>", Quoted: true);
        var msg = MailComposer.Build(Request("Thanks", quote));
        var text = msg.TextBody.Replace("\r\n", "\n");
        Assert.Contains("Thanks\n\nOn 2026-10-08 14:37, Ada <ada@example.org> wrote:\n> First\n> <script>x</script>", text);
        Assert.Contains("<blockquote", msg.HtmlBody);
        Assert.Contains("First<br>&lt;script&gt;", msg.HtmlBody);
        Assert.Contains("Ada &lt;ada@example.org&gt; wrote:", msg.HtmlBody);
    }

    [Fact]
    public void No_recipient_or_a_bad_address_is_refused_with_a_code()
    {
        Assert.Equal("no_recipients", Assert.Throws<SendBlockedException>(() => MailComposer.Build(Request("x", null, " "))).Code);
        Assert.Equal("invalid_address", Assert.Throws<SendBlockedException>(() => MailComposer.Build(Request("x", null, "not an address"))).Code);
    }

    [Fact]
    public void Files_go_along_as_attachments_after_the_body()
    {
        var r = Request("See attached") with { Files = [new ComposeFile("a.txt", "text/plain", "hello"u8.ToArray())] };
        var msg = MailComposer.Build(r);
        var part = Assert.Single(msg.Attachments.OfType<MimePart>());
        Assert.Equal("a.txt", part.FileName);
        Assert.Equal("text/plain", part.ContentType.MimeType);
        // multipart/mixed [ alternative(text, html), a.txt ]: the file is part 2.
        Assert.IsType<Multipart>(msg.Body);
        Assert.Equal(2, ((Multipart)msg.Body).Count);
    }
}
