using System.Net.Sockets;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Fishbowl.Mail;
using Fishbowl.Mail.Models;
using Fishbowl.Mail.Sync;
using Fishbowl.Mail.Text;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Mail;

/// <summary>
/// One pass over one account (mail spec, decision 14). Per synced folder —
/// INBOX, Sent, Archive (Gmail: All Mail):
/// <list type="bullet">
/// <item>a new UIDVALIDITY (or another folder in the role) drops the folder's
///   places; the messages stay and are found again by their key;</item>
/// <item>places the server no longer has go — a message left with none goes
///   to the trash at the end of the pass, after every folder was read, so a
///   move from the INBOX to the Archive is a change of place, not a delete;</item>
/// <item>new mail comes by UID, oldest first; the history newest first; at
///   most <see cref="BatchPerFolder"/> messages per folder and pass, so a big
///   mailbox fills over several passes;</item>
/// <item>flags: what changed since the last mod-sequence (CONDSTORE), else all.</item>
/// </list>
/// A message already here (same account and key) found in a folder gets one
/// more place; a new one is stored with its text and HTML parts — the
/// attachments stay on the server — and meets the workspace's rules
/// (decision 11): their tags come with it, read is written back at once, and
/// what lies in the inbox is archived — its conversation — once every folder
/// was read.
/// </summary>
public sealed class MailSyncer
{
    public const int BatchPerFolder = 200;
    private const int HeaderChunk = 50;
    public const int MaxTextChars = 1_000_000;
    public const int MaxHtmlChars = 2_000_000;

    private readonly MailRepository _repo;
    private readonly IMailboxConnector _connector;
    private readonly MailRuleRepository? _rules;
    private readonly ILogger<MailSyncer> _logger;

    public MailSyncer(MailRepository repo, IMailboxConnector connector, ILogger<MailSyncer>? logger = null, MailRuleRepository? rules = null)
    {
        _repo = repo;
        _connector = connector;
        _rules = rules;
        _logger = logger ?? NullLogger<MailSyncer>.Instance;
    }

    // The rules of one pass, and the messages they archive at its end.
    private sealed class PassRules(IReadOnlyList<MailRule> rules)
    {
        public IReadOnlyList<MailRule> Rules { get; } = rules;
        public HashSet<string> Archive { get; } = new();
    }

    public sealed record Result(bool Ok, string? Error, bool BackfillDone, int Added, int Trashed);

    /// <summary>Connection settings for the library from a stored account.</summary>
    public static ResolvedAccount Resolve(MailAccount a) => ResolvedAccount.From(new AccountConfig
    {
        Id = a.Id,
        Provider = ProviderPreset.All.ContainsKey(a.Provider) ? a.Provider : "custom",
        User = a.Username,
        Address = a.Address,
        DisplayName = a.DisplayName,
        Imap = new EndpointConfig { Host = a.ImapHost, Port = a.ImapPort, Security = Tls(a.ImapSecurity) },
        Smtp = new EndpointConfig { Host = a.SmtpHost, Port = a.SmtpPort, Security = Tls(a.SmtpSecurity) },
    });

    public static TlsMode Tls(string? s) => s?.ToLowerInvariant() switch
    {
        "starttls" => TlsMode.StartTls,
        "none" => TlsMode.None,
        _ => TlsMode.Ssl,
    };

