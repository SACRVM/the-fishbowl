using System.Text.RegularExpressions;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Search;
using Fishbowl.Data.Search;
using Microsoft.Extensions.Logging;

namespace Fishbowl.Data.Mail;

/// <summary>
/// Mail found by meaning (mail spec, decision 15; user/space schema v27): a
/// vector per message in <c>vec_mail</c>, made in the background
/// (<see cref="EmbedPendingAsync"/>, newest first), and a search that blends
/// it with the full text the way notes do — 70 % meaning, 30 % words — puts
/// mail with the query's words before mail found by meaning alone, and ranks
/// a conversation by its best message.
/// </summary>
public partial class MailRepository
{
    // How many messages each side brings to the blend.
    private const int MinPool = 100;
    private const int MaxPool = 500;

    // A message found by meaning alone counts only this close: e5 puts
    // nearly every pair of texts close together, so the nearest few are
    // never "nothing" — the floor keeps a query nothing answers from
    // ranking the least unrelated mail first. Measured (L2 between unit
    // vectors): a German query and a mail about it sit at 0.50–0.55,
    // unrelated mail from 0.58 on; 0.57 is a cosine of about 0.84. A mail
    // with the query's words is in whatever its distance.
    internal const double MeaningFloor = 0.57;

    // What the model reads of a mail: its start says what it is about, and
    // a run's cost grows with its length (about 150 tokens with the subject).
    public const int EmbeddedChars = 600;

