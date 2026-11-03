using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.Caching.Memory;

namespace Fishbowl.Data.Mail;

/// <summary>
/// Who a mail can go to — the To, Cc and Bcc suggestions and the address
/// picker: the workspace's contacts and the people in its mail, the inbox
/// included (whoever one writes back and forth with is often in no address
/// book yet).
/// <list type="bullet">
/// <item>Mail weighs each message by who someone was in it — written to from
/// here 1, the sender of mail that came in ½, copied on it ¼ — fading with its
/// age (half the weight after half a year).</item>
/// <item>Mailing-list mail (a List-Id) doesn't count, nor does a no-reply
/// sender nobody here wrote to; the workspace's own addresses never show.</item>
/// <item>A contact counts one fresh mail more and gives the name; otherwise
/// the latest name the mail used.</item>
/// <item>Every word typed starts a word of the name, the organisation or the
/// address (or the address itself), in any case and with or without accents.</item>
/// </list>
/// The mail side is read once a minute per workspace (a fraction of a second
/// for tens of thousands of messages); a keystroke only filters it.
/// </summary>
public sealed partial class MailRecipients
{
    public const int MaxLimit = 100;
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(1);

    private readonly DatabaseFactory _db;
    private readonly IContactRepository _contacts;
    private readonly IMailRepository _mail;
    private readonly IMemoryCache _cache;

    public MailRecipients(DatabaseFactory db, IContactRepository contacts, IMailRepository mail, IMemoryCache cache)
    {
        _db = db;
        _contacts = contacts;
        _mail = mail;
        _cache = cache;
    }

    // One address as the mail has it: its weight, the latest name it went
    // by, whether anyone here wrote to it (Best = 1).
    private sealed record Seen(string Address, string? Name, double Score, double Best, int Mails, DateTime LastAt);

    private sealed class Candidate
    {
        public required string Address;
        public string? Name;
        public double Score;
        public int Mails;
        public DateTime? LastAt;
        public string? ContactId;
        public string? ContactKind;
        public string? Label;
        public string? Organisation;
    }

