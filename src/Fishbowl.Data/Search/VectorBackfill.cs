using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Search;
using Fishbowl.Data.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Search;

/// <summary>
/// Keeps one workspace's vectors current, in the background (the scheduler's
/// <c>EmbeddingBackfillService</c> calls it per workspace and pass):
/// <list type="bullet">
/// <item>Vectors from another model — or from before any was recorded
/// (user/space schema v27; every older one is all-MiniLM-L6-v2's) — don't
/// compare with the model's queries: notes and searchable table rows are
/// embedded again, mail's vectors go, and the model is recorded.</item>
/// <item>Then mail without a vector gets one, newest first, a batch at a
/// time — mail is many times the volume of notes, so it is never embedded
/// while it is stored (the sync stays as fast as it was).</item>
/// </list>
/// </summary>
public sealed class VectorBackfill
{
    public const string ModelKey = "embedding_model";

    // One model run: big enough to fill the CPU's lanes, small enough that a
    // pass gives way soon.
    public const int Batch = 16;

    private readonly DatabaseFactory _db;
    private readonly IEmbeddingService _embeddings;
    private readonly INoteRepository _notes;
    private readonly ITableRepository _tables;
    private readonly MailRepository _mail;
    private readonly ILogger<VectorBackfill> _logger;

    public VectorBackfill(DatabaseFactory db, IEmbeddingService embeddings, INoteRepository notes, ITableRepository tables,
        MailRepository mail, ILogger<VectorBackfill>? logger = null)
    {
        _db = db;
        _embeddings = embeddings;
        _notes = notes;
        _tables = tables;
        _mail = mail;
        _logger = logger ?? NullLogger<VectorBackfill>.Instance;
    }

    /// <summary>Brings the workspace to the current model and embeds up to
    /// <paramref name="maxMail"/> messages; how many it embedded (0: none
    /// left, or the model isn't ready yet).</summary>
    public async Task<int> RunAsync(ContextRef ctx, int maxMail, CancellationToken ct = default)
    {
        // While the model downloads there is nothing to do.
        try { await _embeddings.EmbedQueryAsync("ready", ct); }
        catch (EmbeddingUnavailableException) { return 0; }

        if (await StoredModelAsync(ctx, ct) != _embeddings.Model)
        {
            _logger.LogInformation("Embedding {CtxType}:{CtxId} again with {Model}", ctx.Type, ctx.Id, _embeddings.Model);
            await _mail.ResetVectorsAsync(ctx, ct);
            await _notes.ReEmbedAllAsync(ctx, ct);
            if (ctx.Type == ContextType.Space)
                foreach (var table in await _tables.ListAsync(ctx, ct))
                    if (table.Columns.Any(c => c.Searchable))
                        await _tables.ReindexAsync(ctx, table.Name, null, ct);
            await _db.WithContextTransactionAsync(ctx, (db, tx, token) => db.ExecuteAsync(new CommandDefinition(
                "INSERT INTO search_meta (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                new { key = ModelKey, value = _embeddings.Model }, tx, cancellationToken: token)), ct);
        }

        var done = 0;
        while (done < maxMail)
        {
            var n = await _mail.EmbedPendingAsync(ctx, Math.Min(Batch, maxMail - done), ct);
            if (n == 0) break;
            done += n;
        }
        return done;
    }

    private async Task<string?> StoredModelAsync(ContextRef ctx, CancellationToken ct)
    {
        using var db = _db.CreateContextConnection(ctx);
        return await db.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM search_meta WHERE key = @key", new { key = ModelKey }, cancellationToken: ct));
    }
}