    public async Task<MailSearchResult> SearchThreadsAsync(ContextRef ctx, MailListQuery q, CancellationToken ct = default)
    {
        var text = (q.Text ?? "").Trim();
        if (text.Length > HybridSearchService.MaxQueryLength) text = text[..HybridSearchService.MaxQueryLength];
        if (text.Length == 0) return new MailSearchResult([], false);
        var limit = Math.Clamp(q.Limit, 1, 100);
        var pool = Math.Clamp(limit * 10, MinPool, MaxPool);

        var fts = new List<(string Id, double Bm25)>();
        var vec = new List<(string Id, double Distance)>();
        var degraded = _embeddings is null;
        Dictionary<string, string> threadOf;
        using (var db = _db.CreateContextConnection(ctx))
        {
            if (FtsQuery(text) is { } match)
                // The subject weighs most, then the people, then the text.
                fts = (await db.QueryAsync<(string, double)>(new CommandDefinition(@"
                    SELECT m.id, bm25(mail_fts, 3.0, 2.0, 1.0) AS rank
                    FROM mail_fts JOIN mail_messages m ON m.rowid = mail_fts.rowid
                    WHERE mail_fts MATCH @match
                    ORDER BY rank LIMIT @pool", new { match, pool }, cancellationToken: ct))).ToList();
            if (_embeddings is not null)
            {
                try
                {
                    var blob = Blob(await _embeddings.EmbedQueryAsync(text, ct));
                    vec = (await db.QueryAsync<(string, double)>(new CommandDefinition(
                        "SELECT id, distance FROM vec_mail WHERE embedding MATCH @blob AND k = @pool ORDER BY distance",
                        new { blob, pool }, cancellationToken: ct))).ToList();
                }
                catch (EmbeddingUnavailableException) { degraded = true; }
            }
            var words = fts.Select(f => f.Id).ToHashSet();
            vec = vec.Where(v => v.Distance <= MeaningFloor || words.Contains(v.Id)).ToList();
            var ids = words.Concat(vec.Select(v => v.Id)).Distinct().ToArray();
            threadOf = ids.Length == 0 ? new() : (await db.QueryAsync<(string Id, string ThreadId)>(new CommandDefinition(
                "SELECT id, thread_id FROM mail_messages WHERE id IN @ids", new { ids }, cancellationToken: ct)))
                .ToDictionary(r => r.Id, r => r.ThreadId);
        }

        // A conversation is as good as its best message — and a message with
        // every word of the query comes before one found by meaning alone
        // (MergeScores).
        var ranked = new List<string>();
        var seen = new HashSet<string>();
        foreach (var (id, _, _) in HybridSearchService.MergeScores(vec, fts, degraded))
            if (threadOf.TryGetValue(id, out var thread) && seen.Add(thread)) ranked.Add(thread);

        // The filters (tab, account, tags, unread) through the list's own
        // query, a page of candidates at a time, until enough are left.
        var threads = new List<MailThread>();
        foreach (var page in ranked.Chunk(200))
        {
            var found = (await ListThreadsAsync(ctx, new MailListQuery
            {
                AccountId = q.AccountId,
                Tag = q.Tag,
                Tags = q.Tags,
                UnreadOnly = q.UnreadOnly,
                Archived = q.Archived,
                AnyState = q.AnyState,
                Address = q.Address,
                ThreadIds = page,
                Limit = page.Length,
            }, ct)).ToDictionary(t => t.ThreadId);
            threads.AddRange(page.Where(found.ContainsKey).Select(id => found[id]));
            if (threads.Count >= limit) break;
        }
        return new MailSearchResult(threads.Take(limit).ToList(), degraded);
    }

    /// <summary>
    /// Gives up to <paramref name="max"/> messages without a vector one,
    /// newest first; how many it did (0: none left, or no model). The model
    /// runs with no transaction open; a message deleted meanwhile is skipped.
    /// Throws <see cref="EmbeddingUnavailableException"/> while the model isn't ready.
    /// </summary>
    public async Task<int> EmbedPendingAsync(ContextRef ctx, int max, CancellationToken ct = default)
    {
        if (_embeddings is null) return 0;
        List<(string Id, string? Subject, string? FromName, string? Body)> rows;
        using (var db = _db.CreateContextConnection(ctx))
            rows = (await db.QueryAsync<(string, string?, string?, string?)>(new CommandDefinition(@"
                SELECT id, subject, from_name, substr(body_text, 1, 4000)
                FROM mail_messages WHERE embedded = 0
                ORDER BY sent_at DESC LIMIT @max", new { max }, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return 0;

        var texts = rows.Select(r => EmbeddingText(r.Subject, r.FromName, r.Body)).ToList();
        var vectors = new float[]?[rows.Count];
        try
        {
            var all = await _embeddings.EmbedManyAsync(texts, ct);
            for (var i = 0; i < all.Count; i++) vectors[i] = all[i];
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or EmbeddingUnavailableException))
        {
            // One by one, so a message the model can't take doesn't hold up
            // the rest: it is marked done without a vector (2) and found by
            // its words only — until another model tries it again.
            _logger.LogWarning(ex, "Embedding a batch of mail failed; trying its messages one by one");
            for (var i = 0; i < texts.Count; i++)
            {
                try { vectors[i] = await _embeddings.EmbedAsync(texts[i], ct); }
                catch (Exception one) when (one is not (OperationCanceledException or EmbeddingUnavailableException))
                {
                    _logger.LogWarning(one, "Embedding a mail message failed; it stays findable by its words");
                }
            }
        }
        await _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var id = rows[i].Id;
                if (await db.ExecuteAsync(new CommandDefinition(
                        "UPDATE mail_messages SET embedded = @state WHERE id = @id AND embedded = 0",
                        new { id, state = vectors[i] is null ? 2 : 1 }, tx, cancellationToken: token)) == 0 || vectors[i] is not { } vector)
                    continue;
                await db.ExecuteAsync(new CommandDefinition("DELETE FROM vec_mail WHERE id = @id", new { id }, tx, cancellationToken: token));
                await db.ExecuteAsync(new CommandDefinition("INSERT INTO vec_mail (id, embedding) VALUES (@id, @blob)",
                    new { id, blob = Blob(vector) }, tx, cancellationToken: token));
            }
        }, ct);
        return rows.Count;
    }

    /// <summary>Every vector goes (another model made them); the backfill
    /// makes them again.</summary>
    public Task ResetVectorsAsync(ContextRef ctx, CancellationToken ct = default) =>
        _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            await db.ExecuteAsync(new CommandDefinition("DELETE FROM vec_mail", transaction: tx, cancellationToken: token));
            await db.ExecuteAsync(new CommandDefinition("UPDATE mail_messages SET embedded = 0 WHERE embedded <> 0", transaction: tx, cancellationToken: token));
        }, ct);

    /// <summary>What the model reads of a mail: its subject, who sent it and
    /// how it starts — without quoted replies (lines with "&gt;", or all
    /// from an "On … wrote:" / "-----Original" line on) and links.</summary>
    public static string EmbeddingText(string? subject, string? from, string? body)
    {
        var kept = new List<string>();
        foreach (var raw in (body ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (QuoteStart().IsMatch(line)) break;
            if (line.Length == 0 || line.StartsWith('>')) continue;
            kept.Add(line);
        }
        var text = Spaces().Replace(Links().Replace(string.Join(" ", kept), " "), " ").Trim();
        if (text.Length > EmbeddedChars) text = text[..EmbeddedChars];
        return string.Join("\n", new[] { subject, from, text }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
    }

    private static byte[] Blob(float[] vector)
    {
        var blob = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);
        return blob;
    }

    [GeneratedRegex(@"^(-{3,}\s*(Original|Ursprüngliche|Forwarded|Weitergeleitete)|(On|Am) .{4,200}(wrote|schrieb[^:]*):$)", RegexOptions.IgnoreCase)]
    private static partial Regex QuoteStart();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Links();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
