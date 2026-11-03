using Dapper;
using Fishbowl.Core.Models;
using Fishbowl.Mail.Sync;

namespace Fishbowl.Tests.Mail;

// User schema v25: a mail's HTML is stored packed (MailBodies) and mail_fts
// is contentless — search finds exactly what it found before, and a mailbox
// from before v25 is converted on its first open.
public class MailStorageTests : MailFixture
{
    private static readonly string Html =
        "<html><body>" + string.Concat(Enumerable.Repeat("<p style=\"color:#333\">Grüße aus dem Newsletter</p>", 200)) + "</body></html>";

    private string FtsSql() => Factory.CreateContextConnection(Ctx).ExecuteScalar<string>(
        "SELECT sql FROM sqlite_master WHERE name = 'mail_fts'")!;

    [Fact]
    public async Task Html_IsStoredPacked_AndReadBackWhole()
    {
        var acc = await AddAccountAsync();
        Connector.For(acc.Id).AddMessage("inbox", messageId: "<news@x>", subject: "Weekly", text: "alpha words", html: Html);
        await SyncAsync(acc);

        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM mail_messages WHERE body_html IS NOT NULL"));
        Assert.True(Scalar("SELECT length(body_html_z) FROM mail_messages") < Html.Length / 10);
        var m = await ByKeyAsync("news@x");
        Assert.Equal(Html, (await Repo.GetMessageAsync(Ctx, m.Id, Ct))!.BodyHtml);
        Assert.Equal(Html, Assert.Single(await Repo.GetThreadAsync(Ctx, m.ThreadId, Ct)).BodyHtml);

        // A row a trash restore brings back from before v25 keeps its HTML in body_html.
        using (var db = Factory.CreateContextConnection(Ctx))
            db.Execute("UPDATE mail_messages SET body_html = '<p>old</p>', body_html_z = NULL");
        Assert.Equal("<p>old</p>", (await Repo.GetMessageAsync(Ctx, m.Id, Ct))!.BodyHtml);
    }

    [Fact]
    public async Task Search_RunsOnTheContentlessIndex_AndForgetsDeletedMail()
    {
        var acc = await AddAccountAsync();
        var server = Connector.For(acc.Id);
        server.AddMessage("inbox", messageId: "<a@x>", subject: "Invoice", text: "alpha words");
        server.AddMessage("inbox", messageId: "<b@x>", subject: "Other", text: "beta words");
        await SyncAsync(acc);

        Assert.Contains("contentless_delete", FtsSql());
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'mail_fts_content'"));
        Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "alpha" }, Ct));
        Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "invoice" }, Ct));
        Assert.Equal(2, (await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "alice" }, Ct)).Count);

        var a = await ByKeyAsync("a@x");
        await Repo.DeleteMessagesAsync(Ctx, [a.Id], tombstone: true, deletedBy: null, Ct);
        Assert.Empty(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "alpha" }, Ct));
        Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "beta" }, Ct));
    }

    [Fact]
    public async Task MailboxFromBeforeV25_IsConvertedOnItsFirstOpen()
    {
        var acc = await AddAccountAsync();
        Connector.For(acc.Id).AddMessage("inbox", messageId: "<news@x>", subject: "Weekly", text: "alpha words", html: Html);
        await SyncAsync(acc);
        // Back to how v24 stored it: the HTML as text, mail_fts with its own copy.
        using (var db = Factory.CreateContextConnection(Ctx))
            db.Execute(@"
                UPDATE mail_messages SET body_html = @Html, body_html_z = NULL;
                DROP TABLE mail_fts;
                CREATE VIRTUAL TABLE mail_fts USING fts5(subject, people, body);
                INSERT INTO mail_fts (rowid, subject, people, body) SELECT rowid, subject, from_address, body_text FROM mail_messages;
                PRAGMA user_version = 24;", new { Html });

        Assert.Equal(Fishbowl.Data.DatabaseFactory.UserSchemaHead, Scalar("PRAGMA user_version"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM mail_messages WHERE body_html IS NOT NULL"));
        Assert.Contains("contentless_delete", FtsSql());
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'mail_fts_new%'"));
        var m = await ByKeyAsync("news@x");
        Assert.Equal(Html, (await Repo.GetMessageAsync(Ctx, m.Id, Ct))!.BodyHtml);
        Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "alpha" }, Ct));
        Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "weekly" }, Ct));
    }

    // User schema v29: a part sent inline counts as an attachment. Mail stored
    // before that lost the photo the iPhone put inline (no HTML shows it); the
    // migration marks what may have lost one, and the sync reads its body again.
    [Fact]
    public async Task MailFromBeforeV29_GetsItsInlinePhotoBack()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        var photo = s.AddMessage("inbox", messageId: "<photo@x>", subject: "BU Auszahlung", text: "");
        s.AddMessage("inbox", messageId: "<note@x>", subject: "Short note", text: "see you");
        await SyncAsync(acc);
        // As v28 stored it: the photo not listed — and the mail as big as the photo.
        s.ByRole["inbox"].Messages[photo].Body = new SyncBody("", null, [new SyncAttachment(0, "2", "IMG_0042.jpeg", "image/jpeg", 1_400_000)]);
        using (var db = Factory.CreateContextConnection(Ctx))
            db.Execute(@"
                UPDATE mail_messages SET attachments = '[]';
                UPDATE mail_messages SET size = 1400000 WHERE message_key = 'photo@x';
                PRAGMA user_version = 28;");

        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM mail_messages WHERE parts = 0"));   // the small one isn't read again
        await SyncAsync(acc);

        var a = Assert.Single((await ByKeyAsync("photo@x")).Attachments);
        Assert.Equal(("IMG_0042.jpeg", "image/jpeg", "2"), (a.Name, a.Type, a.Part));
        Assert.Empty((await ByKeyAsync("note@x")).Attachments);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM mail_messages WHERE parts = 0"));
    }
}
