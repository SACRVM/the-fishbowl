using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Util;

namespace Fishbowl.Data.Mail;

/// <summary>
/// "Apply to the mail already here" (mail spec, decision 11): one rule over
/// what is stored — on or off, it was asked for. Tags are added to the
/// messages it meets; read is written here and to the server (best effort,
/// like a person's); archive takes their conversations that are in the inbox
/// to the archive, on the server first, a few hundred messages at a time.
/// </summary>
public sealed class MailRuleRunner
{
    private const int Batch = 500;

    private readonly MailRepository _mail;
    private readonly MailServer _server;

    public MailRuleRunner(MailRepository mail, MailServer server)
    {
        _mail = mail;
        _server = server;
    }

    /// <summary>Conversations the rule meets; how many of them were archived;
    /// ArchiveFolderMissing when an account had nowhere to archive to.</summary>
    public sealed record Result(int Conversations, int Archived, bool ArchiveFolderMissing);

    public async Task<Result> ApplyAsync(ContextRef ctx, MailRule rule, CancellationToken ct)
    {
        var all = await _mail.RuleCandidatesAsync(ctx, ct);
        var hits = all.Where(m => MailRules.Matches(rule, m)).ToList();
        var threads = hits.Select(m => m.ThreadId).ToHashSet(StringComparer.Ordinal);

        if (rule.AddTags.Count > 0)
            await _mail.AddTagsAsync(ctx, hits.Select(m => m.Id).ToList(), rule.AddTags, ct);

        if (rule.MarkRead)
            foreach (var chunk in hits.Where(m => m.Direction == MailDirections.In && !m.Seen).Select(m => m.Id).Chunk(Batch))
            {
                var places = await _mail.SetFlagsAsync(ctx, chunk, true, null, ct);
                if (places.Count > 0) await _server.MarkAsync(ctx, places, true, null, ct);
            }

        var archived = 0;
        if (rule.Archive)
        {
            var inbox = all.Where(m => m.State == MailStates.Inbox && threads.Contains(m.ThreadId))
                .Select(m => m.ThreadId).ToHashSet(StringComparer.Ordinal);
            var batch = new List<string>();
            var batchThreads = 0;
            foreach (var thread in all.Where(m => inbox.Contains(m.ThreadId)).GroupBy(m => m.ThreadId))
            {
                batch.AddRange(thread.Select(m => m.Id));
                batchThreads++;
                if (batch.Count < Batch) continue;
                if (!await _server.ArchiveAsync(ctx, batch, true, ct)) return new Result(threads.Count, archived, true);
                archived += batchThreads;
                batch.Clear();
                batchThreads = 0;
            }
            if (batch.Count > 0)
            {
                if (!await _server.ArchiveAsync(ctx, batch, true, ct)) return new Result(threads.Count, archived, true);
                archived += batchThreads;
            }
        }
        return new Result(threads.Count, archived, false);
    }
}
