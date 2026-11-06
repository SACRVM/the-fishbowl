using System.Data;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Search;
using Fishbowl.Core.Util;
using Microsoft.Data.Sqlite;
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

    public Task<IEnumerable<Note>> GetAllAsync(
        ContextRef ctx,
        IReadOnlyCollection<string>? tags,
        string match,
        CancellationToken ct = default)
        => GetAllAsync(ctx, tags, match, limit: null, offset: 0, ct);

    public async Task<IEnumerable<Note>> GetAllAsync(
        ContextRef ctx,
        IReadOnlyCollection<string>? tags,
        string match,
        int? limit,
        int offset,
        CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);

        // Clamp + normalise paging. Negative offset and oversized limit
        // become defaults — saves clients from having to validate these
        // and matches the lenient pattern the rest of the API uses.
        var clampedLimit = limit is { } l ? Math.Clamp(l, 1, 500) : (int?)null;
        var clampedOffset = offset < 0 ? 0 : offset;
        var pagingClause = clampedLimit is { } lim
            ? $" LIMIT {lim} OFFSET {clampedOffset}"
            : string.Empty;

        if (tags is null || tags.Count == 0)
        {
            return await db.QueryAsync<Note>(new CommandDefinition(
                "SELECT * FROM notes ORDER BY updated_at DESC" + pagingClause,
                cancellationToken: ct));
        }

        var sql = (match == "all"
            ? @"SELECT * FROM notes
                WHERE (SELECT COUNT(DISTINCT je.value) FROM json_each(notes.tags) je
                       WHERE je.value IN @tags) = @count
                ORDER BY updated_at DESC"
            : @"SELECT * FROM notes
                WHERE EXISTS (SELECT 1 FROM json_each(notes.tags) je WHERE je.value IN @tags)
                ORDER BY updated_at DESC") + pagingClause;

        return await db.QueryAsync<Note>(new CommandDefinition(
            sql, new { tags, count = tags.Count }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<NoteChange>> ListChangesAsync(ContextRef ctx, NoteChangeQuery query, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        var where = new List<string>();
        // An array, not a List<string>: Dapper writes a List<string> as JSON
        // (JsonTagsHandler) instead of spreading it over IN (…).
        var tags = query.Tags is { Count: > 0 } t ? t.Distinct().ToArray() : null;
        if (tags is not null)
            where.Add(query.Match == "all"
                ? "(SELECT COUNT(DISTINCT je.value) FROM json_each(notes.tags) je WHERE je.value IN @tags) = @count"
                : "EXISTS (SELECT 1 FROM json_each(notes.tags) je WHERE je.value IN @tags)");
        // Stored as ISO-8601 UTC ("o"), so text order is time order.
        if (query.Since is { } since) where.Add("notes.updated_at > @since");
        if (query.AfterStamp is not null && query.AfterId is not null)
            where.Add("(notes.updated_at > @afterStamp OR (notes.updated_at = @afterStamp AND notes.id > @afterId))");
        var sql = "SELECT notes.*, notes.updated_at AS stamp FROM notes"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY notes.updated_at, notes.id LIMIT @limit";
        var rows = await db.QueryAsync<Note, NoteStamp, NoteChange>(new CommandDefinition(sql, new
        {
            tags,
            count = tags?.Length ?? 0,
            since = query.Since?.ToUniversalTime().ToString("o"),
            afterStamp = query.AfterStamp,
            afterId = query.AfterId,
            limit = Math.Clamp(query.Limit, 1, 500),
        }, cancellationToken: ct), (note, stamp) => new NoteChange(note, stamp.Stamp), splitOn: "stamp");
        return rows.ToList();
    }

    private sealed class NoteStamp { public string Stamp { get; set; } = ""; }

    public Task<string> CreateAsync(ContextRef ctx, string actorUserId, Note note, CancellationToken ct = default)
        => CreateAsync(ctx, actorUserId, note, NoteSource.Human, ct);

    public async Task<string> CreateAsync(ContextRef ctx, string actorUserId, Note note, NoteSource source, CancellationToken ct = default)
    {
        ApplySourceTags(note, source);
        EnforceLimits(ctx, note);

        if (string.IsNullOrEmpty(note.Id))
            note.Id = Ulid.NewUlid().ToString();

        _logger.LogDebug("Creating note {Id} in context {CtxType}:{CtxId}", note.Id, ctx.Type, ctx.Id);

        note.CreatedAt = DateTime.UtcNow;
        note.UpdatedAt = note.CreatedAt;
        note.CreatedBy = actorUserId;
        note.Slug = string.IsNullOrWhiteSpace(note.Slug) ? null : NoteSlug.Validate(note.Slug);

        await SlugGuard(() => _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            note.Tags = (await _tagRepository.EnsureExistsAsync(db, tx, note.Tags, token)).ToList();

            await db.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO notes (id, title, content, content_secret, type, tags, created_by, created_at, updated_at, pinned, archived, slug, locked)
                VALUES (@Id, @Title, @Content, @ContentSecret, @Type, @Tags, @CreatedBy, @CreatedAt, @UpdatedAt, @Pinned, @Archived, @Slug, @Locked)",
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
                    Archived = note.Archived ? 1 : 0,
                    note.Slug,
                    Locked = note.Locked ? 1 : 0,
                }, transaction: tx, cancellationToken: token));

            await db.ExecuteAsync(new CommandDefinition(
                "INSERT INTO notes_fts (rowid, title, content, tags) VALUES ((SELECT rowid FROM notes WHERE id = @Id), @Title, @Content, @TagsFlat)",
                new
                {
                    note.Id,
                    note.Title,
                    Content = FtsContent(note.Content),
                    TagsFlat = string.Join(' ', note.Tags)
                }, transaction: tx, cancellationToken: token));

            await UpsertEmbeddingAsync(db, tx, note, token);
        }, ct));

        return note.Id;
    }

    public Task<bool> UpdateAsync(ContextRef ctx, Note note, CancellationToken ct = default)
        => UpdateAsync(ctx, note, NoteSource.Human, ct);

    public async Task<bool> UpdateAsync(ContextRef ctx, Note note, NoteSource source, CancellationToken ct = default)
    {
        ApplySourceTags(note, source);
        EnforceLimits(ctx, note);
        note.UpdatedAt = DateTime.UtcNow;
        // null keeps the stored slug, "" clears it.
        var slug = note.Slug is null ? null : note.Slug.Trim().Length == 0 ? "" : NoteSlug.Validate(note.Slug);

        return await SlugGuard(() => _dbFactory.WithContextTransactionAsync<bool>(ctx, async (db, tx, token) =>
        {
            var stored = await db.QuerySingleOrDefaultAsync<Note>(new CommandDefinition(
                "SELECT * FROM notes WHERE id = @Id", new { note.Id }, transaction: tx, cancellationToken: token));
            // An update never sets or lifts the lock (a patch does), and a
            // locked note keeps its title and text — as stored, secret blocks
            // as their markers (the client encrypts them anew on every save,
            // so the ciphertext differs even when nothing did).
            note.Locked = stored?.Locked ?? false;
            if (stored is { Locked: true } && (note.Title != stored.Title || note.Content != stored.Content))
                throw Locked();

            // Re-add any UserRemovable=false system tag that the existing
            // row carries — a human PUT that omits source:mcp should not
            // be able to wipe the provenance marker. SystemTags.Seeds is
            // the source of truth for which tags are sticky.
            await PreserveLockedTagsAsync(db, tx, note, token);

            note.Tags = (await _tagRepository.EnsureExistsAsync(db, tx, note.Tags, token)).ToList();
            note.Slug = slug is null ? stored?.Slug : slug.Length == 0 ? null : slug;

            var affected = await db.ExecuteAsync(new CommandDefinition(@"
                UPDATE notes
                SET title = @Title, content = @Content, content_secret = @ContentSecret,
                    type = @Type, tags = @Tags, updated_at = @UpdatedAt,
                    pinned = @Pinned, archived = @Archived, slug = @Slug
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
                    note.Slug,
                    note.Id
                }, transaction: tx, cancellationToken: token));
            if (affected > 0 && stored is not null && source == NoteSource.Mcp)
                await KeepVersionAsync(db, tx, stored, note, actor: null, token);

            if (affected > 0)
            {
                await db.ExecuteAsync(new CommandDefinition(
                    "UPDATE notes_fts SET title = @Title, content = @Content, tags = @TagsFlat WHERE rowid = (SELECT rowid FROM notes WHERE id = @Id)",
                    new
                    {
                        note.Id,
                        note.Title,
                        Content = FtsContent(note.Content),
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
        }, ct));
    }

    public async Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default, string? deletedBy = null)
    {
        return await _dbFactory.WithContextTransactionAsync<bool>(ctx, async (db, tx, token) =>
        {
            await TrashSnapshots.TakeAsync(db, tx, ctx, TrashKinds.Note, id, deletedBy, token);

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

    public async Task<Note?> GetBySlugAsync(ContextRef ctx, string slug, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.QuerySingleOrDefaultAsync<Note>(new CommandDefinition(
            "SELECT * FROM notes WHERE slug = @slug COLLATE NOCASE", new { slug = slug.Trim() }, cancellationToken: ct));
    }

    public async Task<Note?> PatchAsync(ContextRef ctx, string id, NotePatch patch, NoteSource source, string? actorUserId, CancellationToken ct = default)
    {
        var slug = patch.Slug is null ? null : patch.Slug.Trim().Length == 0 ? "" : NoteSlug.Validate(patch.Slug);
        return await SlugGuard(() => _dbFactory.WithContextTransactionAsync<Note?>(ctx, async (db, tx, token) =>
        {
            var stored = await db.QuerySingleOrDefaultAsync<Note>(new CommandDefinition(
                "SELECT * FROM notes WHERE id = @id", new { id }, transaction: tx, cancellationToken: token));
            if (stored is null) return null;
            var note = SecretStripper.StripNote(stored);   // a copy to change …
            note.Content = stored.Content;                 // … of the stored text
            note.ContentSecret = stored.ContentSecret;
            note.CreatedBy = stored.CreatedBy;

            // Lifted first (the caller checked who may), so one patch can
            // unlock and change the text.
            if (patch.Locked == false) note.Locked = false;
            if (note.Locked && ((patch.Title is not null && patch.Title != note.Title)
                                || (patch.Content is not null && patch.Content != note.Content)))
                throw Locked();
            if (patch.Title is not null) note.Title = patch.Title;
            if (patch.Content is not null) note.Content = patch.Content;
            if (!string.IsNullOrWhiteSpace(patch.Append))
            {
                // Appending under a secret block would put text the client
                // never encrypted into a note whose secrets it holds.
                if (SecretStripper.ContainsSecret(note.Content) || note.ContentSecret is { Length: > 0 })
                    throw new ResourceValidationException(new ResourceValidationError(NoteLimits.Resource, "append",
                        "this note holds secrets — its text can only be changed in Fishbowl", ResourceValidationKind.Conflict, "secret_note"));
                var body = (note.Content ?? "").TrimEnd();
                note.Content = (body.Length > 0 ? body + "\n\n" : "") + patch.Append.Trim();
            }
            if (patch.Locked == true) note.Locked = true;
            if (patch.Pinned is { } pinned) note.Pinned = pinned;
            if (patch.Archived is { } archived) note.Archived = archived;
            if (slug is not null) note.Slug = slug.Length == 0 ? null : slug;

            note.Tags = PatchTags(stored.Tags, patch, source);
            EnforceLimits(ctx, note);
            note.Tags = (await _tagRepository.EnsureExistsAsync(db, tx, note.Tags, token)).ToList();

            var textChanged = note.Title != stored.Title || note.Content != stored.Content;
            var tagsChanged = !note.Tags.ToHashSet().SetEquals(stored.Tags);
            if (!textChanged && !tagsChanged && note.Slug == stored.Slug && note.Locked == stored.Locked
                && note.Pinned == stored.Pinned && note.Archived == stored.Archived)
                return note;   // nothing to write

            note.UpdatedAt = DateTime.UtcNow;
            await db.ExecuteAsync(new CommandDefinition(@"
                UPDATE notes
                SET title = @Title, content = @Content, tags = @Tags, slug = @Slug, locked = @Locked,
                    pinned = @Pinned, archived = @Archived, updated_at = @UpdatedAt
                WHERE id = @Id",
                new
                {
                    note.Title,
                    note.Content,
                    note.Tags,
                    note.Slug,
                    Locked = note.Locked ? 1 : 0,
                    Pinned = note.Pinned ? 1 : 0,
                    Archived = note.Archived ? 1 : 0,
                    UpdatedAt = note.UpdatedAt.ToString("o"),
                    note.Id,
                }, transaction: tx, cancellationToken: token));
            if (textChanged || tagsChanged)
            {
                await db.ExecuteAsync(new CommandDefinition(
                    "UPDATE notes_fts SET title = @Title, content = @Content, tags = @TagsFlat WHERE rowid = (SELECT rowid FROM notes WHERE id = @Id)",
                    new { note.Id, note.Title, Content = FtsContent(note.Content), TagsFlat = string.Join(' ', note.Tags) },
                    transaction: tx, cancellationToken: token));
                if (source == NoteSource.Mcp) await KeepVersionAsync(db, tx, stored, note, actorUserId, token);
                await UpsertEmbeddingAsync(db, tx, note, token);
            }
            return note;
        }, ct));
    }

    // A patch's tags: replaced, then added to and taken from, as stored.
    // An agent's change is marked for review; a person's text change is the
    // approval (as UpdateAsync), a change of tags alone isn't. Nobody adds a
    // tag only the system gives, and nobody takes one the system keeps.
    private static List<string> PatchTags(IReadOnlyCollection<string> stored, NotePatch patch, NoteSource source)
    {
        static string Norm(string t) => TagName.IsValid(t) ? TagName.Normalize(t) : t.Trim();
        var tags = (patch.Tags ?? stored.ToList()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(Norm).ToList();
        foreach (var t in patch.AddTags ?? new())
            if (!string.IsNullOrWhiteSpace(t) && !tags.Contains(Norm(t))) tags.Add(Norm(t));
        var remove = (patch.RemoveTags ?? new()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(Norm).ToHashSet();
        tags.RemoveAll(remove.Contains);

        var given = SystemTags.Seeds.ToDictionary(x => x.Name);
        if (source == NoteSource.Mcp)
        {
            foreach (var t in new[] { "source:mcp", "review:pending" }) if (!tags.Contains(t)) tags.Add(t);
        }
        else
        {
            tags.RemoveAll(t => given.TryGetValue(t, out var spec) && !spec.UserAssignable && !stored.Contains(t));
            if (patch.ChangesText) tags.Remove("review:pending");
        }
        foreach (var t in stored)
            if (given.TryGetValue(t, out var spec) && !spec.UserRemovable && !tags.Contains(t)) tags.Add(t);
        return tags.Distinct().ToList();
    }

    // The note as it was before an agent's write; the newest few a note.
    private static async Task KeepVersionAsync(IDbConnection db, IDbTransaction tx, Note before, Note after, string? actor, CancellationToken ct)
    {
        if (before.Title == after.Title && before.Content == after.Content && before.Tags.ToHashSet().SetEquals(after.Tags)) return;
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO note_versions (id, note_id, title, content, tags, saved_at, saved_by, source)
            VALUES (@id, @noteId, @title, @content, @tags, @at, @by, 'mcp');
            DELETE FROM note_versions WHERE note_id = @noteId AND id NOT IN (
                SELECT id FROM note_versions WHERE note_id = @noteId ORDER BY saved_at DESC, id DESC LIMIT @kept);",
            new
            {
                id = Ulid.NewUlid().ToString(),
                noteId = before.Id,
                title = before.Title,
                content = before.Content,
                tags = System.Text.Json.JsonSerializer.Serialize(before.Tags),
                at = DateTime.UtcNow.ToString("o"),
                by = actor,
                kept = NoteVersionLimits.Kept,
            }, transaction: tx, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<NoteVersion>> ListVersionsAsync(ContextRef ctx, string noteId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return (await db.QueryAsync<NoteVersion>(new CommandDefinition(
            "SELECT * FROM note_versions WHERE note_id = @noteId ORDER BY saved_at DESC, id DESC",
            new { noteId }, cancellationToken: ct))).ToList();
    }

    public async Task<NoteVersion?> GetVersionAsync(ContextRef ctx, string noteId, string versionId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.QuerySingleOrDefaultAsync<NoteVersion>(new CommandDefinition(
            "SELECT * FROM note_versions WHERE note_id = @noteId AND id = @versionId",
            new { noteId, versionId }, cancellationToken: ct));
    }

    private static ResourceValidationException Locked() => new(new ResourceValidationError(
        NoteLimits.Resource, "locked", "this note is locked — its title and text don't change; text can only be appended",
        ResourceValidationKind.Conflict, "note_locked"));

    // A slug another note has: the unique index answers, a 409 says so.
    private static async Task<T> SlugGuard<T>(Func<Task<T>> write)
    {
        try { return await write(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && ex.Message.Contains("slug", StringComparison.OrdinalIgnoreCase))
        {
            throw new ResourceValidationException(new ResourceValidationError(NoteLimits.Resource, "slug",
                "another note of this workspace has that slug", ResourceValidationKind.Conflict, "slug_taken"));
        }
    }

    private static async Task SlugGuard(Func<Task> write) => await SlugGuard(async () => { await write(); return true; });

    public async Task ReindexAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var note = await db.QuerySingleOrDefaultAsync<Note>(new CommandDefinition(
                "SELECT * FROM notes WHERE id = @id", new { id }, transaction: tx, cancellationToken: token));
            if (note is null) return;
            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM notes_fts WHERE rowid = (SELECT rowid FROM notes WHERE id = @id)",
                new { id }, transaction: tx, cancellationToken: token));
            await db.ExecuteAsync(new CommandDefinition(
                "INSERT INTO notes_fts (rowid, title, content, tags) VALUES ((SELECT rowid FROM notes WHERE id = @Id), @Title, @Content, @TagsFlat)",
                new { note.Id, note.Title, Content = FtsContent(note.Content), TagsFlat = string.Join(' ', note.Tags) },
                transaction: tx, cancellationToken: token));
            await UpsertEmbeddingAsync(db, tx, note, token);
        }, ct);
    }

    // ────────── Bulk re-embed ──────────
    //
    // The model runs with no transaction open — a write lock held across
    // every note's embedding would leave every other request of the context
    // with "database is locked". The vectors of each chunk then land in one
    // short transaction; a note changed or deleted since it was read is
    // skipped there (that write brought its own vector).
    private const int ReEmbedChunkSize = 32;

    public async Task<ReEmbedResult> ReEmbedAllAsync(ContextRef ctx, CancellationToken ct = default)
    {
        if (_embeddings is null)
        {
            _logger.LogInformation("Re-embed skipped — no embedding service configured");
            return new ReEmbedResult(Processed: 0, Failed: 0);
        }

        var processed = 0;
        var failed = 0;

        List<Note> rows;
        using (var read = _dbFactory.CreateContextConnection(ctx))
        {
            rows = (await read.QueryAsync<Note>(new CommandDefinition(
                "SELECT * FROM notes ORDER BY updated_at DESC", cancellationToken: ct))).ToList();
        }

        foreach (var chunk in rows.Chunk(ReEmbedChunkSize))
        {
            var embedded = new List<(string Id, string Text, byte[] Blob)>(chunk.Length);
            foreach (var note in chunk)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var text = BuildEmbeddingText(note);
                    var vec = await _embeddings.EmbedAsync(text, ct);
                    var blob = new byte[vec.Length * sizeof(float)];
                    Buffer.BlockCopy(vec, 0, blob, 0, blob.Length);
                    embedded.Add((note.Id, text, blob));
                }
                catch (EmbeddingUnavailableException)
                {
                    // Model went away mid-run. Leave the existing vector in
                    // place (or none) and move on; the user can retry.
                    _logger.LogDebug("Re-embed hit EmbeddingUnavailable on note {Id}; leaving existing row", note.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Re-embed failed for note {Id}", note.Id);
                    failed++;
                }
            }
            if (embedded.Count == 0) continue;

            processed += await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
            {
                var written = 0;
                foreach (var (id, text, blob) in embedded)
                {
                    var current = await db.QuerySingleOrDefaultAsync<Note>(new CommandDefinition(
                        "SELECT * FROM notes WHERE id = @id", new { id }, transaction: tx, cancellationToken: token));
                    if (current is null || BuildEmbeddingText(current) != text) continue;
                    await db.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM vec_notes WHERE id = @id",
                        new { id }, transaction: tx, cancellationToken: token));
                    await db.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO vec_notes(id, embedding) VALUES (@id, @blob)",
                        new { id, blob }, transaction: tx, cancellationToken: token));
                    written++;
                }
                return written;
            }, ct);
        }

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

    // What notes_fts indexes of a note's content: the same secret-stripped
    // text the model gets. A secret block that reached the stored content in
    // the clear (written by a key, before the vault, or behind a character
    // the client didn't take for whitespace) must not make the note findable
    // by its secret. TrashRepository's restore and the user v20 migration
    // index the same way.
    public static string? FtsContent(string? content) => SecretStripper.Strip(content);

    // ────────── Locked-tag preservation ──────────
    // Reads the existing notes.tags JSON for the row, then merges any tag
    // marked UserRemovable=false in SystemTags.Seeds into `note.Tags`.
    // Cheap (one SELECT inside the existing tx, no extra connection); the
    // alternative is letting the caller silently strip provenance markers.
    // No-op when the row doesn't exist yet — the UPDATE below will simply
    // affect 0 rows.
    private static async Task PreserveLockedTagsAsync(
        IDbConnection db, IDbTransaction tx, Note note, CancellationToken ct)
    {
        var lockedNames = SystemTags.Seeds
            .Where(s => !s.UserRemovable)
            .Select(s => s.Name)
            .ToList();
        if (lockedNames.Count == 0) return;

        var rawTags = await db.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT tags FROM notes WHERE id = @Id",
            new { note.Id }, transaction: tx, cancellationToken: ct));
        if (string.IsNullOrEmpty(rawTags)) return;

        // notes.tags is JSON via JsonTagsHandler in production; the raw
        // SELECT skips that handler so we re-parse here. Tolerate older
        // rows or test seeds that wrote a non-JSON value by bailing on
        // parse failure — preservation is best-effort, not a correctness
        // boundary.
        List<string>? existing;
        try { existing = System.Text.Json.JsonSerializer.Deserialize<List<string>>(rawTags); }
        catch (System.Text.Json.JsonException) { return; }
        if (existing is null) return;

        note.Tags ??= new List<string>();
        var live = new HashSet<string>(note.Tags, StringComparer.Ordinal);
        foreach (var name in lockedNames)
        {
            if (existing.Contains(name)) live.Add(name);
        }
        note.Tags = live.ToList();
    }

    // ────────── Write gate ──────────
    // Throws ResourceValidationException when a field is past the hard cap
    // (413) or the note can't be accepted as sent (400): a content_secret
    // that isn't a v2 envelope, or any secret at all in a space — spaces
    // have no shared vault yet, so a secret there would sit in the space DB
    // in plaintext. Every write path (cookie, Bearer, MCP) passes through
    // here; MCP tools surface the same exception as InvalidParams.
    private static void EnforceLimits(ContextRef ctx, Note note)
    {
        var error = NoteLimits.Validate(note) ?? SecretEnvelope.Validate(note.ContentSecret);
        if (error is null && ctx.Type == ContextType.Space
            && (note.ContentSecret is not null || SecretStripper.ContainsSecret(note.Content)))
        {
            error = new ResourceValidationError(NoteLimits.Resource, "content",
                "secrets are personal for now — spaces can't hold them", ResourceValidationKind.Invalid);
        }
        if (error is not null) throw new ResourceValidationException(error);
    }

    // ────────── Source-tag massage ──────────
    // Runs inside CreateAsync/UpdateAsync before the tag-repo's EnsureExists.
    // Mcp writes gain `source:mcp` + `review:pending` so the human notices
    // them in the review inbox. Human writes strip `review:pending` —
    // editing a pending note is implicit approval.
    //
    // Audit integrity: a Human write must not be able to forge `source:mcp`
    // (or any other UserAssignable=false system tag) by stuffing it into
    // the request body. SystemTags.Seeds is the source of truth — anything
    // marked UserAssignable=false gets stripped from incoming tags on the
    // Human path. Mcp writes bypass the strip because the MCP path *is*
    // the legitimate origin for those tags.
    private static void ApplySourceTags(Note note, NoteSource source)
    {
        note.Tags ??= new List<string>();
        // Compared as they will be stored: "Source:MCP" is source:mcp once
        // EnsureExistsAsync normalises it, so it must not slip past the strip
        // below. A name TagName can't take stays as sent — EnforceLimits
        // refuses it.
        var tags = new HashSet<string>(
            note.Tags.Select(t => TagName.IsValid(t) ? TagName.Normalize(t) : t),
            StringComparer.Ordinal);

        if (source == NoteSource.Mcp)
        {
            tags.Add("source:mcp");
            tags.Add("review:pending");
        }
        else
        {
            tags.Remove("review:pending");
            foreach (var spec in SystemTags.Seeds)
            {
                if (!spec.UserAssignable) tags.Remove(spec.Name);
            }
        }

        note.Tags = tags.ToList();
    }
}
