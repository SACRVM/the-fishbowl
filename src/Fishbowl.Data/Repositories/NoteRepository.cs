using System.Data;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Search;
using Fishbowl.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class NoteRepository : INoteRepository
{
    private readonly DatabaseFactory _dbFactory;
    private readonly ITagRepository _tagRepository;
    private readonly IEmbeddingService? _embeddings;
    private readonly ILogger<NoteRepository> _logger;

    // IEmbeddingService is nullable so tests that don't exercise the embedding
    // path (and the v1 cookie-auth call sites from before Task 5.3 landed)
    // can construct NoteRepository without a model on disk. When null, the
    // vec_notes writes are skipped — same degraded path as when the service
    // is wired but the model hasn't finished downloading.
    public NoteRepository(
        DatabaseFactory dbFactory,
        ITagRepository tagRepository,
        IEmbeddingService? embeddings = null,
        ILogger<NoteRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _tagRepository = tagRepository;
        _embeddings = embeddings;
        _logger = logger ?? NullLogger<NoteRepository>.Instance;
    }

    // ────────── ContextRef overloads (canonical implementation) ──────────

    public async Task<Note?> GetByIdAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.QuerySingleOrDefaultAsync<Note>(
            new CommandDefinition("SELECT * FROM notes WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public Task<IEnumerable<Note>> GetAllAsync(ContextRef ctx, CancellationToken ct = default)
        => GetAllAsync(ctx, tags: null, match: "any", ct);

    public async Task<IEnumerable<Note>> GetAllAsync(
        ContextRef ctx,
        IReadOnlyCollection<string>? tags,
        string match,
        CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);

        if (tags is null || tags.Count == 0)
        {
            return await db.QueryAsync<Note>(new CommandDefinition(
                "SELECT * FROM notes ORDER BY updated_at DESC", cancellationToken: ct));
        }

        var sql = match == "all"
            ? @"SELECT * FROM notes
                WHERE (SELECT COUNT(DISTINCT je.value) FROM json_each(notes.tags) je
                       WHERE je.value IN @tags) = @count
                ORDER BY updated_at DESC"
            : @"SELECT * FROM notes
                WHERE EXISTS (SELECT 1 FROM json_each(notes.tags) je WHERE je.value IN @tags)
                ORDER BY updated_at DESC";

        return await db.QueryAsync<Note>(new CommandDefinition(
            sql, new { tags, count = tags.Count }, cancellationToken: ct));
    }

    public Task<string> CreateAsync(ContextRef ctx, string actorUserId, Note note, CancellationToken ct = default)
        => CreateAsync(ctx, actorUserId, note, NoteSource.Human, ct);

    public async Task<string> CreateAsync(ContextRef ctx, string actorUserId, Note note, NoteSource source, CancellationToken ct = default)
    {
        ApplySourceTags(note, source);
        EnforceLimits(note);

        if (string.IsNullOrEmpty(note.Id))
            note.Id = Ulid.NewUlid().ToString();

        _logger.LogDebug("Creating note {Id} in context {CtxType}:{CtxId}", note.Id, ctx.Type, ctx.Id);

        note.CreatedAt = DateTime.UtcNow;
        note.UpdatedAt = note.CreatedAt;
        note.CreatedBy = actorUserId;

        await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            note.Tags = (await _tagRepository.EnsureExistsAsync(db, tx, note.Tags, token)).ToList();

            await db.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO notes (id, title, content, content_secret, type, tags, created_by, created_at, updated_at, pinned, archived)
                VALUES (@Id, @Title, @Content, @ContentSecret, @Type, @Tags, @CreatedBy, @CreatedAt, @UpdatedAt, @Pinned, @Archived)",
                new
                {
                    note.Id,
                    note.Title,
                    note.Content,
                    note.ContentSecret,
                    note.Type,
                    note.Tags,
                    note.CreatedBy,
                    CreatedAt = note.CreatedAt.ToString("o"),
                    UpdatedAt = note.UpdatedAt.ToString("o"),
                    Pinned = note.Pinned ? 1 : 0,
                    Archived = note.Archived ? 1 : 0
                }, transaction: tx, cancellationToken: token));

            await db.ExecuteAsync(new CommandDefinition(
                "INSERT INTO notes_fts (rowid, title, content, tags) VALUES ((SELECT rowid FROM notes WHERE id = @Id), @Title, @Content, @TagsFlat)",
                new
                {
                    note.Id,
                    note.Title,
                    note.Content,
                    TagsFlat = string.Join(' ', note.Tags)
                }, transaction: tx, cancellationToken: token));

            await UpsertEmbeddingAsync(db, tx, note, token);
        }, ct);

        return note.Id;
    }

    public Task<bool> UpdateAsync(ContextRef ctx, Note note, CancellationToken ct = default)
        => UpdateAsync(ctx, note, NoteSource.Human, ct);

    public async Task<bool> UpdateAsync(ContextRef ctx, Note note, NoteSource source, CancellationToken ct = default)
    {
        ApplySourceTags(note, source);
        EnforceLimits(note);
        note.UpdatedAt = DateTime.UtcNow;

        return await _dbFactory.WithContextTransactionAsync<bool>(ctx, async (db, tx, token) =>
        {
            note.Tags = (await _tagRepository.EnsureExistsAsync(db, tx, note.Tags, token)).ToList();

            var affected = await db.ExecuteAsync(new CommandDefinition(@"
                UPDATE notes
                SET title = @Title, content = @Content, content_secret = @ContentSecret,
                    type = @Type, tags = @Tags, updated_at = @UpdatedAt,
                    pinned = @Pinned, archived = @Archived
                WHERE id = @Id",
                new
                {
                    note.Title,
                    note.Content,
                    note.ContentSecret,
                    note.Type,
                    note.Tags,
                    UpdatedAt = note.UpdatedAt.ToString("o"),
                    Pinned = note.Pinned ? 1 : 0,
                    Archived = note.Archived ? 1 : 0,
                    note.Id
                }, transaction: tx, cancellationToken: token));

            if (affected > 0)
            {
                await db.ExecuteAsync(new CommandDefinition(
                    "UPDATE notes_fts SET title = @Title, content = @Content, tags = @TagsFlat WHERE rowid = (SELECT rowid FROM notes WHERE id = @Id)",
                    new
                    {
                        note.Id,
                        note.Title,
                        note.Content,
                        TagsFlat = string.Join(' ', note.Tags)
                    }, transaction: tx, cancellationToken: token));

                await UpsertEmbeddingAsync(db, tx, note, token);
            }
            else
            {
                _logger.LogWarning("Update of note {Id} in {CtxType}:{CtxId} matched no rows",
                    note.Id, ctx.Type, ctx.Id);
            }

            return affected > 0;
        }, ct);
    }

    public async Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        return await _dbFactory.WithContextTransactionAsync<bool>(ctx, async (db, tx, token) =>
        {
            // Delete from the two index tables before the authoritative `notes`
            // row disappears — same pattern notes_fts already uses. `vec_notes`
            // has no FK, but a stale row there would surface as a phantom hit
            // in hybrid search (pointing at a notes.id that no longer exists).
            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM vec_notes WHERE id = @id",
                new { id }, transaction: tx, cancellationToken: token));

            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM notes_fts WHERE rowid = (SELECT rowid FROM notes WHERE id = @id)",
                new { id }, transaction: tx, cancellationToken: token));

            var affected = await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM notes WHERE id = @id",
                new { id }, transaction: tx, cancellationToken: token));

            return affected > 0;
        }, ct);
    }

    // ────────── Bulk re-embed ──────────
    //
    // Runs each note's embedding sequentially in a single transaction. On
    // SQLite this is fine at personal-memory scale; for very large vaults
    // we'd want chunked commits to keep the WAL bounded. Keep an eye on
    // durations in logs.
    public async Task<ReEmbedResult> ReEmbedAllAsync(ContextRef ctx, CancellationToken ct = default)
    {
        if (_embeddings is null)
        {
            _logger.LogInformation("Re-embed skipped — no embedding service configured");
            return new ReEmbedResult(Processed: 0, Failed: 0);
        }

        var processed = 0;
        var failed = 0;

        await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var rows = (await db.QueryAsync<Note>(new CommandDefinition(
                "SELECT * FROM notes ORDER BY updated_at DESC",
                transaction: tx, cancellationToken: token))).ToList();

            foreach (var note in rows)
            {
                token.ThrowIfCancellationRequested();
                var before = processed + failed;
                try
                {
                    var text = BuildEmbeddingText(note);
                    var vec = await _embeddings.EmbedAsync(text, token);
                    var blob = new byte[vec.Length * sizeof(float)];
                    Buffer.BlockCopy(vec, 0, blob, 0, blob.Length);

                    await db.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM vec_notes WHERE id = @id",
                        new { id = note.Id }, transaction: tx, cancellationToken: token));
                    await db.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO vec_notes(id, embedding) VALUES (@id, @blob)",
                        new { id = note.Id, blob }, transaction: tx, cancellationToken: token));
                    processed++;
                }
                catch (EmbeddingUnavailableException)
                {
                    // Model went away mid-run. Leave the existing vector in
                    // place (or none) and move on; the user can retry.
                    _logger.LogDebug("Re-embed hit EmbeddingUnavailable on note {Id}; leaving existing row", note.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Re-embed failed for note {Id}", note.Id);
                    failed++;
                }
            }
        }, ct);

        _logger.LogInformation("Re-embed finished in {CtxType}:{CtxId} — processed={Processed} failed={Failed}",
            ctx.Type, ctx.Id, processed, failed);

        return new ReEmbedResult(processed, failed);
    }

    // ────────── Legacy (personal-context) aliases ──────────

    public Task<Note?> GetByIdAsync(string userId, string id, CancellationToken ct = default)
        => GetByIdAsync(ContextRef.User(userId), id, ct);

    public Task<IEnumerable<Note>> GetAllAsync(string userId, CancellationToken ct = default)
        => GetAllAsync(ContextRef.User(userId), ct);

    public Task<IEnumerable<Note>> GetAllAsync(
        string userId,
        IReadOnlyCollection<string>? tags,
        string match,
        CancellationToken ct = default)
        => GetAllAsync(ContextRef.User(userId), tags, match, ct);

    public Task<string> CreateAsync(string userId, Note note, CancellationToken ct = default)
        => CreateAsync(ContextRef.User(userId), userId, note, ct);

    public Task<bool> UpdateAsync(string userId, Note note, CancellationToken ct = default)
        => UpdateAsync(ContextRef.User(userId), note, ct);

    public Task<bool> DeleteAsync(string userId, string id, CancellationToken ct = default)
        => DeleteAsync(ContextRef.User(userId), id, ct);

    // ────────── Embedding write ──────────
    //
    // Runs inside the same transaction as the notes + notes_fts writes so the
    // three tables stay consistent. On failure (model not downloaded yet,
    // ONNX error, etc.) we log and move on — the row lands in vec_notes on
    // the next UpdateAsync or the explicit re-index action.
    //
    // Text fed to the model is: title + stripped content + tag names joined
    // with spaces. The strip is critical: secret blocks must not shape the
    // vector — a semantic search over "api key" could otherwise surface the
    // redacted note because its secret happened to cluster in embedding
    // space, violating the same invariant SecretStripper enforces on the
    // MCP wire.
    private async Task UpsertEmbeddingAsync(IDbConnection db, IDbTransaction tx, Note note, CancellationToken ct)
    {
        if (_embeddings is null) return;

        try
        {
            var text = BuildEmbeddingText(note);
            var vec = await _embeddings.EmbedAsync(text, ct);

            // sqlite-vec vec0 FLOAT[N] stores 4*N bytes little-endian. .NET's
            // float memory layout is IEEE 754 little-endian on every target
            // platform we support; Buffer.BlockCopy is the no-alloc way to
            // get there.
            var blob = new byte[vec.Length * sizeof(float)];
            Buffer.BlockCopy(vec, 0, blob, 0, blob.Length);

            // sqlite-vec's vec0 virtual table doesn't honour `INSERT OR REPLACE`
            // as a true replace — the old row sticks around and the new insert
            // errors on the PK. Do it explicitly: delete first, then insert.
            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM vec_notes WHERE id = @id",
                new { id = note.Id }, transaction: tx, cancellationToken: ct));
            await db.ExecuteAsync(new CommandDefinition(
                "INSERT INTO vec_notes(id, embedding) VALUES (@id, @blob)",
                new { id = note.Id, blob }, transaction: tx, cancellationToken: ct));
        }
        catch (EmbeddingUnavailableException)
        {
            _logger.LogDebug("Embedding service not ready; note {Id} will be indexed on next re-index", note.Id);
        }
        catch (Exception ex)
        {
            // Don't fail the write over an indexing glitch. The user's note
            // has to land; the vector can wait for the next opportunity.
            _logger.LogWarning(ex, "Embedding failed for note {Id}; skipping vec_notes update", note.Id);
        }
    }

    private static string BuildEmbeddingText(Note note)
    {
        var stripped = SecretStripper.StripNote(note).Content ?? string.Empty;
        var tags = note.Tags is null ? string.Empty : string.Join(' ', note.Tags);
        return $"{note.Title} {stripped} {tags}".Trim();
    }

    // ────────── Size-limit gate ──────────
    // Throws NoteValidationException when a field is past the hard cap.
    // The API edge catches this and converts to 413; MCP tools see the
    // same exception type and surface it as a tool error to the caller.
    private static void EnforceLimits(Note note)
    {
        var error = NoteLimits.Validate(note);
        if (error is not null) throw new NoteValidationException(error);
    }

    // ────────── Source-tag massage ──────────
    // Runs inside CreateAsync/UpdateAsync before the tag-repo's EnsureExists.
    // Mcp writes gain `source:mcp` + `review:pending` so the human notices
    // them in the review inbox. Human writes strip `review:pending` —
    // editing a pending note is implicit approval.
    private static void ApplySourceTags(Note note, NoteSource source)
    {
        note.Tags ??= new List<string>();
        var tags = new HashSet<string>(note.Tags, StringComparer.Ordinal);

        if (source == NoteSource.Mcp)
        {
            tags.Add("source:mcp");
            tags.Add("review:pending");
        }
        else
        {
            tags.Remove("review:pending");
        }

        note.Tags = tags.ToList();
    }
}
