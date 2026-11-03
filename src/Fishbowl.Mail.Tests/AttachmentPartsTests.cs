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
}
