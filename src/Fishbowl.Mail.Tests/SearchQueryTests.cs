using Fishbowl.Mail;
using Fishbowl.Mail.Imap;
using Fishbowl.Mail.Models;
using MailKit.Search;
using SearchOptions = Fishbowl.Mail.Models.SearchOptions;

namespace Fishbowl.Mail.Tests;

public class SearchQueryTests
{
    [Fact]
    public void Empty_options_yield_All()
    {
        var q = MailboxSession.BuildQuery(new SearchOptions(), gmail: false);
        Assert.Equal(SearchTerm.All, q.Term);
    }

    [Fact]
    public void Filters_are_anded()
    {
        var q = MailboxSession.BuildQuery(new SearchOptions { From = "amazon", Unread = true }, gmail: false);
        Assert.Equal(SearchTerm.And, q.Term);
    }

    [Fact]
    public void GmailQuery_requires_gmail()
    {
        Assert.Throws<MailException>(() => MailboxSession.BuildQuery(new SearchOptions { GmailQuery = "newer_than:7d" }, gmail: false));
        var q = MailboxSession.BuildQuery(new SearchOptions { GmailQuery = "newer_than:7d" }, gmail: true);
        Assert.Equal(SearchTerm.And, q.Term);
    }
}
