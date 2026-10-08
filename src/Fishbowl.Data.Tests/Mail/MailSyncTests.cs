using Fishbowl.Core.Models;

namespace Fishbowl.Tests.Mail;

public class MailSyncTests : MailFixture
{
    private static DateTimeOffset D(int day) => new(2026, 3, day, 12, 0, 0, TimeSpan.Zero);

    // ───────── first sync and backfill ─────────

    [Fact]
    public async Task FirstSync_BackfillsNewestFirst_InPassesOf200()
    {
        var acc = await AddAccountAsync();
        var server = Connector.For(acc.Id);
        for (var i = 1; i <= 450; i++) server.AddMessage("inbox", subject: $"S{i}");

        var r1 = await SyncAsync(acc);
        Assert.True(r1.Ok);
        Assert.Equal(200, r1.Added);
        Assert.False(r1.BackfillDone);
        Assert.Equal(200, Scalar("SELECT COUNT(*) FROM mail_messages"));
        Assert.Equal(251, Scalar("SELECT MIN(uid) FROM mail_locations"));
        Assert.Equal(450, Scalar("SELECT MAX(uid) FROM mail_locations"));
        Assert.False((await Repo.GetAccountAsync(Ctx, acc.Id, Ct))!.BackfillDone);

        var r2 = await SyncAsync(acc);
        Assert.Equal(200, r2.Added);
        Assert.False(r2.BackfillDone);
        Assert.Equal(51, Scalar("SELECT MIN(uid) FROM mail_locations"));

        var r3 = await SyncAsync(acc);
        Assert.Equal(50, r3.Added);
        Assert.True(r3.BackfillDone);
        Assert.Equal(450, Scalar("SELECT COUNT(*) FROM mail_messages"));

        var stored = await Repo.GetAccountAsync(Ctx, acc.Id, Ct);
        Assert.True(stored!.BackfillDone);
        Assert.Equal(MailAccountStates.Ok, stored.State);
        Assert.Null(stored.LastError);
        Assert.NotNull(stored.LastSyncAt);

        // A fourth pass finds nothing to do.
        var r4 = await SyncAsync(acc);
        Assert.Equal(0, r4.Added);
        Assert.True(r4.BackfillDone);
    }

    [Fact]
    public async Task NewMailBetweenPasses_IsPickedUp_WhileHistoryStillFills()
    {
        var acc = await AddAccountAsync();
        var server = Connector.For(acc.Id);
        for (var i = 1; i <= 450; i++) server.AddMessage("inbox", subject: $"S{i}");
        await SyncAsync(acc);

        var fresh = server.AddMessage("inbox", messageId: "<fresh@x>", subject: "Brand new");
        var r2 = await SyncAsync(acc);

        Assert.Equal(200, r2.Added);
        Assert.False(r2.BackfillDone);
        Assert.Equal(400, Scalar("SELECT COUNT(*) FROM mail_messages"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_locations WHERE uid = {fresh}"));
        Assert.NotNull(await ByKeyAsync("fresh@x"));

        var r3 = await SyncAsync(acc);
        Assert.True(r3.BackfillDone);
        Assert.Equal(451, Scalar("SELECT COUNT(*) FROM mail_messages"));
    }

    [Fact]
    public async Task AbsentRole_IsSkipped()
    {
        var acc = await AddAccountAsync();
        var server = Connector.For(acc.Id);
        server.RemoveRole("archive");
        server.AddMessage("inbox");

        var r = await SyncAsync(acc);

        Assert.True(r.Ok);
        Assert.True(r.BackfillDone);
        Assert.Equal(1, r.Added);
    }

    // ───────── threads ─────────

    [Fact]
    public async Task Reply_JoinsItsParentsThread()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        s.AddMessage("inbox", "<p@x>", "Plan", date: D(1));
        s.AddMessage("inbox", "<r@x>", "Re: Plan", inReplyTo: "<p@x>", references: ["<p@x>"], date: D(2));
        await SyncAsync(acc);

        var p = await ByKeyAsync("p@x");
        var r = await ByKeyAsync("r@x");
        Assert.Equal(p.ThreadId, r.ThreadId);
        Assert.Equal(2, (await Repo.GetThreadAsync(Ctx, p.ThreadId, Ct)).Count);
    }

    [Fact]
    public async Task ChildStoredBeforeParent_EndsUpInOneThread()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        // History is read newest first: the reply (higher UID) is stored before its parent.
        s.AddMessage("inbox", "<parent@x>", "Topic", date: D(1));
        s.AddMessage("inbox", "<child@x>", "Re: Topic", inReplyTo: "<parent@x>", date: D(2));
        await SyncAsync(acc);

        Assert.Equal((await ByKeyAsync("parent@x")).ThreadId, (await ByKeyAsync("child@x")).ThreadId);
    }

