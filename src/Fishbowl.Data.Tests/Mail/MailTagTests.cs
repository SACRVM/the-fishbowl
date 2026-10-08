using Fishbowl.Core.Models;
using Fishbowl.Data.Repositories;

namespace Fishbowl.Tests.Mail;

// Mail's tags are the workspace's: a new one gets its row (and colour) like a
// note's, and renaming or deleting a tag reaches mail as it reaches notes.
public class MailTagTests : MailFixture
{
    [Fact]
    public async Task ThreadTags_AreWorkspaceTags_AndFollowRenameAndDelete()
    {
        var acc = await AddAccountAsync();
        Connector.For(acc.Id).AddMessage("inbox", "<t1@x>", "Receipt", from: "shop@x.test", text: "thanks");
        await SyncAsync(acc);
        var thread = Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery(), Ct));

        Assert.Equal(new[] { "bills" }, await Repo.SetThreadTagsAsync(Ctx, thread.ThreadId, ["Bills"], Ct));
        var tags = new TagRepository(Factory);
        Assert.Contains(await tags.GetAllAsync(Ctx, Ct), t => t.Name == "bills");

        Assert.True(await tags.RenameAsync(Ctx, "bills", "paid", Ct));
        Assert.Equal("Receipt", Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Tag = "paid" }, Ct)).Subject);
        Assert.Empty(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Tag = "bills" }, Ct));

        Assert.True(await tags.DeleteAsync(Ctx, "paid", Ct));
        // What remains is where it came from: the account's source tag, system-given.
        Assert.Equal(new[] { acc.SourceTag }, Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery(), Ct)).Tags);
        Assert.Contains(await tags.GetAllAsync(Ctx, Ct), t => t.Name == acc.SourceTag && t.IsSystem && !t.UserAssignable);
        // A source tag is never given by hand.
        Assert.Equal(new[] { "kept" }, await Repo.SetThreadTagsAsync(Ctx, thread.ThreadId, [acc.SourceTag, "kept"], Ct));
        Assert.Equal("Receipt", Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Tags = [acc.SourceTag, "kept"] }, Ct)).Subject);

        // No thread, no tag row.
        Assert.Null(await Repo.SetThreadTagsAsync(Ctx, "no-such-thread", ["ghost"], Ct));
        Assert.DoesNotContain(await tags.GetAllAsync(Ctx, Ct), t => t.Name == "ghost");
    }
}
