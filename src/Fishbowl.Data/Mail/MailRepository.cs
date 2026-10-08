using System.Data;
using System.Text;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Fishbowl.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Mail;

/// <summary>
/// A workspace's mail (user/space schema v22, mail spec). The public half is
/// <see cref="IMailRepository"/>; the rest is what <see cref="MailSyncer"/>
/// writes with: folder state, places on the server (mail_locations), new
/// messages with their thread, flags, and messages the server no longer has
/// (to the trash, decision 9).
/// </summary>
public class MailRepository : IMailRepository
{
    private readonly DatabaseFactory _db;
    private readonly MailCredentials _credentials;
    private readonly ILogger<MailRepository> _logger;
    private readonly ITagRepository _tags;

    public MailRepository(DatabaseFactory db, MailCredentials credentials, ILogger<MailRepository>? logger = null, ITagRepository? tags = null)
    {
        _db = db;
        _credentials = credentials;
        _logger = logger ?? NullLogger<MailRepository>.Instance;
        _tags = tags ?? new TagRepository(db);
    }

    private static string Iso(DateTime d) => TimeUtil.AsUtc(d).ToString("o");

    // ────────── accounts ──────────

    private const string AccountColumns = @"id, name, address, display_name, aliases, provider, username,
        imap_host, imap_port, imap_security, smtp_host, smtp_port, smtp_security,
        state, last_error, last_sync_at, backfill_done, created_by, created_at, updated_at";

    private static MailAccount Fix(MailAccount a)
    {
        a.CreatedAt = TimeUtil.AsUtc(a.CreatedAt);
        a.UpdatedAt = TimeUtil.AsUtc(a.UpdatedAt);
        if (a.LastSyncAt is { } s) a.LastSyncAt = TimeUtil.AsUtc(s);
        return a;
    }