    /// <summary>What went wrong, as a code the UI translates — never the server's text.</summary>
    public static string ErrorCode(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is MailKit.Security.AuthenticationException) return "auth_failed";
            if (e is SocketException or TimeoutException or ServiceNotConnectedException) return "unreachable";
            if (e is MailKit.Security.SslHandshakeException) return "tls_failed";
            if (e is IOException or ImapProtocolException) return "unreachable";
        }
        return "sync_failed";
    }

    // One pass per account at a time, whoever starts it: two at once would
    // both store the same new message.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Running = new();

    public async Task<Result> SyncAsync(ContextRef ctx, MailAccount account, byte[] secret, CancellationToken ct)
    {
        var gate = Running.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await PassAsync(ctx, account, secret, ct); }
        finally { gate.Release(); }
    }

    private async Task<Result> PassAsync(ContextRef ctx, MailAccount account, byte[] secret, CancellationToken ct)
    {
        var password = await _repo.PasswordAsync(account.Id, secret, ct);
        if (password is null)
        {
            // Encrypted with another instance's key (a moved folder): enter it again.
            await _repo.SetAccountStateAsync(ctx, account.Id, MailAccountStates.Failed, "credentials_unreadable", null, ct);
            return new Result(false, "credentials_unreadable", false, 0, 0);
        }
        try
        {
            await using var box = _connector.Open(Resolve(account), password);
            var own = new HashSet<string>(account.Aliases.Append(account.Address).Select(a => a.Trim().ToLowerInvariant()));
            var candidates = new HashSet<string>();
            var rules = new PassRules(await RulesAsync(ctx, ct));
            var added = 0;
            var done = true;
            foreach (var role in MailRoles.All)
            {
                var name = await box.RoleFolderAsync(role, ct);
                if (name is null) continue;
                var (a, d) = await SyncFolderAsync(ctx, account, own, box, role, name, candidates, rules, ct);
                added += a;
                done &= d;
            }
            var trashed = await _repo.TrashOrphansAsync(ctx, candidates, ct);
            if (rules.Archive.Count > 0) await ArchiveAsync(ctx, account, rules.Archive, ct);
            await _repo.SetAccountStateAsync(ctx, account.Id, MailAccountStates.Ok, null, done, ct);
            return new Result(true, null, done, added, trashed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var code = ErrorCode(ex);
            // The server's own words can name the account; they stay in the debug log.
            _logger.LogWarning("Mail sync failed for account {AccountId}: {Code}", account.Id, code);
            _logger.LogDebug(ex, "Mail sync failure detail for account {AccountId}", account.Id);
            await _repo.SetAccountStateAsync(ctx, account.Id, MailAccountStates.Failed, code, null, ct);
            return new Result(false, code, false, 0, 0);
        }
    }

    private async Task<IReadOnlyList<MailRule>> RulesAsync(ContextRef ctx, CancellationToken ct)
    {
        if (_rules is null) return [];
        var rules = (await _rules.ListAsync(ctx, ct)).Where(r => r.Enabled).ToList();
        // A tag deleted since a rule named it comes back with the rule's mail.
        if (rules.Count > 0) await _rules.EnsureTagsAsync(ctx, rules.SelectMany(r => r.AddTags), ct);
        return rules;
    }

    /// <summary>Rules' archive: the conversations of the messages, on the
    /// server first (<see cref="MailServer.ArchiveAsync"/>). Best effort — the
    /// pass stands; an account without an archive folder keeps them.</summary>
    private async Task ArchiveAsync(ContextRef ctx, MailAccount account, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        try
        {
            var all = await _repo.WholeThreadsOfAsync(ctx, ids, ct);
            if (!await new MailServer(_repo, _connector).ArchiveAsync(ctx, all, true, ct))
                _logger.LogWarning("Mail rules: account {AccountId} has no archive folder", account.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Mail rules: archive failed for account {AccountId}: {Code}", account.Id, ErrorCode(ex));
        }
    }

    private async Task<(int Added, bool BackfillDone)> SyncFolderAsync(
        ContextRef ctx, MailAccount account, HashSet<string> own, IMailbox box, string role, string name,
        HashSet<string> candidates, PassRules rules, CancellationToken ct)
    {
        var status = await box.StatusAsync(name, ct);
        var state = await _repo.GetFolderAsync(ctx, account.Id, role, ct);
        if (state is null || state.UidValidity != status.UidValidity || state.Name != name)
        {
            if (state is not null)
            {
                await _repo.DropLocationsAsync(ctx, account.Id, role, ct);
                await _repo.DropTombstonePlacesAsync(ctx, account.Id, role, ct);
            }
            // From now on is "new"; everything below is history, read newest first.
            state = new MailRepository.FolderState(account.Id, role, name, status.UidValidity, status.UidNext, null, status.UidNext);
        }

        var server = (await box.UidsAsync(name, ct)).OrderBy(u => u).ToList();
        var serverSet = server.ToHashSet();
        var known = await _repo.FolderLocationsAsync(ctx, account.Id, role, ct);

        var gone = known.Keys.Where(u => !serverSet.Contains(u)).ToList();
        if (gone.Count > 0) candidates.UnionWith(await _repo.RemoveLocationsAsync(ctx, account.Id, role, gone, ct));

        // Mail deleted only in Fishbowl stays away (decision 8).
        var dead = await _repo.TombstonesAsync(ctx, account.Id, ct);
        var deadHere = dead.Uids.GetValueOrDefault(role) ?? [];
        bool Wanted(uint u) => !known.ContainsKey(u) && !deadHere.Contains(u);

        var budget = BatchPerFolder;
        var fresh = server.Where(u => u >= state.UidNext && Wanted(u)).ToList();
        var takeNew = fresh.Take(budget).ToList();
        budget -= takeNew.Count;
        // Gaps in what was read: a message restored from the trash after a
        // delete only in Fishbowl — its place on the server comes back.
        var gaps = server.Where(u => u >= state.BackfillBelow && u < state.UidNext && Wanted(u)).Take(budget).ToList();
        budget -= gaps.Count;
        var older = server.Where(u => u < state.BackfillBelow && Wanted(u)).OrderByDescending(u => u).ToList();
        var takeOld = older.Take(budget).ToList();

        var added = 0;
        foreach (var chunk in takeNew.Concat(gaps).Concat(takeOld).Chunk(HeaderChunk))
            added += await StoreAsync(ctx, account, own, box, role, name, chunk, dead, rules, ct);

        // Flags of what was here before this pass (new ones came with theirs).
        if (known.Count > 0)
        {
            var since = status.HighestModSeq is not null && state.HighestModseq is { } m ? (ulong)m : (ulong?)null;
            var flags = await box.FlagsAsync(name, since, ct);
            var mine = flags.Where(f => known.ContainsKey(f.Uid) && serverSet.Contains(f.Uid))
                .Select(f => (f.Uid, f.Seen, f.Flagged)).ToList();
            if (mine.Count > 0) await _repo.ApplyFlagsAsync(ctx, account.Id, role, mine, ct);
        }

        var uidNext = takeNew.Count < fresh.Count ? (long)takeNew[^1] + 1 : Math.Max(state.UidNext, status.UidNext);
        var backfillBelow = takeOld.Count < older.Count ? (long)takeOld[^1] : 1;
        await _repo.SaveFolderAsync(ctx, state with
        {
            UidNext = uidNext,
            HighestModseq = status.HighestModSeq is { } hm ? (long)hm : null,
            BackfillBelow = backfillBelow,
        }, ct);
        return (added, backfillBelow <= 1);
    }

    private async Task<int> StoreAsync(
        ContextRef ctx, MailAccount account, HashSet<string> own, IMailbox box, string role, string name,
        IReadOnlyList<uint> uids, MailRepository.Tombstones dead, PassRules rules, CancellationToken ct)
    {
        var added = 0;
        var read = new List<uint>();
        foreach (var h in await box.HeadersAsync(name, uids, ct))
        {
            var key = MailThreading.MessageKey(h.MessageId, h.From?.Address, h.Date ?? h.InternalDate, h.Subject, h.Size);
            if (dead.Keys.Contains(key))
            {
                await _repo.TombstonePlaceAsync(ctx, account.Id, key, role, h.Uid, ct);
                continue;
            }
            var existing = await _repo.FindByKeyAsync(ctx, account.Id, key, ct);
            if (existing is not null)
            {
                await _repo.AddLocationAsync(ctx, existing, account.Id, role, h.Uid, h.Seen, h.Flagged, ct);
                continue;
            }

            SyncBody? body = null;
            try { body = await box.BodyAsync(name, h.Uid, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogDebug(ex, "Mail body unreadable (account {AccountId}, uid {Uid})", account.Id, h.Uid); }

            var text = body?.Text is { Length: > 0 } t ? HtmlToText.Normalize(t)
                : body?.Html is { Length: > 0 } html ? HtmlToText.Convert(html) : null;
            if (text is { Length: > MaxTextChars }) text = text[..MaxTextChars];
            var from = h.From?.Address?.Trim().ToLowerInvariant();
            var m = new MailMessage
            {
                AccountId = account.Id,
                MessageKey = key,
                InReplyTo = MailThreading.CleanId(h.InReplyTo),
                Refs = h.References.Select(MailThreading.CleanId).Where(r => r is not null).Select(r => r!).Distinct().ToList(),
                Direction = role == MailRoles.Sent || (from is not null && own.Contains(from)) ? MailDirections.Out : MailDirections.In,
                FromName = h.From?.Name,
                FromAddress = h.From?.Address,
                ToList = People(h.To),
                CcList = People(h.Cc),
                BccList = People(h.Bcc),
                ReplyTo = People(h.ReplyTo),
                Subject = h.Subject,
                SentAt = (h.Date ?? h.InternalDate ?? DateTimeOffset.UtcNow).UtcDateTime,
                Snippet = MailThreading.Snippet(text),
                BodyText = text,
                BodyHtml = body?.Html is { Length: > 0 and <= MaxHtmlChars } bh ? bh : null,
                Attachments = body?.Attachments.Select(x => new MailAttachment(x.Index, x.Part, x.FileName, x.ContentType, x.Size)).ToList() ?? new(),
                Size = h.Size,
                Seen = h.Seen,
                Flagged = h.Flagged,
                ListId = h.ListId,
            };
            var outcome = MailRules.For(rules.Rules, m);
            m.Tags = outcome.Tags.ToList();
            if (outcome.MarkRead && m.Direction == MailDirections.In && !m.Seen)
            {
                m.Seen = true;
                read.Add(h.Uid);
            }
            var id = await _repo.InsertAsync(ctx, m, role, h.Uid, ct);
            if (outcome.Archive && role == MailRoles.Inbox) rules.Archive.Add(id);
            added++;
        }
        if (read.Count > 0)
        {
            // Best effort, like a person's read: the flags come back with the next pass.
            try { await box.MarkAsync(name, read, true, null, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning("Mail rules: read not written back for account {AccountId}: {Code}", account.Id, ErrorCode(ex)); }
        }
        return added;
    }

    private static List<MailPerson> People(IReadOnlyList<MailAddress> list) =>
        list.Where(a => !string.IsNullOrWhiteSpace(a.Address)).Select(a => new MailPerson(a.Name, a.Address)).ToList();
}
