using System;
using System.Collections.Generic;
using System.Linq;
using Fishbowl.Core.Models;

namespace Fishbowl.Core.Util;

/// <summary>
/// The pure parts of mail rules (mail spec, decision 11): whether a rule meets
/// a message, and what the rules that do add up to. A condition is "contains",
/// in any case — a name or an address for the people fields, so "Weygandt"
/// meets "Anna Weygandt &lt;a.weygandt@example.com&gt;".
/// </summary>
public static class MailRules
{
    public const int MaxRules = 100;
    public const int MaxConditions = 10;
    public const int MaxNameLength = 80;
    public const int MaxValueLength = 200;
    public const int MaxTags = 20;

    /// <summary>What every matching rule asks for together.</summary>
    public sealed record Outcome(IReadOnlyList<string> Tags, bool Archive, bool MarkRead)
    {
        public static readonly Outcome None = new([], false, false);
        public bool Any => Tags.Count > 0 || Archive || MarkRead;
    }

    /// <summary>Whether the rule's conditions meet the message — on or off;
    /// <see cref="For"/> takes only the rules that are on.</summary>
    public static bool Matches(MailRule rule, MailMessage m)
    {
        if (rule.Conditions.Count == 0) return false;
        return rule.MatchAll ? rule.Conditions.All(c => Meets(c, m)) : rule.Conditions.Any(c => Meets(c, m));
    }

    public static Outcome For(IEnumerable<MailRule> rules, MailMessage m)
    {
        var hits = rules.Where(r => r.Enabled && Matches(r, m)).ToList();
        if (hits.Count == 0) return Outcome.None;
        return new Outcome(
            hits.SelectMany(r => r.AddTags).Distinct(StringComparer.Ordinal).ToList(),
            hits.Any(r => r.Archive),
            hits.Any(r => r.MarkRead));
    }

    public static bool Meets(MailRuleCondition c, MailMessage m)
    {
        var v = c.Value?.Trim();
        if (string.IsNullOrEmpty(v)) return false;
        return c.Field switch
        {
            MailRuleFields.From => Person(m.FromName, m.FromAddress, v),
            MailRuleFields.To => Recipients(m, v),
            MailRuleFields.Anyone => Person(m.FromName, m.FromAddress, v) || Recipients(m, v),
            MailRuleFields.Subject => Contains(m.Subject, v),
            MailRuleFields.Account => string.Equals(m.AccountId, v, StringComparison.Ordinal),
            MailRuleFields.List => Contains(m.ListId, v),
            _ => false,
        };
    }

    /// <summary>Null when the rule can be stored, else what is missing:
    /// a name, a condition (a known field with a value), an action.</summary>
    public static string? Problem(MailRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Trim().Length > MaxNameLength) return "name";
        if (rule.Conditions.Count is 0 or > MaxConditions) return "conditions";
        if (rule.Conditions.Any(c => !MailRuleFields.All.Contains(c.Field)
            || string.IsNullOrWhiteSpace(c.Value) || c.Value.Trim().Length > MaxValueLength))
            return "conditions";
        if (rule.AddTags.Count > MaxTags) return "tags";
        if (rule.AddTags.Count == 0 && !rule.Archive && !rule.MarkRead) return "actions";
        return null;
    }

    private static bool Recipients(MailMessage m, string v) =>
        m.ToList.Concat(m.CcList).Concat(m.BccList).Any(p => Person(p.Name, p.Address, v));

    private static bool Person(string? name, string? address, string v) => Contains(name, v) || Contains(address, v);

    private static bool Contains(string? s, string v) => s is not null && s.Contains(v, StringComparison.OrdinalIgnoreCase);
}
