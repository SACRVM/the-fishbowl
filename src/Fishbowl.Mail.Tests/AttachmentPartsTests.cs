using Fishbowl.Mail.Sync;
using MailKit;
using MimeKit;

namespace Fishbowl.Mail.Tests;

// Which parts of a message are its files (ImapMailbox.AttachmentParts): the
// iPhone sends a photo inline, and a mail without HTML has nothing that shows
// it — it was lost when only parts marked "attachment" counted.
public class AttachmentPartsTests
{
    private static BodyPartText Text(string part, string subtype = "plain") => new()
    {
        ContentType = new ContentType("text", subtype) { Charset = "utf-8" },
        ContentTransferEncoding = "7bit",
        PartSpecifier = part,
    };

    private static BodyPartBasic Photo(string part) => new()
    {
        ContentType = new ContentType("image", "jpeg") { Name = "IMG_0042.jpeg" },
        ContentId = "<photo-1@apple>",
        ContentDisposition = new ContentDisposition("inline") { FileName = "IMG_0042.jpeg" },
        ContentTransferEncoding = "base64",
        Octets = 1_400_000,
        PartSpecifier = part,
    };

    private static BodyPartBasic Pdf(string part) => new()
    {
        ContentType = new ContentType("application", "pdf") { Name = "Bescheid.pdf" },
        ContentDisposition = new ContentDisposition("attachment") { FileName = "Bescheid.pdf" },
        ContentTransferEncoding = "base64",
        Octets = 90_000,
        PartSpecifier = part,
    };

    private static BodyPartMultipart Multi(string subtype, string part, params BodyPart[] parts)
    {
        var m = new BodyPartMultipart { ContentType = new ContentType("multipart", subtype), PartSpecifier = part };
        foreach (var p in parts) m.BodyParts.Add(p);
        return m;
    }

    [Fact]
    public void AnInlinePhoto_InAMailWithoutHtml_IsAnAttachment()
    {
        // What the iPhone sends: an empty text, the photo inline, an empty text.
        var text = Text("1");
        var body = Multi("mixed", "", text, Photo("2"), Text("3"));

        var files = ImapMailbox.AttachmentParts(body, text, null, null);

        var photo = Assert.Single(files);
        Assert.Equal("image/jpeg", photo.ContentType.MimeType);
        Assert.Equal("IMG_0042.jpeg", photo.FileName);
        Assert.Equal("2", photo.PartSpecifier);
    }

    [Fact]
    public void AnImageTheHtmlShows_IsNoAttachment_ButOneItDoesntShowIs()
    {
        var (text, html) = (Text("1.1"), Text("1.2.1", "html"));
        var body = Multi("mixed", "",
            Multi("alternative", "1", text, Multi("related", "1.2", html, Photo("1.2.2"))),
            Pdf("2"));

        var shown = ImapMailbox.AttachmentParts(body, text, html, "<p>Look:</p><img src=\"cid:photo-1@apple\">");
        Assert.Equal(["Bescheid.pdf"], shown.Select(p => p.FileName));

        var notShown = ImapMailbox.AttachmentParts(body, text, html, "<p>No picture here</p>");
        Assert.Equal(["IMG_0042.jpeg", "Bescheid.pdf"], notShown.Select(p => p.FileName));
    }

    [Fact]
    public void APartMarkedAsAnAttachment_AlwaysCounts_AndTextWithoutANameNever()
    {
        var text = Text("1");
        var body = Multi("mixed", "", text, Pdf("2"), Text("3"), Text("4", "calendar"));

        var files = ImapMailbox.AttachmentParts(body, text, null, null);

        Assert.Equal(["Bescheid.pdf"], files.Select(p => p.FileName));
    }

    // The text of "text, photo, text" (Apple Mail): every plain part beside
    // the photo, with the files among them, in order — only the first text
    // was kept, and nothing said where a photo stood.
    [Fact]
    public void TheTextAroundInlinePhotos_IsEveryPlainPart_WithTheFiles_InOrder()
    {
        var (first, last) = (Text("1"), Text("5"));
        var body = Multi("mixed", "", first, Photo("2"), Text("3"), Photo("4"), last, Pdf("6"));
        var files = ImapMailbox.AttachmentParts(body, first, null, null);

        Assert.Equal(["1", "2", "3", "4", "5", "6"], ImapMailbox.TextLayout(body, first, files).Select(p => p.PartSpecifier));
    }

    [Fact]
    public void InAnAlternative_TheTextBodyIsTheText()
    {
        var text = Text("1.1");
        var body = Multi("mixed", "", Multi("alternative", "1", text, Text("1.2", "html")), Photo("2"));
        var files = ImapMailbox.AttachmentParts(body, text, null, null);

        Assert.Equal(["1.1"], ImapMailbox.TextLayout(body, text, files).Select(p => p.PartSpecifier));
        Assert.Empty(ImapMailbox.TextLayout(body, null, files));
    }

    // A file between texts is a line of its own holding U+FFFC; one after the
    // last text, or an empty text, is no mark — it follows the text anyway.
    [Fact]
    public void FilesBetweenTexts_AreMarked_WhereTheyStood()
    {
        var (text, inText) = ImapMailbox.Compose([
            new("\r\n", null), new(null, 0), new("Hallo,\r\nhier das Foto:\r\n", null),
            new(null, 1), new("\r\nund noch eins:", null), new(null, 2), new("", null), new(null, 3),
        ]);

        Assert.Equal("\uFFFC\n\nHallo,\r\nhier das Foto:\n\n\uFFFC\n\nund noch eins:", text);
        Assert.Equal([0, 1], inText.Order());
    }

    // Apple Mail's own U+FFFC in a text says where a file went, but not which:
    // it goes, so the marks left are ours, each with its file.
    [Fact]
    public void TheSendersOwnMarks_Go()
    {
        var (text, inText) = ImapMailbox.Compose([new("Anbei die Doku \uFFFC", null), new(null, 0)]);
        Assert.Equal("Anbei die Doku ", text);
        Assert.Empty(inText);

        (text, inText) = ImapMailbox.Compose([new("Hallo\n\n\uFFFC\n\nGruß", null), new(null, 0), new("Ende", null)]);
        Assert.Equal("Hallo\n\n\n\nGruß\n\n\uFFFC\n\nEnde", text);
        Assert.Equal([0], inText);
    }

    [Fact]
    public void OneTextWithAPhotoAfterIt_IsTheTextAlone()
    {
        var (text, inText) = ImapMailbox.Compose([new("", null), new(null, 0), new(" ", null)]);
        Assert.Equal("", text);
        Assert.Empty(inText);

        (text, inText) = ImapMailbox.Compose([new("Gruß\r\n", null), new(null, 0)]);
        Assert.Equal("Gruß", text);
        Assert.Empty(inText);
    }
}