    [Fact]
    public async Task MessageBridgingTwoThreads_MergesThem()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        s.AddMessage("inbox", "<a@x>", "Alpha", from: "a@one.test", date: D(1));
        s.AddMessage("inbox", "<b@x>", "Beta", from: "b@two.test", date: D(2));
        await SyncAsync(acc);
        Assert.NotEqual((await ByKeyAsync("a@x")).ThreadId, (await ByKeyAsync("b@x")).ThreadId);

        s.AddMessage("inbox", "<c@x>", "Gamma", references: ["<a@x>", "<b@x>"], date: D(3));
        await SyncAsync(acc);

        var ta = (await ByKeyAsync("a@x")).ThreadId;
        Assert.Equal(ta, (await ByKeyAsync("b@x")).ThreadId);
        Assert.Equal(ta, (await ByKeyAsync("c@x")).ThreadId);
        Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery(), Ct));
    }

    [Fact]
    public async Task HeaderlessReply_JoinsBySubject_WhenAnAddressIsShared()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        s.AddMessage("inbox", "<o@x>", "Project plan", from: "alice@example.com", date: D(1));
        await SyncAsync(acc);
        s.AddMessage("inbox", "<h@x>", "AW: Project   plan", from: "alice@example.com", date: D(10));
        await SyncAsync(acc);

        Assert.Equal((await ByKeyAsync("o@x")).ThreadId, (await ByKeyAsync("h@x")).ThreadId);
    }

    [Fact]
    public async Task SameSubject_WithoutSharedAddress_StaysItsOwnThread()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        s.AddMessage("inbox", "<o@x>", "Project plan", from: "alice@example.com", to: "me@example.com", date: D(1));
        await SyncAsync(acc);
        s.AddMessage("inbox", "<h@x>", "Re: Project plan", from: "bob@other.test", to: "carol@other.test", date: D(10));
        await SyncAsync(acc);

        Assert.NotEqual((await ByKeyAsync("o@x")).ThreadId, (await ByKeyAsync("h@x")).ThreadId);
    }

    [Fact]
    public async Task SameSubject_OutsideTheWindow_StaysItsOwnThread()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        s.AddMessage("inbox", "<o@x>", "Project plan", date: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await SyncAsync(acc);
        s.AddMessage("inbox", "<h@x>", "Re: Project plan", date: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        await SyncAsync(acc);

        Assert.NotEqual((await ByKeyAsync("o@x")).ThreadId, (await ByKeyAsync("h@x")).ThreadId);
    }

    [Fact]
    public async Task SameMessageInTwoAccounts_ShowsOnce_OutgoingCopyWins()
    {
        var a = await AddAccountAsync("me@example.com", name: "A");
        var b = await AddAccountAsync("you@example.org", name: "B");
        Connector.For(a.Id).AddMessage("sent", "<hi@x>", "Hi", from: "me@example.com", to: "you@example.org", date: D(1));
        Connector.For(b.Id).AddMessage("inbox", "<hi@x>", "Hi", from: "me@example.com", to: "you@example.org", date: D(1));
        await SyncAsync(a);
        await SyncAsync(b);

        var threads = await Repo.ListThreadsAsync(Ctx, new MailListQuery(), Ct);
        var t = Assert.Single(threads);
        Assert.Equal(1, t.Count);
        Assert.Equal(new[] { a.Id }, t.AccountIds); // the shown copy's account only
        var msgs = await Repo.GetThreadAsync(Ctx, t.ThreadId, Ct);
        var m = Assert.Single(msgs);
        Assert.Equal(MailDirections.Out, m.Direction);
        Assert.Equal(a.Id, m.AccountId);
    }

    // ───────── direction and list ─────────

    [Fact]
    public async Task Direction_FromOwnAddressAliasOrSent_IsOut()
    {
        var acc = await AddAccountAsync(aliases: ["alias@example.com"]);
        var s = Connector.For(acc.Id);
        s.AddMessage("inbox", "<own@x>", "One", from: "ME@example.com");
        s.AddMessage("inbox", "<ali@x>", "Two", from: "alias@example.com");
        s.AddMessage("inbox", "<in@x>", "Three", from: "alice@example.com");
        s.AddMessage("sent", "<sent@x>", "Four", from: "somebody@else.test");
        await SyncAsync(acc);

        Assert.Equal(MailDirections.Out, (await ByKeyAsync("own@x")).Direction);
        Assert.Equal(MailDirections.Out, (await ByKeyAsync("ali@x")).Direction);
        Assert.Equal(MailDirections.In, (await ByKeyAsync("in@x")).Direction);
        Assert.Equal(MailDirections.Out, (await ByKeyAsync("sent@x")).Direction);
    }

    [Fact]
    public async Task List_ShowsInboxAndSent_ArchivedFlagShowsArchiveOnly()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        s.AddMessage("inbox", "<i@x>", "Inbox thread");
        s.AddMessage("sent", "<s@x>", "Sent thread", from: "me@example.com");
        s.AddMessage("archive", "<a@x>", "Archive thread");
        await SyncAsync(acc);

        var list = await Repo.ListThreadsAsync(Ctx, new MailListQuery(), Ct);
        Assert.Equal(new[] { "Inbox thread", "Sent thread" }, list.Select(t => t.Subject).OrderBy(x => x).ToArray());
        Assert.All(list, t => Assert.False(t.Archived));

        var archived = await Repo.ListThreadsAsync(Ctx, new MailListQuery { Archived = true }, Ct);
        var only = Assert.Single(archived);
        Assert.Equal("Archive thread", only.Subject);
        Assert.True(only.Archived);
    }

    [Fact]
    public async Task List_Filters_UnreadTagAddressText()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        s.AddMessage("inbox", "<1@x>", "Invoice March", from: "billing@shop.test", seen: true, text: "please pay the zebra", date: D(1));
        s.AddMessage("inbox", "<2@x>", "Holiday photos", from: "alice@example.com", seen: false, text: "see attached", date: D(2));
        s.AddMessage("inbox", "<3@x>", "Meeting", from: "bob@work.test", seen: false, text: "agenda inside", date: D(3));
        await SyncAsync(acc);

        var unread = await Repo.ListThreadsAsync(Ctx, new MailListQuery { UnreadOnly = true }, Ct);
        Assert.Equal(new[] { "Holiday photos", "Meeting" }, unread.Select(t => t.Subject).OrderBy(x => x).ToArray());

        var holiday = unread.Single(t => t.Subject == "Holiday photos");
        var tags = await Repo.SetThreadTagsAsync(Ctx, holiday.ThreadId, ["family", " family ", "fun"], Ct);
        Assert.Equal(new[] { "family", "fun" }, tags);
        Assert.Equal("Holiday photos", Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Tag = "family" }, Ct)).Subject);
        Assert.Empty(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Tag = "nope" }, Ct));
        Assert.Null(await Repo.SetThreadTagsAsync(Ctx, "no-such-thread", ["x"], Ct));

        Assert.Equal("Invoice March", Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Address = "Billing@Shop.test" }, Ct)).Subject);

        // Subject, people and body are all searched.
        Assert.Equal("Invoice March", Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "invoice" }, Ct)).Subject);
        Assert.Equal("Meeting", Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "bob" }, Ct)).Subject);
        Assert.Equal("Invoice March", Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "zebra" }, Ct)).Subject);
        Assert.Empty(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "unicorn" }, Ct));

        Assert.Equal(2, await Repo.UnreadCountAsync(Ctx, Ct));
    }

    [Fact]
    public async Task List_PagesWithBefore()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        for (var i = 1; i <= 5; i++) s.AddMessage("inbox", $"<p{i}@x>", $"T{i}", date: D(i));
        await SyncAsync(acc);

        var first = await Repo.ListThreadsAsync(Ctx, new MailListQuery { Limit = 2 }, Ct);
        Assert.Equal(new[] { "T5", "T4" }, first.Select(t => t.Subject).ToArray());
        var second = await Repo.ListThreadsAsync(Ctx, new MailListQuery { Limit = 2, Before = first[^1].LatestAt }, Ct);
        Assert.Equal(new[] { "T3", "T2" }, second.Select(t => t.Subject).ToArray());
        var third = await Repo.ListThreadsAsync(Ctx, new MailListQuery { Limit = 2, Before = second[^1].LatestAt }, Ct);
        Assert.Equal(new[] { "T1" }, third.Select(t => t.Subject).ToArray());
    }

    // ───────── server changes come back ─────────

    [Fact]
    public async Task MovedToArchiveOnServer_IsTheSameMessage_Archived_NotTrashed()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        var uid = s.AddMessage("inbox", "<mv@x>", "Move me");
        await SyncAsync(acc);
        var before = await ByKeyAsync("mv@x");
        await Repo.SetThreadTagsAsync(Ctx, before.ThreadId, ["keep"], Ct);
        Assert.Equal(MailStates.Inbox, before.State);

        var newUid = s.Move("inbox", uid, "archive");
        var r = await SyncAsync(acc);

        Assert.Equal(0, r.Trashed);
        var after = await ByKeyAsync("mv@x");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(MailStates.Archived, after.State);
        Assert.Equal(new[] { "keep" }, after.Tags);
        var loc = Assert.Single(await Repo.LocationsAsync(Ctx, after.Id, Ct));
        Assert.Equal(("archive", newUid), (loc.Role, loc.Uid));
        Assert.DoesNotContain(await Trash.ListAsync(Ctx, Ct), t => t.Kind == TrashKinds.Mail);
        Assert.Empty(await Repo.ListThreadsAsync(Ctx, new MailListQuery(), Ct));
        Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Archived = true }, Ct));
    }

    [Fact]
    public async Task DeletedOnServer_GoesToTheTrash_AndRestoreBringsItBackSearchable()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        var uid = s.AddMessage("inbox", "<del@x>", "Doomed note", text: "contains the word pineapple");
        s.AddMessage("inbox", "<stay@x>", "Stays");
        await SyncAsync(acc);
        var original = await ByKeyAsync("del@x");

        s.Delete("inbox", uid);
        var r = await SyncAsync(acc);

        Assert.Equal(1, r.Trashed);
        Assert.Null(await Repo.GetMessageAsync(Ctx, original.Id, Ct));
        Assert.Empty(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "pineapple" }, Ct));
        var item = Assert.Single(await Trash.ListAsync(Ctx, Ct), t => t.Kind == TrashKinds.Mail);
        Assert.Equal("Doomed note", item.Title);

        var (result, _) = await Trash.RestoreAsync(Ctx, item.Id, Ct);

        Assert.Equal(TrashRestore.Restored, result);
        var back = await Repo.GetMessageAsync(Ctx, original.Id, Ct);
        Assert.NotNull(back);
        Assert.Equal("Doomed note", back!.Subject);
        // It lies nowhere on the server any more: archived, and found again by full text.
        var hit = Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Archived = true, Text = "pineapple" }, Ct));
        Assert.Equal(back.ThreadId, hit.ThreadId);
        Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Archived = true, Address = "alice@example.com" }, Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeenAndFlaggedChanges_Apply(bool condstore)
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        s.Add("inbox", "INBOX", condstore);
        var u1 = s.AddMessage("inbox", "<f1@x>", "One");
        var u2 = s.AddMessage("inbox", "<f2@x>", "Two", seen: true, flagged: true);
        await SyncAsync(acc);
        Assert.False((await ByKeyAsync("f1@x")).Seen);
        Assert.True((await ByKeyAsync("f2@x")).Flagged);

        s.SetFlags("inbox", u1, seen: true, flagged: true);
        s.SetFlags("inbox", u2, seen: false, flagged: false);
        await SyncAsync(acc);

        var m1 = await ByKeyAsync("f1@x");
        var m2 = await ByKeyAsync("f2@x");
        Assert.True(m1.Seen);
        Assert.True(m1.Flagged);
        Assert.False(m2.Seen);
        Assert.False(m2.Flagged);
    }

    [Fact]
    public async Task UidValidityReset_KeepsMessages_AndRelinksThem()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        for (var i = 1; i <= 3; i++) s.AddMessage("inbox", $"<u{i}@x>", $"U{i}");
        await SyncAsync(acc);
        var ids = (await AllMessagesAsync()).ToDictionary(m => m.MessageKey, m => m.Id);
        Assert.Equal(3, ids.Count);

        s.ResetUidValidity("inbox");
        var r = await SyncAsync(acc);

        Assert.True(r.Ok);
        Assert.Equal(0, r.Trashed);
        Assert.Equal(0, r.Added);
        var after = (await AllMessagesAsync()).ToDictionary(m => m.MessageKey, m => m.Id);
        Assert.Equal(ids, after);
        Assert.Equal(3, Scalar("SELECT COUNT(*) FROM mail_locations WHERE uid >= 1000"));
        Assert.Equal(3, Scalar("SELECT COUNT(*) FROM mail_locations"));
        Assert.DoesNotContain(await Trash.ListAsync(Ctx, Ct), t => t.Kind == TrashKinds.Mail);
        Assert.All(await AllMessagesAsync(), m => Assert.Equal(MailStates.Inbox, m.State));
    }

    [Fact]
    public async Task SetThreadSeen_ReturnsServerPlacesOfTheChangedIncomingMessages()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        var u1 = s.AddMessage("inbox", "<t1@x>", "Chat", date: D(1));
        var u2 = s.AddMessage("inbox", "<t2@x>", "Re: Chat", inReplyTo: "<t1@x>", date: D(2));
        s.AddMessage("sent", "<t3@x>", "Re: Chat", from: "me@example.com", inReplyTo: "<t2@x>", date: D(3), seen: true);
        await SyncAsync(acc);
        var threadId = (await ByKeyAsync("t1@x")).ThreadId;

        var changed = await Repo.SetThreadSeenAsync(Ctx, threadId, true, Ct);

        Assert.Equal(2, changed.Count);
        Assert.All(changed, c => Assert.Equal(("inbox", acc.Id), (c.Role, c.AccountId)));
        Assert.Equal(new[] { u1, u2 }.OrderBy(x => x), changed.Select(c => c.Uid).OrderBy(x => x));
        Assert.All(await Repo.GetThreadAsync(Ctx, threadId, Ct), m => Assert.True(m.Seen));
        Assert.Empty(await Repo.SetThreadSeenAsync(Ctx, threadId, true, Ct));
        Assert.Equal(0, await Repo.UnreadCountAsync(Ctx, Ct));

        var back = await Repo.SetThreadSeenAsync(Ctx, threadId, false, Ct);
        Assert.Equal(2, back.Count);
        Assert.Equal(1, await Repo.UnreadCountAsync(Ctx, Ct));
    }

    // ───────── account delete ─────────

    [Fact]
    public async Task DeleteAccount_RemovesItsMailAndIndexes_LeavesOthers()
    {
        var a = await AddAccountAsync("a@example.com", name: "A");
        var b = await AddAccountAsync("b@example.org", name: "B");
        Connector.For(a.Id).AddMessage("inbox", "<ma@x>", "From A", text: "alpha content");
        Connector.For(a.Id).AddMessage("sent", "<ma2@x>", "Also A", from: "a@example.com");
        Connector.For(b.Id).AddMessage("inbox", "<mb@x>", "From B", text: "beta content");
        await SyncAsync(a);
        await SyncAsync(b);
        Assert.Equal(3, Scalar("SELECT COUNT(*) FROM mail_messages"));

        Assert.True(await Repo.DeleteAccountAsync(Ctx, a.Id, Ct));

        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_messages WHERE account_id = '{a.Id}'"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_locations WHERE account_id = '{a.Id}'"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_folders WHERE account_id = '{a.Id}'"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_accounts WHERE id = '{a.Id}'"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM mail_messages"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM mail_locations"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM mail_fts"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM mail_refs WHERE message_id NOT IN (SELECT id FROM mail_messages)"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM mail_addresses WHERE message_id NOT IN (SELECT id FROM mail_messages)"));

        Assert.Empty(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "alpha" }, Ct));
        Assert.Single(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Text = "beta" }, Ct));
        Assert.Single(await Repo.ListAccountsAsync(Ctx, Ct));
        Assert.False(await Repo.DeleteAccountAsync(Ctx, a.Id, Ct));
    }
}