    public async Task<IReadOnlyList<MailAccount>> ListAccountsAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<MailAccount>(new CommandDefinition(
            $"SELECT {AccountColumns} FROM mail_accounts ORDER BY created_at", cancellationToken: ct));
        return rows.Select(Fix).ToList();
    }

    public async Task<MailAccount?> GetAccountAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        var a = await db.QuerySingleOrDefaultAsync<MailAccount>(new CommandDefinition(
            $"SELECT {AccountColumns} FROM mail_accounts WHERE id = @id", new { id }, cancellationToken: ct));
        return a is null ? null : Fix(a);
    }

    public async Task<MailAccount> CreateAccountAsync(ContextRef ctx, MailAccount account, string password, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        account.Id = Ulid.NewUlid().ToString();
        account.State = MailAccountStates.New;
        account.LastError = null;
        account.LastSyncAt = null;
        account.BackfillDone = false;
        account.CreatedAt = account.UpdatedAt = now;
        var secret = await _credentials.ProtectAsync(account.Id, password, ct);
        using var db = _db.CreateContextConnection(ctx);
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO mail_accounts (id, name, address, display_name, aliases, provider, username, secret,
                imap_host, imap_port, imap_security, smtp_host, smtp_port, smtp_security,
                state, backfill_done, created_by, created_at, updated_at)
            VALUES (@Id, @Name, @Address, @DisplayName, @Aliases, @Provider, @Username, @Secret,
                @ImapHost, @ImapPort, @ImapSecurity, @SmtpHost, @SmtpPort, @SmtpSecurity,
                @State, 0, @CreatedBy, @Created, @Created)",
            new
            {
                account.Id,
                account.Name,
                account.Address,
                account.DisplayName,
                account.Aliases,
                account.Provider,
                account.Username,
                Secret = secret,
                account.ImapHost,
                account.ImapPort,
                account.ImapSecurity,
                account.SmtpHost,
                account.SmtpPort,
                account.SmtpSecurity,
                account.State,
                account.CreatedBy,
                Created = Iso(now),
            }, cancellationToken: ct));
        return account;
    }

    public async Task<MailAccount?> UpdateAccountAsync(ContextRef ctx, string id, MailAccountUpdate update, CancellationToken ct = default)
    {
        var current = await GetAccountAsync(ctx, id, ct);
        if (current is null) return null;
        byte[]? secret = update.Password is { Length: > 0 } pw ? await _credentials.ProtectAsync(id, pw, ct) : null;
        using var db = _db.CreateContextConnection(ctx);
        await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE mail_accounts SET
                name = COALESCE(@Name, name),
                display_name = CASE WHEN @SetDisplay THEN @DisplayName ELSE display_name END,
                aliases = COALESCE(@Aliases, aliases),
                secret = COALESCE(@Secret, secret),
                state = CASE WHEN @Secret IS NULL THEN state ELSE 'new' END,
                last_error = CASE WHEN @Secret IS NULL THEN last_error ELSE NULL END,
                updated_at = @Now
            WHERE id = @id",
            new
            {
                id,
                update.Name,
                SetDisplay = update.DisplayName is not null,
                DisplayName = string.IsNullOrWhiteSpace(update.DisplayName) ? null : update.DisplayName.Trim(),
                Aliases = update.Aliases is null ? null : System.Text.Json.JsonSerializer.Serialize(update.Aliases),
                Secret = secret,
                Now = Iso(DateTime.UtcNow),
            }, cancellationToken: ct));
        return await GetAccountAsync(ctx, id, ct);
    }

    public async Task<bool> DeleteAccountAsync(ContextRef ctx, string id, CancellationToken ct = default) =>
        await _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var ids = (await db.QueryAsync<string>(new CommandDefinition(
                "SELECT id FROM mail_messages WHERE account_id = @id", new { id }, tx, cancellationToken: token))).ToList();
            foreach (var chunk in ids.Chunk(500))
                await RemoveRowsAsync(db, tx, chunk, token);
            await db.ExecuteAsync(new CommandDefinition(@"
                DELETE FROM mail_locations WHERE account_id = @id;
                DELETE FROM mail_folders WHERE account_id = @id;",
                new { id }, tx, cancellationToken: token));
            return await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM mail_accounts WHERE id = @id", new { id }, tx, cancellationToken: token)) > 0;
        }, ct);

    /// <summary>How many messages each account has here.</summary>
    public async Task<Dictionary<string, int>> CountsAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<(string AccountId, int N)>(new CommandDefinition(
            "SELECT account_id, COUNT(*) FROM mail_messages GROUP BY account_id", cancellationToken: ct));
        return rows.ToDictionary(r => r.AccountId, r => r.N);
    }

    // ────────── reading ──────────

    // Everything but the bodies: what a list and a thread header need.
    private const string LightColumns = @"id, account_id, message_key, thread_id, in_reply_to, refs, direction, state,
        from_name, from_address, to_list, cc_list, bcc_list, reply_to, subject, sent_at, snippet,
        attachments, size, seen, flagged, tags, list_id, created_at, updated_at";

    private static MailMessage Fix(MailMessage m)
    {
        m.SentAt = TimeUtil.AsUtc(m.SentAt);
        m.CreatedAt = TimeUtil.AsUtc(m.CreatedAt);
        m.UpdatedAt = TimeUtil.AsUtc(m.UpdatedAt);
        return m;
    }

    /// <summary>
    /// The list (decision 3): one row per conversation, newest activity first.
    /// A thread is in the list while one of its messages is in the inbox or
    /// sent, archived otherwise; filters narrow the messages that count.
    /// </summary>
    public async Task<IReadOnlyList<MailThread>> ListThreadsAsync(ContextRef ctx, MailListQuery q, CancellationToken ct = default)
    {
        var where = new List<string>();
        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(q.AccountId)) { where.Add("m.account_id = @account"); p.Add("account", q.AccountId); }
        if (!string.IsNullOrWhiteSpace(q.Tag))
        {
            where.Add("m.thread_id IN (SELECT t.thread_id FROM mail_messages t, json_each(t.tags) j WHERE j.value = @tag)");
            p.Add("tag", q.Tag);
        }
        if (!string.IsNullOrWhiteSpace(q.Address))
        {
            where.Add("m.thread_id IN (SELECT t.thread_id FROM mail_messages t JOIN mail_addresses a ON a.message_id = t.id WHERE a.address = @address)");
            p.Add("address", q.Address.Trim().ToLowerInvariant());
        }
        if (FtsQuery(q.Text) is { } match)
        {
            where.Add("m.thread_id IN (SELECT t.thread_id FROM mail_messages t WHERE t.rowid IN (SELECT rowid FROM mail_fts WHERE mail_fts MATCH @match))");
            p.Add("match", match);
        }
        var having = new List<string> { q.Archived ? "active = 0" : "active = 1" };
        if (q.UnreadOnly) having.Add("unread > 0");
        if (q.Before is { } before) { having.Add("latest < @before"); p.Add("before", Iso(before)); }
        p.Add("limit", Math.Clamp(q.Limit, 1, 200));

        using var db = _db.CreateContextConnection(ctx);
        var heads = (await db.QueryAsync<(string ThreadId, string Latest, long Unread, long Active)>(new CommandDefinition($@"
            SELECT m.thread_id, MAX(m.sent_at) AS latest,
                   SUM(CASE WHEN m.direction = 'in' AND m.seen = 0 THEN 1 ELSE 0 END) AS unread,
                   MAX(CASE WHEN m.state IN ('inbox', 'sent') THEN 1 ELSE 0 END) AS active
            FROM mail_messages m
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            GROUP BY m.thread_id
            HAVING {string.Join(" AND ", having)}
            ORDER BY latest DESC
            LIMIT @limit", p, cancellationToken: ct))).ToList();
        if (heads.Count == 0) return [];

        // Arrays for every IN: a List<string> is a JSON column to Dapper here
        // (JsonTagsHandler), and a handled type is never expanded.
        var ids = heads.Select(h => h.ThreadId).ToArray();
        var messages = (await db.QueryAsync<MailMessage>(new CommandDefinition(
            $"SELECT {LightColumns} FROM mail_messages WHERE thread_id IN @ids ORDER BY sent_at",
            new { ids }, cancellationToken: ct))).Select(Fix).ToList();
        var byThread = messages.GroupBy(m => m.ThreadId).ToDictionary(g => g.Key, g => Distinct(g).ToList());
        return heads.Where(h => byThread.ContainsKey(h.ThreadId)).Select(h => ToThread(byThread[h.ThreadId], h.Active == 0)).ToList();
    }

    /// <summary>The same message in two accounts (sent from one to the
    /// other) shows once — the outgoing copy wins.</summary>
    private static IEnumerable<MailMessage> Distinct(IEnumerable<MailMessage> list) =>
        list.GroupBy(m => m.MessageKey)
            .Select(g => g.OrderBy(m => m.Direction == MailDirections.Out ? 0 : 1).First())
            .OrderBy(m => m.SentAt);

    private static MailThread ToThread(List<MailMessage> ms, bool archived)
    {
        var first = ms[0];
        var latest = ms[^1];
        var people = new List<MailPerson>();
        void Add(string? name, string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            if (people.Any(x => x.Address.Equals(address, StringComparison.OrdinalIgnoreCase))) return;
            people.Add(new MailPerson(name, address));
        }
        // Who the conversation is with: the senders of what came in, the
        // recipients of what went out.
        foreach (var m in ms)
        {
            if (m.Direction == MailDirections.In) Add(m.FromName, m.FromAddress);
            else foreach (var r in m.ToList.Concat(m.CcList)) Add(r.Name, r.Address);
        }
        return new MailThread
        {
            ThreadId = first.ThreadId,
            Subject = first.Subject,
            LatestAt = latest.SentAt,
            LatestDirection = latest.Direction,
            Snippet = latest.Snippet,
            Count = ms.Count,
            Unread = ms.Count(m => m.Direction == MailDirections.In && !m.Seen),
            Flagged = ms.Any(m => m.Flagged),
            Archived = archived,
            HasAttachments = ms.Any(m => m.Attachments.Count > 0),
            Participants = people,
            Tags = ms.SelectMany(m => m.Tags).Distinct(StringComparer.Ordinal).ToList(),
            AccountIds = ms.Select(m => m.AccountId).Distinct().ToList(),
        };
    }

    public async Task<IReadOnlyList<MailMessage>> GetThreadAsync(ContextRef ctx, string threadId, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<MailMessage>(new CommandDefinition(
            "SELECT * FROM mail_messages WHERE thread_id = @threadId ORDER BY sent_at", new { threadId }, cancellationToken: ct));
        return Distinct(rows.Select(Fix)).ToList();
    }

    public async Task<MailMessage?> GetMessageAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        var m = await db.QuerySingleOrDefaultAsync<MailMessage>(new CommandDefinition(
            "SELECT * FROM mail_messages WHERE id = @id", new { id }, cancellationToken: ct));
        return m is null ? null : Fix(m);
    }

    public async Task<int> UnreadCountAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(@"
            SELECT COUNT(*) FROM (
                SELECT thread_id FROM mail_messages GROUP BY thread_id
                HAVING MAX(CASE WHEN state IN ('inbox', 'sent') THEN 1 ELSE 0 END) = 1
                   AND SUM(CASE WHEN direction = 'in' AND seen = 0 THEN 1 ELSE 0 END) > 0)", cancellationToken: ct));
    }

    public Task<IReadOnlyList<string>?> SetThreadTagsAsync(ContextRef ctx, string threadId, IReadOnlyList<string> tags, CancellationToken ct = default) =>
        _db.WithContextTransactionAsync<IReadOnlyList<string>?>(ctx, async (db, tx, token) =>
        {
            if (await db.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COUNT(*) FROM mail_messages WHERE thread_id = @threadId", new { threadId }, tx, cancellationToken: token)) == 0)
                return null;
            // The workspace's tags, like notes': a new name gets its row and colour.
            var clean = (await _tags.EnsureExistsAsync(db, tx, tags, token)).ToList();
            var n = await db.ExecuteAsync(new CommandDefinition(
                "UPDATE mail_messages SET tags = @tags, updated_at = @now WHERE thread_id = @threadId",
                new { tags = clean, now = Iso(DateTime.UtcNow), threadId }, tx, cancellationToken: token));
            return n == 0 ? null : clean;
        }, ct);

    public async Task<IReadOnlyList<MailLocationRef>> SetThreadSeenAsync(ContextRef ctx, string threadId, bool seen, CancellationToken ct = default) =>
        await _db.WithContextTransactionAsync<IReadOnlyList<MailLocationRef>>(ctx, async (db, tx, token) =>
        {
            var changed = (await db.QueryAsync<string>(new CommandDefinition(
                "SELECT id FROM mail_messages WHERE thread_id = @threadId AND direction = 'in' AND seen <> @seen",
                new { threadId, seen }, tx, cancellationToken: token))).ToArray();
            if (changed.Length == 0) return [];
            await db.ExecuteAsync(new CommandDefinition(
                "UPDATE mail_messages SET seen = @seen, updated_at = @now WHERE id IN @changed",
                new { seen, now = Iso(DateTime.UtcNow), changed }, tx, cancellationToken: token));
            return (await db.QueryAsync<(string AccountId, string Role, long Uid)>(new CommandDefinition(
                "SELECT account_id, role, uid FROM mail_locations WHERE message_id IN @changed",
                new { changed }, tx, cancellationToken: token)))
                .Select(l => new MailLocationRef(l.AccountId, l.Role, (uint)l.Uid)).ToList();
        }, ct);

    public async Task<IReadOnlyList<MailLocationRef>> LocationsAsync(ContextRef ctx, string messageId, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        return (await db.QueryAsync<(string AccountId, string Role, long Uid)>(new CommandDefinition(
            "SELECT account_id, role, uid FROM mail_locations WHERE message_id = @messageId ORDER BY role",
            new { messageId }, cancellationToken: ct)))
            .Select(l => new MailLocationRef(l.AccountId, l.Role, (uint)l.Uid)).ToList();
    }

    /// <summary>Words → an FTS5 prefix query (AND), like notes'; null when nothing is left.</summary>
    internal static string? FtsQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words.Count == 0 ? null : string.Join(" AND ", words.Select(w => $"\"{w}\"*"));
    }

    // ────────── the sync's side ──────────

    public sealed record FolderState(string AccountId, string Role, string Name, long UidValidity, long UidNext, long? HighestModseq, long BackfillBelow);

    /// <summary>Every account of the workspace with its encrypted password.</summary>
    public async Task<IReadOnlyList<(MailAccount Account, byte[] Secret)>> SyncAccountsAsync(ContextRef ctx, CancellationToken ct)
    {
        using var db = _db.CreateContextConnection(ctx);
        var accounts = (await db.QueryAsync<MailAccount>(new CommandDefinition(
            $"SELECT {AccountColumns} FROM mail_accounts", cancellationToken: ct))).Select(Fix).ToList();
        var result = new List<(MailAccount, byte[])>();
        foreach (var a in accounts)
        {
            var secret = await db.ExecuteScalarAsync<byte[]>(new CommandDefinition(
                "SELECT secret FROM mail_accounts WHERE id = @Id", new { a.Id }, cancellationToken: ct));
            result.Add((a, secret ?? []));
        }
        return result;
    }

    public Task<string?> PasswordAsync(string accountId, byte[] secret, CancellationToken ct) =>
        _credentials.UnprotectAsync(accountId, secret, ct);

    public async Task<string?> PasswordAsync(ContextRef ctx, string accountId, CancellationToken ct)
    {
        using var db = _db.CreateContextConnection(ctx);
        var secret = await db.ExecuteScalarAsync<byte[]>(new CommandDefinition(
            "SELECT secret FROM mail_accounts WHERE id = @accountId", new { accountId }, cancellationToken: ct));
        return secret is null ? null : await _credentials.UnprotectAsync(accountId, secret, ct);
    }

    public async Task SetAccountStateAsync(ContextRef ctx, string accountId, string state, string? error, bool? backfillDone, CancellationToken ct)
    {
        using var db = _db.CreateContextConnection(ctx);
        await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE mail_accounts SET state = @state, last_error = @error, last_sync_at = @now,
                backfill_done = COALESCE(@backfillDone, backfill_done)
            WHERE id = @accountId",
            new { accountId, state, error, now = Iso(DateTime.UtcNow), backfillDone }, cancellationToken: ct));
    }

    public async Task<FolderState?> GetFolderAsync(ContextRef ctx, string accountId, string role, CancellationToken ct)
    {
        using var db = _db.CreateContextConnection(ctx);
        return await db.QuerySingleOrDefaultAsync<FolderState>(new CommandDefinition(@"
            SELECT account_id AS AccountId, role AS Role, name AS Name, uid_validity AS UidValidity, uid_next AS UidNext,
                   highest_modseq AS HighestModseq, backfill_below AS BackfillBelow
            FROM mail_folders WHERE account_id = @accountId AND role = @role",
            new { accountId, role }, cancellationToken: ct));
    }

    public async Task SaveFolderAsync(ContextRef ctx, FolderState f, CancellationToken ct)
    {
        using var db = _db.CreateContextConnection(ctx);
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO mail_folders (account_id, role, name, uid_validity, uid_next, highest_modseq, backfill_below)
            VALUES (@AccountId, @Role, @Name, @UidValidity, @UidNext, @HighestModseq, @BackfillBelow)
            ON CONFLICT(account_id, role) DO UPDATE SET name = excluded.name, uid_validity = excluded.uid_validity,
                uid_next = excluded.uid_next, highest_modseq = excluded.highest_modseq, backfill_below = excluded.backfill_below",
            f, cancellationToken: ct));
    }

    /// <summary>A folder whose UIDs mean something else now (UIDVALIDITY changed,
    /// or another folder plays the role): its places go; the messages stay and
    /// are found again by their key as the folder is read anew.</summary>
    public async Task DropLocationsAsync(ContextRef ctx, string accountId, string role, CancellationToken ct)
    {
        using var db = _db.CreateContextConnection(ctx);
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mail_locations WHERE account_id = @accountId AND role = @role",
            new { accountId, role }, cancellationToken: ct));
    }

    /// <summary>uid → message id for one folder.</summary>
    public async Task<Dictionary<uint, string>> FolderLocationsAsync(ContextRef ctx, string accountId, string role, CancellationToken ct)
    {
        using var db = _db.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<(long Uid, string MessageId)>(new CommandDefinition(
            "SELECT uid, message_id FROM mail_locations WHERE account_id = @accountId AND role = @role",
            new { accountId, role }, cancellationToken: ct));
        return rows.ToDictionary(r => (uint)r.Uid, r => r.MessageId);
    }

    public async Task<string?> FindByKeyAsync(ContextRef ctx, string accountId, string key, CancellationToken ct)
    {
        using var db = _db.CreateContextConnection(ctx);
        return await db.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT id FROM mail_messages WHERE account_id = @accountId AND message_key = @key",
            new { accountId, key }, cancellationToken: ct));
    }

    /// <summary>A message already here turned up in (another) folder: one more
    /// place, its flags as that folder has them, its state anew.</summary>
    public Task AddLocationAsync(ContextRef ctx, string messageId, string accountId, string role, uint uid, bool seen, bool flagged, CancellationToken ct) =>
        _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            await db.ExecuteAsync(new CommandDefinition(@"
                INSERT OR REPLACE INTO mail_locations (message_id, account_id, role, uid) VALUES (@messageId, @accountId, @role, @uid);
                UPDATE mail_messages SET seen = @seen, flagged = @flagged,
                    direction = CASE WHEN @role = 'sent' THEN 'out' ELSE direction END
                WHERE id = @messageId;",
                new { messageId, accountId, role, uid = (long)uid, seen, flagged }, tx, cancellationToken: token));
            await RestateAsync(db, tx, [messageId], token);
        }, ct);

    /// <summary>Places the server no longer has; returns the messages they belonged to.</summary>
    public Task<IReadOnlyList<string>> RemoveLocationsAsync(ContextRef ctx, string accountId, string role, IReadOnlyCollection<uint> uids, CancellationToken ct) =>
        _db.WithContextTransactionAsync<IReadOnlyList<string>>(ctx, async (db, tx, token) =>
        {
            var affected = new HashSet<string>();
            foreach (var chunk in uids.Select(u => (long)u).Chunk(500))
            {
                var ids = await db.QueryAsync<string>(new CommandDefinition(
                    "SELECT message_id FROM mail_locations WHERE account_id = @accountId AND role = @role AND uid IN @chunk",
                    new { accountId, role, chunk }, tx, cancellationToken: token));
                affected.UnionWith(ids);
                await db.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM mail_locations WHERE account_id = @accountId AND role = @role AND uid IN @chunk",
                    new { accountId, role, chunk }, tx, cancellationToken: token));
            }
            await RestateAsync(db, tx, affected.ToList(), token);
            return affected.ToList();
        }, ct);

    /// <summary>Seen and flagged as one folder has them now.</summary>
    public Task ApplyFlagsAsync(ContextRef ctx, string accountId, string role, IReadOnlyList<(uint Uid, bool Seen, bool Flagged)> flags, CancellationToken ct) =>
        _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            foreach (var f in flags)
                await db.ExecuteAsync(new CommandDefinition(@"
                    UPDATE mail_messages SET seen = @Seen, flagged = @Flagged
                    WHERE id = (SELECT message_id FROM mail_locations WHERE account_id = @accountId AND role = @role AND uid = @Uid)
                      AND (seen <> @Seen OR flagged <> @Flagged)",
                    new { accountId, role, Uid = (long)f.Uid, f.Seen, f.Flagged }, tx, cancellationToken: token));
        }, ct);

    /// <summary>
    /// A new message: stored with its place, its references and addresses, in
    /// the full-text index — and in a thread (decision 4): the thread of any
    /// message it points at, that points at it, or that is the same message
    /// in another account; several such threads become one. Without any, a
    /// reply's subject and a shared participant within 30 days find it.
    /// </summary>
    public Task<string> InsertAsync(ContextRef ctx, MailMessage m, string role, uint uid, CancellationToken ct) =>
        _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var now = DateTime.UtcNow;
            m.Id = Ulid.NewUlid().ToString();
            m.CreatedAt = m.UpdatedAt = now;
            var subjectKey = MailThreading.SubjectKey(m.Subject);
            var parents = MailThreading.Parents(m.MessageKey, m.InReplyTo, m.Refs);
            var addresses = Addresses(m).ToList();

            var threads = (await db.QueryAsync<string>(new CommandDefinition(@"
                SELECT thread_id FROM mail_messages WHERE message_key IN @keys
                UNION
                SELECT t.thread_id FROM mail_refs r JOIN mail_messages t ON t.id = r.message_id WHERE r.ref = @key",
                new { keys = parents.Append(m.MessageKey).ToArray(), key = m.MessageKey }, tx, cancellationToken: token)))
                .Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();

            if (threads.Count == 0 && subjectKey is not null && (parents.Count > 0 || MailThreading.IsReplySubject(m.Subject)))
            {
                var people = addresses.Select(a => a.Address).Distinct().ToArray();
                var byTopic = await db.ExecuteScalarAsync<string?>(new CommandDefinition(@"
                    SELECT t.thread_id FROM mail_messages t
                    WHERE t.subject_key = @subjectKey AND t.sent_at >= @from AND t.sent_at <= @to
                      AND EXISTS (SELECT 1 FROM mail_addresses a WHERE a.message_id = t.id AND a.address IN @people)
                    ORDER BY t.sent_at DESC LIMIT 1",
                    new
                    {
                        subjectKey,
                        people,
                        from = Iso(m.SentAt - MailThreading.SubjectWindow),
                        to = Iso(m.SentAt + MailThreading.SubjectWindow),
                    }, tx, cancellationToken: token));
                if (byTopic is not null) threads.Add(byTopic);
            }

            m.ThreadId = threads.Count > 0 ? threads[0] : m.Id;
            if (threads.Count > 1)
                await db.ExecuteAsync(new CommandDefinition(
                    "UPDATE mail_messages SET thread_id = @into WHERE thread_id IN @others",
                    new { into = m.ThreadId, others = threads.Skip(1).ToArray() }, tx, cancellationToken: token));

            await db.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO mail_messages (id, account_id, message_key, thread_id, in_reply_to, refs, direction, state,
                    from_name, from_address, to_list, cc_list, bcc_list, reply_to, subject, subject_key, sent_at, snippet,
                    body_text, body_html, attachments, size, seen, flagged, tags, list_id, created_at, updated_at)
                VALUES (@Id, @AccountId, @MessageKey, @ThreadId, @InReplyTo, @Refs, @Direction, @State,
                    @FromName, @FromAddress, @ToList, @CcList, @BccList, @ReplyTo, @Subject, @SubjectKey, @SentAtIso, @Snippet,
                    @BodyText, @BodyHtml, @Attachments, @Size, @Seen, @Flagged, @Tags, @ListId, @Now, @Now)",
                new
                {
                    m.Id,
                    m.AccountId,
                    m.MessageKey,
                    m.ThreadId,
                    m.InReplyTo,
                    m.Refs,
                    m.Direction,
                    State = MailStates.Archived,
                    m.FromName,
                    m.FromAddress,
                    m.ToList,
                    m.CcList,
                    m.BccList,
                    m.ReplyTo,
                    m.Subject,
                    SubjectKey = subjectKey,
                    SentAtIso = Iso(m.SentAt),
                    m.Snippet,
                    m.BodyText,
                    m.BodyHtml,
                    m.Attachments,
                    m.Size,
                    m.Seen,
                    m.Flagged,
                    m.Tags,
                    m.ListId,
                    Now = Iso(now),
                }, tx, cancellationToken: token));
            await db.ExecuteAsync(new CommandDefinition(
                "INSERT OR REPLACE INTO mail_locations (message_id, account_id, role, uid) VALUES (@Id, @AccountId, @role, @uid)",
                new { m.Id, m.AccountId, role, uid = (long)uid }, tx, cancellationToken: token));
            foreach (var r in parents)
                await db.ExecuteAsync(new CommandDefinition(
                    "INSERT OR IGNORE INTO mail_refs (message_id, ref) VALUES (@Id, @r)", new { m.Id, r }, tx, cancellationToken: token));
            foreach (var a in addresses)
                await db.ExecuteAsync(new CommandDefinition(
                    "INSERT OR IGNORE INTO mail_addresses (message_id, address, field) VALUES (@Id, @Address, @Field)",
                    new { m.Id, a.Address, a.Field }, tx, cancellationToken: token));
            await IndexAsync(db, tx, m, token);
            await RestateAsync(db, tx, [m.Id], token);
            return m.Id;
        }, ct);

    private static IEnumerable<(string Address, string Field)> Addresses(MailMessage m)
    {
        if (!string.IsNullOrWhiteSpace(m.FromAddress)) yield return (m.FromAddress.Trim().ToLowerInvariant(), "from");
        foreach (var (list, field) in new[] { (m.ToList, "to"), (m.CcList, "cc"), (m.BccList, "bcc") })
            foreach (var p in list)
                if (!string.IsNullOrWhiteSpace(p.Address)) yield return (p.Address.Trim().ToLowerInvariant(), field);
    }

    private static async Task IndexAsync(IDbConnection db, IDbTransaction tx, MailMessage m, CancellationToken ct)
    {
        var people = string.Join(" ", new[] { m.FromName, m.FromAddress }
            .Concat(m.ToList.Concat(m.CcList).SelectMany(p => new[] { p.Name, p.Address }))
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO mail_fts (rowid, subject, people, body)
            VALUES ((SELECT rowid FROM mail_messages WHERE id = @Id), @Subject, @people, @body)",
            new { m.Id, Subject = m.Subject ?? "", people, body = m.BodyText ?? "" }, tx, cancellationToken: ct));
    }

    /// <summary>A message's state from its places: inbox → inbox, else sent →
    /// sent, else archived (a message with no place left stays archived until
    /// the sync hands it to the trash).</summary>
    private static Task RestateAsync(IDbConnection db, IDbTransaction tx, IReadOnlyList<string> ids, CancellationToken ct) =>
        ids.Count == 0 ? Task.CompletedTask : db.ExecuteAsync(new CommandDefinition(@"
            UPDATE mail_messages SET state = CASE
                WHEN EXISTS (SELECT 1 FROM mail_locations l WHERE l.message_id = mail_messages.id AND l.role = 'inbox') THEN 'inbox'
                WHEN EXISTS (SELECT 1 FROM mail_locations l WHERE l.message_id = mail_messages.id AND l.role = 'sent') THEN 'sent'
                ELSE 'archived' END
            WHERE id IN @ids", new { ids = ids.ToArray() }, tx, cancellationToken: ct));

    /// <summary>
    /// The messages among <paramref name="ids"/> that lie nowhere on the
    /// server any more go to the trash (decision 9 — a delete on the phone is
    /// recoverable here). Returns how many went.
    /// </summary>
    public Task<int> TrashOrphansAsync(ContextRef ctx, IReadOnlyCollection<string> ids, CancellationToken ct) =>
        ids.Count == 0 ? Task.FromResult(0) : _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var orphans = (await db.QueryAsync<string>(new CommandDefinition(@"
                SELECT id FROM mail_messages WHERE id IN @ids
                  AND NOT EXISTS (SELECT 1 FROM mail_locations l WHERE l.message_id = mail_messages.id)",
                new { ids = ids.ToArray() }, tx, cancellationToken: token))).ToList();
            foreach (var id in orphans)
                await TrashSnapshots.TakeAsync(db, tx, ctx, TrashKinds.Mail, id, null, token);
            await RemoveRowsAsync(db, tx, orphans, token);
            return orphans.Count;
        }, ct);

    /// <summary>Removes messages with everything that indexes them.</summary>
    private static async Task RemoveRowsAsync(IDbConnection db, IDbTransaction tx, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        await db.ExecuteAsync(new CommandDefinition(@"
            DELETE FROM mail_fts WHERE rowid IN (SELECT rowid FROM mail_messages WHERE id IN @ids);
            DELETE FROM mail_refs WHERE message_id IN @ids;
            DELETE FROM mail_addresses WHERE message_id IN @ids;
            DELETE FROM mail_locations WHERE message_id IN @ids;
            DELETE FROM mail_messages WHERE id IN @ids;",
            new { ids = ids.ToArray() }, tx, cancellationToken: ct));
    }

    /// <summary>After a trash restore: the restored row is back, its indexes
    /// are rebuilt (it lies nowhere on the server, so it stays archived).</summary>
    internal static async Task ReindexRestoredAsync(IDbConnection db, IDbTransaction tx, string id, CancellationToken ct)
    {
        var m = await db.QuerySingleOrDefaultAsync<MailMessage>(new CommandDefinition(
            "SELECT * FROM mail_messages WHERE id = @id", new { id }, tx, cancellationToken: ct));
        if (m is null) return;
        foreach (var r in MailThreading.Parents(m.MessageKey, m.InReplyTo, m.Refs))
            await db.ExecuteAsync(new CommandDefinition(
                "INSERT OR IGNORE INTO mail_refs (message_id, ref) VALUES (@id, @r)", new { id, r }, tx, cancellationToken: ct));
        foreach (var a in Addresses(m))
            await db.ExecuteAsync(new CommandDefinition(
                "INSERT OR IGNORE INTO mail_addresses (message_id, address, field) VALUES (@id, @Address, @Field)",
                new { id, a.Address, a.Field }, tx, cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mail_fts WHERE rowid = (SELECT rowid FROM mail_messages WHERE id = @id)", new { id }, tx, cancellationToken: ct));
        await IndexAsync(db, tx, m, ct);
        await RestateAsync(db, tx, [id], ct);
    }
}
