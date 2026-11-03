using Fishbowl.Core.Models;
using Fishbowl.Data.Mail;
using Fishbowl.Data.Repositories;
using Microsoft.Extensions.Caching.Memory;

namespace Fishbowl.Tests.Mail;

// The To field's suggestions (MailRecipients): contacts and the people in the
// workspace's mail — the inbox too, past mailing lists and no-reply senders —
// ranked by how much mail there is with them and how recent.
public class MailRecipientsTests : MailFixture
{
    private readonly ContactRepository _contacts;
    private readonly MailRecipients _recipients;
    private uint _uid;

    public MailRecipientsTests()
    {
        _contacts = new ContactRepository(Factory);
        _recipients = new MailRecipients(Factory, _contacts, Repo, new MemoryCache(new MemoryCacheOptions()));
    }

    private Task Mail(MailAccount acc, string direction, MailPerson from, MailPerson[] to, int daysAgo = 1, string? listId = null, MailPerson[]? cc = null) =>
        Repo.InsertAsync(Ctx, new MailMessage
        {
            AccountId = acc.Id,
            MessageKey = $"m{++_uid}@test",
            Direction = direction,
            FromName = from.Name,
            FromAddress = from.Address,
            ToList = to.ToList(),
            CcList = (cc ?? []).ToList(),
            Subject = "Hello",
            SentAt = DateTime.UtcNow.AddDays(-daysAgo),
            ListId = listId,
        }, direction == MailDirections.Out ? MailRoles.Sent : MailRoles.Inbox, _uid, Ct);

    private async Task<List<string>> Addresses(string? q, int limit = 10) =>
        (await _recipients.SuggestAsync(Ctx, q, limit, Ct)).Select(r => r.Address).ToList();

    [Fact]
    public async Task Suggests_ContactsAndPeopleFromTheMail_ButNeverListsNoReplyOrOwnAddresses()
    {
        var acc = await AddAccountAsync("me@example.com", aliases: ["me@alias.example"]);
        var me = new MailPerson("Me", "me@example.com");
        await Mail(acc, MailDirections.Out, me, [new("Hans Müller", "h.mueller@firma.de")]);
        await Mail(acc, MailDirections.In, new("Grete Weiß", "grete@post.example"), [me, new(null, "me@alias.example")],
            cc: [new("Otto Copied", "otto@copied.example")]);
        await Mail(acc, MailDirections.In, new("Club News", "news@club.example"), [me], listId: "club.example");
        await Mail(acc, MailDirections.In, new("Shop", "noreply@shop.example"), [me]);
        await _contacts.CreateAsync(Ctx, "mail_test_user", new Contact
        {
            Kind = ContactKinds.Person,
            FirstName = "Ada",
            LastName = "Lovelace",
            Emails = [new ContactValue { Label = "work", Value = "Ada@Engine.example" }],
        }, Ct);

        var all = await Addresses(null);
        Assert.Equal(["ada@engine.example", "h.mueller@firma.de", "grete@post.example", "otto@copied.example"], all);

        // Any word of the name or the address, any case, with or without accents.
        Assert.Equal(["h.mueller@firma.de"], await Addresses("mül"));
        Assert.Equal(["h.mueller@firma.de"], await Addresses("MUELLER"));
        Assert.Equal(["h.mueller@firma.de"], await Addresses("firma"));
        Assert.Equal(["grete@post.example"], await Addresses("weiss"));
        Assert.Equal(["ada@engine.example"], await Addresses("ada love"));
        Assert.Empty(await Addresses("club"));
        Assert.Empty(await Addresses("shop"));
        Assert.Empty(await Addresses("me@"));

        var ada = (await _recipients.SuggestAsync(Ctx, "lovelace", 5, Ct)).Single();
        Assert.Equal(("Ada Lovelace", "Ada Lovelace <ada@engine.example>", "work"), (ada.Name, ada.Text, ada.Label));
        Assert.NotNull(ada.ContactId);
        var grete = (await _recipients.SuggestAsync(Ctx, "grete", 5, Ct)).Single();
        Assert.Equal("Grete Weiß <grete@post.example>", grete.Text);
        Assert.Null(grete.ContactId);
    }

    [Fact]
    public async Task WhomTheWorkspaceWritesTo_RanksBeforeWhoWritesToIt_AndRecentBeforeOld()
    {
        var acc = await AddAccountAsync();
        var me = new MailPerson("Me", "me@example.com");
        await Mail(acc, MailDirections.In, new("Anna Inbox", "anna.in@example.org"), [me]);
        await Mail(acc, MailDirections.Out, me, [new("Anna Written", "anna.out@example.org")]);
        await Mail(acc, MailDirections.Out, me, [new("Anna Long Ago", "anna.old@example.org")], daysAgo: 1500);
        await Mail(acc, MailDirections.Out, me, [new("Anna Long Ago", "anna.old@example.org")], daysAgo: 1400);

        Assert.Equal(["anna.out@example.org", "anna.in@example.org", "anna.old@example.org"], await Addresses("anna"));
        Assert.Equal(["anna.out@example.org"], await Addresses("anna", limit: 1));
    }

    [Fact]
    public async Task ANameWithACommaGoesInQuoted_SoTheFieldSplitsItRight()
    {
        var acc = await AddAccountAsync();
        await Mail(acc, MailDirections.Out, new("Me", "me@example.com"), [new("Doe, Jane", "jane@example.org")]);

        var jane = (await _recipients.SuggestAsync(Ctx, "jane", 5, Ct)).Single();

        Assert.Equal("\"Doe, Jane\" <jane@example.org>", jane.Text);
    }
}