    public async Task<IReadOnlyList<MailRecipient>> SuggestAsync(ContextRef ctx, string? query, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var own = (await _mail.ListAccountsAsync(ctx, ct))
            .SelectMany(a => a.Aliases.Append(a.Address)).Select(Key).ToHashSet();
        var all = new Dictionary<string, Candidate>();
        foreach (var s in await SeenAsync(ctx, ct))
        {
            if (own.Contains(s.Address) || (s.Best < 1 && Automated(s.Address))) continue;
            all[s.Address] = new Candidate { Address = s.Address, Name = s.Name, Score = s.Score, Mails = s.Mails, LastAt = s.LastAt };
        }

        var contacts = (await _contacts.GetAllAsync(ctx, includeArchived: false, ct)).ToList();
        var organisations = contacts.Where(c => c.Kind == ContactKinds.Organisation).ToDictionary(c => c.Id, c => c.Name);
        foreach (var c in contacts)
            foreach (var e in c.Emails)
            {
                var address = Key(e.Value);
                if (!address.Contains('@') || own.Contains(address)) continue;
                if (!all.TryGetValue(address, out var x)) all[address] = x = new Candidate { Address = address };
                if (x.ContactId is not null) continue;   // two contacts with one address: the first
                x.ContactId = c.Id;
                x.ContactKind = c.Kind;
                x.Label = string.IsNullOrWhiteSpace(e.Label) ? null : e.Label.Trim();
                x.Score += 1;
                if (!string.IsNullOrWhiteSpace(c.Name)) x.Name = c.Name.Trim();
                x.Organisation = c.OrganisationId is { } org && organisations.TryGetValue(org, out var name) ? name : null;
            }

        var words = Words(query);
        return all.Values
            .Where(x => Matches(x, words))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name ?? x.Address, StringComparer.InvariantCultureIgnoreCase)
            .Take(limit)
            .Select(x => new MailRecipient(x.Address, x.Name, MailWriter.Person(x.Name, x.Address),
                x.ContactId, x.ContactKind, x.Label, x.Organisation, x.Mails, x.LastAt))
            .ToList();
    }

    private async Task<IReadOnlyList<Seen>> SeenAsync(ContextRef ctx, CancellationToken ct) =>
        (await _cache.GetOrCreateAsync($"mail-recipients:{ctx.Type}:{ctx.Id}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = Keep;
            return await ReadSeenAsync(ctx, ct);
        }))!;

    // Every address of every message that counts, with the weight of who it
    // was there; the latest name is the maximum of "<sent_at>\x1f<name>".
    private async Task<IReadOnlyList<Seen>> ReadSeenAsync(ContextRef ctx, CancellationToken ct)
    {
        using var db = _db.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<(string Address, string? Named, double Score, double Best, long Mails, string LastAt)>(new CommandDefinition(@"
            WITH hits AS (
                SELECT json_extract(p.value, '$.address') AS address, json_extract(p.value, '$.name') AS name, 1.0 AS w, m.sent_at
                    FROM mail_messages m, json_each(m.to_list) p WHERE m.direction = 'out'
                UNION ALL SELECT json_extract(p.value, '$.address'), json_extract(p.value, '$.name'), 1.0, m.sent_at
                    FROM mail_messages m, json_each(m.cc_list) p WHERE m.direction = 'out'
                UNION ALL SELECT json_extract(p.value, '$.address'), json_extract(p.value, '$.name'), 1.0, m.sent_at
                    FROM mail_messages m, json_each(m.bcc_list) p WHERE m.direction = 'out'
                UNION ALL SELECT m.from_address, m.from_name, 0.5, m.sent_at
                    FROM mail_messages m WHERE m.direction = 'in' AND m.list_id IS NULL
                UNION ALL SELECT json_extract(p.value, '$.address'), json_extract(p.value, '$.name'), 0.25, m.sent_at
                    FROM mail_messages m, json_each(m.to_list) p WHERE m.direction = 'in' AND m.list_id IS NULL
                UNION ALL SELECT json_extract(p.value, '$.address'), json_extract(p.value, '$.name'), 0.25, m.sent_at
                    FROM mail_messages m, json_each(m.cc_list) p WHERE m.direction = 'in' AND m.list_id IS NULL
            )
            SELECT lower(trim(address)) AS address,
                   MAX(CASE WHEN trim(coalesce(name, '')) <> '' THEN sent_at || char(31) || trim(name) END) AS named,
                   SUM(w / (1 + (julianday('now') - julianday(sent_at)) / 180.0)) AS score,
                   MAX(w) AS best,
                   COUNT(*) AS mails,
                   MAX(sent_at) AS last_at
            FROM hits
            WHERE address IS NOT NULL AND instr(address, '@') > 1
            GROUP BY lower(trim(address))", cancellationToken: ct));
        return rows.Select(r =>
        {
            var name = r.Named is { } n && n.IndexOf('\u001f') is var i and >= 0 ? n[(i + 1)..].Trim('"', '\'', ' ') : null;
            if (string.IsNullOrEmpty(name) || Key(name) == r.Address) name = null;
            return new Seen(r.Address, name, r.Score, r.Best, (int)r.Mails,
                DateTime.Parse(r.LastAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }).ToList();
    }

    private static string Key(string address) => address.Trim().ToLowerInvariant();

    // Senders no one writes to: they only count once someone here did.
    [GeneratedRegex(@"no-?reply|no_reply|do-?not-?reply|do_not_reply|mailer-daemon|postmaster|bounce|newsletter|notification", RegexOptions.IgnoreCase)]
    private static partial Regex AutomatedLocal();

    private static bool Automated(string address) => AutomatedLocal().IsMatch(address[..address.IndexOf('@')]);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRun();

    private static List<string> Words(string? query) =>
        string.IsNullOrWhiteSpace(query) ? []
            : query.Split([' ', '\t', ',', ';', '<', '>', '"', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
                .Select(Fold).Where(w => w.Length > 0).Take(8).ToList();

    private static bool Matches(Candidate x, List<string> words)
    {
        if (words.Count == 0) return true;
        var keys = new List<string>();
        foreach (var text in new[] { x.Name, x.Organisation })
            if (!string.IsNullOrEmpty(text)) keys.AddRange(WordRun().Matches(Fold(text)).Select(m => m.Value));
        var address = Fold(x.Address);
        var at = address.IndexOf('@');
        keys.Add(address);
        keys.Add(address[..at]);
        keys.AddRange(address.Split(['@', '.', '_', '-', '+'], StringSplitOptions.RemoveEmptyEntries));
        keys.Add(address[(at + 1)..]);
        return words.All(w => keys.Any(k => k.StartsWith(w, StringComparison.Ordinal)));
    }

    /// <summary>Lower case, accents off ("Müller" → "muller", "ß" → "ss"), so
    /// a word typed either way finds it.</summary>
    internal static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var b = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) b.Append(char.ToLowerInvariant(ch));
        return b.Replace("ß", "ss").ToString();
    }
}
