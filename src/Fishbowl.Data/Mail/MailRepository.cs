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
        var rows = (await db.QueryAsync<MailAccount>(new CommandDefinition(
            $"SELECT {AccountColumns} FROM mail_accounts ORDER BY created_at", cancellationToken: ct))).Select(Fix).ToList();
        await EnsureSourceTagsAsync(db, null, rows, ct);
        return rows;
    }

    /// <summary>Every account's source tag is a system tag of the workspace
    /// (fixed: not assigned, not removed, not renamed by hand); one whose
    /// account is gone or renamed goes.</summary>
    private static async Task EnsureSourceTagsAsync(IDbConnection db, IDbTransaction? tx, IReadOnlyList<MailAccount> accounts, CancellationToken ct)
    {
        var names = accounts.Select(a => a.SourceTag).Distinct().ToArray();
        // Read first: a list is a read, and a write would queue behind the sync's.
        var fixedNow = (await db.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM tags WHERE is_system = 1 AND user_assignable = 0", transaction: tx, cancellationToken: ct)))
            .Where(n => !SystemTags.ReservedNames.Contains(n)).ToHashSet();
        if (fixedNow.SetEquals(names)) return;
        var now = Iso(DateTime.UtcNow);
        foreach (var name in names)
            await db.ExecuteAsync(new CommandDefinition(@"
                INSERT OR IGNORE INTO tags(name, color, created_at, is_system, user_assignable, user_removable)
                VALUES (@name, @color, @now, 1, 0, 0)",
                new { name, color = TagPalette.DefaultFor(name), now }, tx, cancellationToken: ct));
        // Fixed tags nobody holds any more (a renamed or removed account's) go;
        // Fishbowl's own seeds stay.
        var keep = names.Concat(SystemTags.ReservedNames).ToArray();
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tags WHERE is_system = 1 AND user_assignable = 0 AND name NOT IN @keep",
            new { keep }, tx, cancellationToken: ct));
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
            INSERT OR IGNORE INTO tags(name, color, created_at, is_system, user_assignable, user_removable)
            VALUES (@name, @color, @now, 1, 0, 0)",
            new { name = account.SourceTag, color = TagPalette.DefaultFor(account.SourceTag), now = Iso(now) }, cancellationToken: ct));
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
                DELETE FROM mail_folders WHERE account_id = @id;
                DELETE FROM mail_tombstones WHERE account_id = @id;",
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
        attachments, size, seen, flagged, tags, list_id, sent_by, created_at, updated_at";

    // A message with its HTML packed as stored (v25); Dapper fills
    // BodyHtmlZ from body_html_z. body_html is only ever set on a row a trash
    // restore brought back from before v25.
    private sealed class StoredMessage : MailMessage
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public byte[]? BodyHtmlZ { get; set; }
    }

    private static MailMessage Unpacked(StoredMessage m)
    {
        m.BodyHtml ??= MailBodies.Unpack(m.BodyHtmlZ);
        m.BodyHtmlZ = null;
        return Fix(m);
    }

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
        // Tags, all of them (AND): a source tag is its account's mail, any
        // other the messages that carry it.
        var accounts = await ListAccountsAsync(ctx, ct);
        var tags = q.Tags.Append(q.Tag ?? "").Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        for (var i = 0; i < tags.Count; i++)
        {
            var from = accounts.Where(a => a.SourceTag == tags[i]).Select(a => a.Id).ToArray();
            if (from.Length > 0)
            {
                where.Add($"m.thread_id IN (SELECT t.thread_id FROM mail_messages t WHERE t.account_id IN @src{i})");
                p.Add($"src{i}", from);
            }
            else
            {
                where.Add($"m.thread_id IN (SELECT t.thread_id FROM mail_messages t, json_each(t.tags) j WHERE j.value = @tag{i})");
                p.Add($"tag{i}", tags[i]);
            }
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
        var source = accounts.ToDictionary(a => a.Id, a => a.SourceTag);
        return heads.Where(h => byThread.ContainsKey(h.ThreadId)).Select(h =>
        {
            var thread = ToThread(byThread[h.ThreadId], h.Active == 0);
            // Where it came from first, then what was given to it.
            thread.Tags = thread.AccountIds.Select(id => source.GetValueOrDefault(id)).Where(s => s is not null).Select(s => s!)
                .Distinct().Concat(thread.Tags).ToList();
            return thread;
        }).ToList();
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
        var rows = await db.QueryAsync<StoredMessage>(new CommandDefinition(
            "SELECT * FROM mail_messages WHERE thread_id = @threadId ORDER BY sent_at", new { threadId }, cancellationToken: ct));
        return Distinct(rows.Select(Unpacked)).ToList();
    }

    public async Task<MailMessage?> GetMessageAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        var m = await db.QuerySingleOrDefaultAsync<StoredMessage>(new CommandDefinition(
            "SELECT * FROM mail_messages WHERE id = @id", new { id }, cancellationToken: ct));
        return m is null ? null : Unpacked(m);
    }

    /// <summary>The tags mail carries, with how many conversations: each
    /// account's source tag, then every tag given to a message.</summary>
    public async Task<IReadOnlyList<(string Name, int Count, bool Source)>> TagCountsAsync(ContextRef ctx, CancellationToken ct = default)
    {
        var accounts = await ListAccountsAsync(ctx, ct);
        using var db = _db.CreateContextConnection(ctx);
        var perAccount = (await db.QueryAsync<(string AccountId, int N)>(new CommandDefinition(
            "SELECT account_id, COUNT(DISTINCT thread_id) FROM mail_messages GROUP BY account_id", cancellationToken: ct)))
            .ToDictionary(r => r.AccountId, r => r.N);
        var given = await db.QueryAsync<(string Name, int N)>(new CommandDefinition(
            "SELECT j.value, COUNT(DISTINCT m.thread_id) FROM mail_messages m, json_each(m.tags) j GROUP BY j.value ORDER BY 2 DESC, 1",
            cancellationToken: ct));
        return accounts.GroupBy(a => a.SourceTag)
            .Select(g => (g.Key, g.Sum(a => perAccount.GetValueOrDefault(a.Id)), true))
            .Concat(given.Select(r => (r.Name, r.N, false))).ToList();
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
            // System tags nobody assigns (a source) are never given by hand.
            var fixedNames = (await db.QueryAsync<string>(new CommandDefinition(
                "SELECT name FROM tags WHERE is_system = 1 AND user_assignable = 0", transaction: tx, cancellationToken: token))).ToHashSet();
            var wanted = tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToLowerInvariant())
                .Where(t => !fixedNames.Contains(t)).ToList();
            var clean = (await _tags.EnsureExistsAsync(db, tx, wanted, token)).ToList();
            var n = await db.ExecuteAsync(new CommandDefinition(
                "UPDATE mail_messages SET tags = @tags, updated_at = @now WHERE thread_id = @threadId",
                new { tags = clean, now = Iso(DateTime.UtcNow), threadId }, tx, cancellationToken: token));
            return n == 0 ? null : clean;
        }, ct);

    /// <summary>Every message with what a rule looks at — the people, the
    /// subject, the list, the account — and its thread, state and tags; no text.</summary>
    public async Task<IReadOnlyList<MailMessage>> RuleCandidatesAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        return (await db.QueryAsync<MailMessage>(new CommandDefinition(@"
            SELECT id, account_id, thread_id, direction, state, seen, from_name, from_address, to_list, cc_list, bcc_list,
                   subject, list_id, tags
            FROM mail_messages", cancellationToken: ct))).ToList();
    }

    /// <summary>Adds the tags to each message that hasn't got them all (a
    /// rule's tags, beside what it has); how many messages changed.</summary>
    public Task<int> AddTagsAsync(ContextRef ctx, IReadOnlyList<string> ids, IReadOnlyList<string> tags, CancellationToken ct = default) =>
        ids.Count == 0 || tags.Count == 0 ? Task.FromResult(0) : _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var clean = await _tags.EnsureExistsAsync(db, tx, tags, token);
            var now = Iso(DateTime.UtcNow);
            var changed = 0;
            foreach (var chunk in ids.Chunk(500))
                foreach (var (id, json) in await db.QueryAsync<(string Id, string Tags)>(new CommandDefinition(
                    "SELECT id, tags FROM mail_messages WHERE id IN @chunk", new { chunk }, tx, cancellationToken: token)))
                {
                    var has = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new();
                    var next = has.Concat(clean).Distinct(StringComparer.Ordinal).ToList();
                    if (next.Count == has.Count) continue;
                    await db.ExecuteAsync(new CommandDefinition(
                        "UPDATE mail_messages SET tags = @next, updated_at = @now WHERE id = @id", new { next, now, id }, tx, cancellationToken: token));
                    changed++;
                }
            return changed;
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

    /// <summary>
    /// Every unread conversation of the list (or of the archive) read at once —
    /// by the list's own rule (decision 3), whatever is loaded or filtered —
    /// and where its messages lie, for the server. In SQL, not by ids: an inbox
    /// may hold more unread mail than a statement takes parameters.
    /// </summary>
    public async Task<(int Conversations, IReadOnlyList<MailLocationRef> Places)> MarkAllSeenAsync(ContextRef ctx, bool archived, CancellationToken ct = default) =>
        await _db.WithContextTransactionAsync<(int, IReadOnlyList<MailLocationRef>)>(ctx, async (db, tx, token) =>
        {
            const string unread = @"
                SELECT m.id FROM mail_messages m
                WHERE m.direction = 'in' AND m.seen = 0 AND m.thread_id IN (
                    SELECT thread_id FROM mail_messages GROUP BY thread_id
                    HAVING MAX(CASE WHEN state IN ('inbox', 'sent') THEN 1 ELSE 0 END) = @active)";
            var active = archived ? 0 : 1;
            var conversations = await db.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(DISTINCT thread_id) FROM mail_messages WHERE id IN ({unread})", new { active }, tx, cancellationToken: token));
            if (conversations == 0) return (0, []);
            var places = (await db.QueryAsync<(string AccountId, string Role, long Uid)>(new CommandDefinition(
                $"SELECT account_id, role, uid FROM mail_locations WHERE message_id IN ({unread})", new { active }, tx, cancellationToken: token)))
                .Select(l => new MailLocationRef(l.AccountId, l.Role, (uint)l.Uid)).ToList();
            await db.ExecuteAsync(new CommandDefinition(
                $"UPDATE mail_messages SET seen = 1, updated_at = @now WHERE id IN ({unread})",
                new { active, now = Iso(DateTime.UtcNow) }, tx, cancellationToken: token));
            return (conversations, places);
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
        _db.WithContextTransactionAsync(ctx, (db, tx, token) => InsertCoreAsync(db, tx, m, role, uid, null, token), ct);

    /// <summary>A message just sent from here, at once in its conversation —
    /// in Sent where the server said its UID; without one it stands as sent
    /// until the sync finds it there (by its Message-ID). <paramref name="sentBy"/>
    /// is the API key that sent it, null for a person.</summary>
    public Task<string> InsertSentAsync(ContextRef ctx, MailMessage m, uint? sentUid, string? sentBy, CancellationToken ct) =>
        _db.WithContextTransactionAsync(ctx, (db, tx, token) =>
            InsertCoreAsync(db, tx, m, sentUid is null ? null : MailRoles.Sent, sentUid, sentBy, token), ct);

    private static async Task<string> InsertCoreAsync(IDbConnection db, IDbTransaction tx, MailMessage m, string? role, uint? uid, string? sentBy, CancellationToken token)
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
                    body_text, body_html_z, attachments, size, seen, flagged, tags, list_id, sent_by, created_at, updated_at)
                VALUES (@Id, @AccountId, @MessageKey, @ThreadId, @InReplyTo, @Refs, @Direction, @State,
                    @FromName, @FromAddress, @ToList, @CcList, @BccList, @ReplyTo, @Subject, @SubjectKey, @SentAtIso, @Snippet,
                    @BodyText, @BodyHtmlZ, @Attachments, @Size, @Seen, @Flagged, @Tags, @ListId, @sentBy, @Now, @Now)",
            new
            {
                m.Id,
                m.AccountId,
                m.MessageKey,
                m.ThreadId,
                m.InReplyTo,
                m.Refs,
                m.Direction,
                State = role is null && m.Direction == MailDirections.Out ? MailStates.Sent : MailStates.Archived,
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
                BodyHtmlZ = MailBodies.Pack(m.BodyHtml),
                m.Attachments,
                m.Size,
                m.Seen,
                m.Flagged,
                m.Tags,
                m.ListId,
                sentBy,
                Now = Iso(now),
            }, tx, cancellationToken: token));
        if (role is not null && uid is { } at)
            await db.ExecuteAsync(new CommandDefinition(
                "INSERT OR REPLACE INTO mail_locations (message_id, account_id, role, uid) VALUES (@Id, @AccountId, @role, @uid)",
                new { m.Id, m.AccountId, role, uid = (long)at }, tx, cancellationToken: token));
        foreach (var r in parents)
            await db.ExecuteAsync(new CommandDefinition(
                "INSERT OR IGNORE INTO mail_refs (message_id, ref) VALUES (@Id, @r)", new { m.Id, r }, tx, cancellationToken: token));
        foreach (var a in addresses)
            await db.ExecuteAsync(new CommandDefinition(
                "INSERT OR IGNORE INTO mail_addresses (message_id, address, field) VALUES (@Id, @Address, @Field)",
                new { m.Id, a.Address, a.Field }, tx, cancellationToken: token));
        await IndexAsync(db, tx, m, token);
        if (role is not null) await RestateAsync(db, tx, [m.Id], token);
        return m.Id;
    }

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
    /// sent unless it was archived here, else archived (a message with no place
    /// left stays archived until the sync hands it to the trash).</summary>
    private static Task RestateAsync(IDbConnection db, IDbTransaction tx, IReadOnlyList<string> ids, CancellationToken ct) =>
        ids.Count == 0 ? Task.CompletedTask : db.ExecuteAsync(new CommandDefinition(@"
            UPDATE mail_messages SET state = CASE
                WHEN EXISTS (SELECT 1 FROM mail_locations l WHERE l.message_id = mail_messages.id AND l.role = 'inbox') THEN 'inbox'
                WHEN archived = 0 AND EXISTS (SELECT 1 FROM mail_locations l WHERE l.message_id = mail_messages.id AND l.role = 'sent') THEN 'sent'
                ELSE 'archived' END
            WHERE id IN @ids", new { ids = ids.ToArray() }, tx, cancellationToken: ct));

    public async Task<IReadOnlyList<string>> ThreadMessageIdsAsync(ContextRef ctx, string threadId, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        return (await db.QueryAsync<string>(new CommandDefinition(
            "SELECT id FROM mail_messages WHERE thread_id = @threadId", new { threadId }, cancellationToken: ct))).ToList();
    }

    /// <summary>Where these messages lie, each place with its message.</summary>
    /// <summary>Every message of the conversations these messages are in.</summary>
    public Task<IReadOnlyList<string>> WholeThreadsOfAsync(ContextRef ctx, IReadOnlyCollection<string> messageIds, CancellationToken ct = default) =>
        IdsAsync(ctx, "SELECT id FROM mail_messages WHERE thread_id IN (SELECT thread_id FROM mail_messages WHERE id IN @chunk)", messageIds, ct);

    /// <summary>Every message of these conversations.</summary>
    public Task<IReadOnlyList<string>> ThreadsMessageIdsAsync(ContextRef ctx, IReadOnlyCollection<string> threadIds, CancellationToken ct = default) =>
        IdsAsync(ctx, "SELECT id FROM mail_messages WHERE thread_id IN @chunk", threadIds, ct);

    private async Task<IReadOnlyList<string>> IdsAsync(ContextRef ctx, string sql, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        using var db = _db.CreateContextConnection(ctx);
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in keys.Chunk(500))
            all.UnionWith(await db.QueryAsync<string>(new CommandDefinition(sql, new { chunk }, cancellationToken: ct)));
        return all.ToList();
    }

    public async Task<IReadOnlyList<MailPlace>> PlacesOfAsync(ContextRef ctx, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        using var db = _db.CreateContextConnection(ctx);
        return (await db.QueryAsync<(string MessageId, string AccountId, string Role, long Uid)>(new CommandDefinition(
            "SELECT message_id, account_id, role, uid FROM mail_locations WHERE message_id IN @ids",
            new { ids = ids.ToArray() }, cancellationToken: ct)))
            .Select(l => new MailPlace(l.MessageId, l.AccountId, l.Role, (uint)l.Uid)).ToList();
    }

    /// <summary>
    /// Archives messages here, or brings them back, after the server moved
    /// what lay in its folders: each move's old place goes (unless the server
    /// kept it — a Gmail label), its new one comes where the server said it.
    /// <paramref name="archived"/> marks <paramref name="ids"/> for what has no
    /// place to move from (sent mail stays in Sent; its conversation leaves the
    /// list). Every message touched gets its state anew.
    /// </summary>
    public Task ApplyArchiveAsync(ContextRef ctx, IReadOnlyList<string> ids, bool archived, IReadOnlyList<MailMove> moves, CancellationToken ct = default) =>
        _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            foreach (var m in moves)
            {
                if (!m.SourceKept)
                    await db.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM mail_locations WHERE message_id = @MessageId AND account_id = @AccountId AND role = @FromRole AND uid = @FromUid",
                        new { m.MessageId, m.AccountId, m.FromRole, FromUid = (long)m.FromUid }, tx, cancellationToken: token));
                if (m.ToUid is { } uid)
                    await db.ExecuteAsync(new CommandDefinition(
                        "INSERT OR REPLACE INTO mail_locations (message_id, account_id, role, uid) VALUES (@MessageId, @AccountId, @ToRole, @uid)",
                        new { m.MessageId, m.AccountId, m.ToRole, uid = (long)uid }, tx, cancellationToken: token));
            }
            if (ids.Count > 0)
                await db.ExecuteAsync(new CommandDefinition(
                    "UPDATE mail_messages SET archived = @archived, updated_at = @now WHERE id IN @ids",
                    new { archived, now = Iso(DateTime.UtcNow), ids = ids.ToArray() }, tx, cancellationToken: token));
            await RestateAsync(db, tx, ids.Concat(moves.Select(m => m.MessageId)).Distinct().ToList(), token);
        }, ct);

    /// <summary>Sets seen and / or flagged on these messages here (null leaves
    /// it) and returns where the changed ones lie, for the write-back.</summary>
    public Task<IReadOnlyList<MailLocationRef>> SetFlagsAsync(ContextRef ctx, IReadOnlyList<string> ids, bool? seen, bool? flagged, CancellationToken ct = default) =>
        _db.WithContextTransactionAsync<IReadOnlyList<MailLocationRef>>(ctx, async (db, tx, token) =>
        {
            if (ids.Count == 0 || (seen is null && flagged is null)) return [];
            var changed = (await db.QueryAsync<string>(new CommandDefinition(@"
                SELECT id FROM mail_messages WHERE id IN @ids
                  AND ((@seen IS NOT NULL AND seen <> @seen) OR (@flagged IS NOT NULL AND flagged <> @flagged))",
                new { ids = ids.ToArray(), seen, flagged }, tx, cancellationToken: token))).ToArray();
            if (changed.Length == 0) return [];
            await db.ExecuteAsync(new CommandDefinition(@"
                UPDATE mail_messages SET seen = COALESCE(@seen, seen), flagged = COALESCE(@flagged, flagged), updated_at = @now
                WHERE id IN @changed",
                new { seen, flagged, now = Iso(DateTime.UtcNow), changed }, tx, cancellationToken: token));
            return (await db.QueryAsync<(string AccountId, string Role, long Uid)>(new CommandDefinition(
                "SELECT account_id, role, uid FROM mail_locations WHERE message_id IN @changed",
                new { changed }, tx, cancellationToken: token)))
                .Select(l => new MailLocationRef(l.AccountId, l.Role, (uint)l.Uid)).ToList();
        }, ct);

    public async Task<IReadOnlyList<MailLocationRef>> LocationsOfAsync(ContextRef ctx, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        using var db = _db.CreateContextConnection(ctx);
        return (await db.QueryAsync<(string AccountId, string Role, long Uid)>(new CommandDefinition(
            "SELECT account_id, role, uid FROM mail_locations WHERE message_id IN @ids",
            new { ids = ids.ToArray() }, cancellationToken: ct)))
            .Select(l => new MailLocationRef(l.AccountId, l.Role, (uint)l.Uid)).ToList();
    }

    /// <summary>
    /// Deletes messages here (decision 8): each goes to the trash, restorable.
    /// With <paramref name="tombstone"/> — deleted only in Fishbowl, the server
    /// keeps it — its key and places are marked so the sync never brings it
    /// back (a restore lifts the mark). Deleted everywhere, the server copy is
    /// already in its Trash and nothing is marked. Returns how many went.
    /// </summary>
    public Task<int> DeleteMessagesAsync(ContextRef ctx, IReadOnlyList<string> ids, bool tombstone, string? deletedBy, CancellationToken ct = default) =>
        ids.Count == 0 ? Task.FromResult(0) : _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var rows = (await db.QueryAsync<(string Id, string AccountId, string MessageKey)>(new CommandDefinition(
                "SELECT id, account_id, message_key FROM mail_messages WHERE id IN @ids",
                new { ids = ids.ToArray() }, tx, cancellationToken: token))).ToList();
            var now = Iso(DateTime.UtcNow);
            foreach (var r in rows)
            {
                if (tombstone)
                {
                    await db.ExecuteAsync(new CommandDefinition(@"
                        INSERT OR IGNORE INTO mail_tombstones (account_id, message_key, role, uid, created_at)
                        VALUES (@AccountId, @MessageKey, '', 0, @now);
                        INSERT OR IGNORE INTO mail_tombstones (account_id, message_key, role, uid, created_at)
                        SELECT account_id, @MessageKey, role, uid, @now FROM mail_locations WHERE message_id = @Id;",
                        new { r.Id, r.AccountId, r.MessageKey, now }, tx, cancellationToken: token));
                }
                await TrashSnapshots.TakeAsync(db, tx, ctx, TrashKinds.Mail, r.Id, deletedBy, token);
            }
            await RemoveRowsAsync(db, tx, rows.Select(r => r.Id).ToList(), token);
            return rows.Count;
        }, ct);

    /// <summary>An account's mail deleted only in Fishbowl: the message keys,
    /// and per role the UIDs not to fetch again.</summary>
    public sealed record Tombstones(HashSet<string> Keys, Dictionary<string, HashSet<uint>> Uids);

    public async Task<Tombstones> TombstonesAsync(ContextRef ctx, string accountId, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<(string MessageKey, string Role, long Uid)>(new CommandDefinition(
            "SELECT message_key, role, uid FROM mail_tombstones WHERE account_id = @accountId",
            new { accountId }, cancellationToken: ct));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var uids = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            keys.Add(r.MessageKey);
            if (r.Role.Length > 0) (uids.TryGetValue(r.Role, out var set) ? set : uids[r.Role] = new()).Add((uint)r.Uid);
        }
        return new Tombstones(keys, uids);
    }

    /// <summary>A deleted message found again at another place (Gmail's
    /// labels, a new UIDVALIDITY): that UID isn't fetched again either.</summary>
    public async Task TombstonePlaceAsync(ContextRef ctx, string accountId, string key, string role, uint uid, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT OR IGNORE INTO mail_tombstones (account_id, message_key, role, uid, created_at)
            VALUES (@accountId, @key, @role, @uid, @now)",
            new { accountId, key, role, uid = (long)uid, now = Iso(DateTime.UtcNow) }, cancellationToken: ct));
    }

    /// <summary>A folder's UIDs were renumbered (new UIDVALIDITY): its marked
    /// places mean nothing now; the keys stay.</summary>
    public async Task DropTombstonePlacesAsync(ContextRef ctx, string accountId, string role, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mail_tombstones WHERE account_id = @accountId AND role = @role",
            new { accountId, role }, cancellationToken: ct));
    }

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
        // Deleted only in Fishbowl: back here, the sync finds its places again.
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mail_tombstones WHERE account_id = @AccountId AND message_key = @MessageKey",
            new { m.AccountId, m.MessageKey }, tx, cancellationToken: ct));
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
