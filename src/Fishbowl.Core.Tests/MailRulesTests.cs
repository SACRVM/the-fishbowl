using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

public class MailRulesTests
{
    private static MailMessage Message(string? fromName = "Anna Weygandt", string? from = "a.weygandt@helvetia.example",
        string to = "me@example.com", string? cc = null, string? subject = "Police 4711", string? list = null, string account = "acc1") => new()
        {
            AccountId = account,
            FromName = fromName,
            FromAddress = from,
            ToList = [new MailPerson(null, to)],
            CcList = cc is null ? [] : [new MailPerson("Copy", cc)],
            Subject = subject,
            ListId = list,
        };

    private static MailRule Rule(bool all, params (string Field, string Value)[] conditions) => new()
    {
        Name = "r",
        MatchAll = all,
        Conditions = conditions.Select(c => new MailRuleCondition(c.Field, c.Value)).ToList(),
        AddTags = ["helvetia"],
    };

    [Theory]
    [InlineData("weygandt", true)]      // the name, any case
    [InlineData("HELVETIA.example", true)]  // the address
    [InlineData("müller", false)]
    public void From_MeetsTheNameOrTheAddress(string value, bool meets)
        => Assert.Equal(meets, MailRules.Matches(Rule(true, (MailRuleFields.From, value)), Message()));

    [Fact]
    public void Anyone_MeetsTheSenderAndEveryRecipient()
    {
        var rule = Rule(true, (MailRuleFields.Anyone, "Weygandt"));
        Assert.True(MailRules.Matches(rule, Message()));
        // My own mail to Weygandt: the recipient carries the name.
        Assert.True(MailRules.Matches(rule, Message(fromName: "Me", from: "me@example.com", to: "anna.weygandt@helvetia.example")));
        Assert.True(MailRules.Matches(rule, Message(fromName: "X", from: "x@example.com", cc: "weygandt@example.com")));
        Assert.False(MailRules.Matches(rule, Message(fromName: "X", from: "x@example.com")));
    }

    [Fact]
    public void To_LooksAtRecipientsOnly()
    {
        var rule = Rule(true, (MailRuleFields.To, "weygandt"));
        Assert.False(MailRules.Matches(rule, Message()));
        Assert.True(MailRules.Matches(rule, Message(to: "weygandt@example.com")));
    }

    [Fact]
    public void SubjectListAndAccount()
    {
        Assert.True(MailRules.Matches(Rule(true, (MailRuleFields.Subject, "police")), Message()));
        Assert.True(MailRules.Matches(Rule(true, (MailRuleFields.List, "news.example")), Message(list: "<weekly.news.example>")));
        Assert.False(MailRules.Matches(Rule(true, (MailRuleFields.List, "news")), Message()));
        Assert.True(MailRules.Matches(Rule(true, (MailRuleFields.Account, "acc1")), Message()));
        Assert.False(MailRules.Matches(Rule(true, (MailRuleFields.Account, "acc")), Message()));   // an id is equal, not contained
    }

    [Fact]
    public void AllOrAny()
    {
        var both = new[] { (MailRuleFields.From, "weygandt"), (MailRuleFields.Subject, "invoice") };
        Assert.False(MailRules.Matches(Rule(true, both), Message()));
        Assert.True(MailRules.Matches(Rule(false, both), Message()));
    }

    [Fact]
    public void For_AddsUpTheRulesThatAreOn()
    {
        var a = Rule(true, (MailRuleFields.From, "weygandt"));
        a.AddTags = ["helvetia", "versicherungen"];
        var b = Rule(true, (MailRuleFields.Subject, "police"));
        b.AddTags = ["versicherungen"];
        b.MarkRead = true;
        var off = Rule(true, (MailRuleFields.From, "weygandt"));
        off.Enabled = false;
        off.Archive = true;

        var outcome = MailRules.For([a, b, off], Message());
        Assert.Equal(["helvetia", "versicherungen"], outcome.Tags);
        Assert.True(outcome.MarkRead);
        Assert.False(outcome.Archive);
        Assert.Same(MailRules.Outcome.None, MailRules.For([a], Message(fromName: "X", from: "x@example.com")));
    }

    [Fact]
    public void Problem_NamesWhatIsMissing()
    {
        Assert.Null(MailRules.Problem(Rule(true, (MailRuleFields.From, "x"))));
        var noName = Rule(true, (MailRuleFields.From, "x"));
        noName.Name = " ";
        Assert.Equal("name", MailRules.Problem(noName));
        Assert.Equal("conditions", MailRules.Problem(Rule(true)));
        Assert.Equal("conditions", MailRules.Problem(Rule(true, ("body", "x"))));
        Assert.Equal("conditions", MailRules.Problem(Rule(true, (MailRuleFields.From, "  "))));
        var nothing = Rule(true, (MailRuleFields.From, "x"));
        nothing.AddTags = [];
        Assert.Equal("actions", MailRules.Problem(nothing));
    }
}
