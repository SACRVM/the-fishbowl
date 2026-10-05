using System.Data;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class TagRepository : ITagRepository
{
    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<TagRepository> _logger;

    public TagRepository(DatabaseFactory dbFactory, ILogger<TagRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<TagRepository>.Instance;
    }

    // ────────── ContextRef overloads (canonical implementation) ──────────

    public async Task<IEnumerable<Tag>> GetAllAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        return await db.QueryAsync<Tag>(new CommandDefinition(@"
            SELECT t.name AS Name,
                   t.color AS Color,
                   t.created_at AS CreatedAt,
                   t.is_system AS IsSystem,
                   t.user_assignable AS UserAssignable,
                   t.user_removable AS UserRemovable,
                   COALESCE((SELECT COUNT(*) FROM notes n
                             WHERE EXISTS (SELECT 1 FROM json_each(n.tags) je WHERE je.value = t.name)), 0) AS UsageCount
            FROM tags t
            ORDER BY t.name", cancellationToken: ct));
    }

    public async Task<Tag> UpsertColorAsync(ContextRef ctx, string name, string color, CancellationToken ct = default)
    {
        var normalized = TagName.Normalize(name);
        if (!TagPalette.Slots.Contains(color))
        {
            throw new ArgumentException(
                $"Color '{color}' is not in the tag palette.", nameof(color));
        }

        using var db = _dbFactory.CreateContextConnection(ctx);
        var now = DateTime.UtcNow.ToString("o");
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO tags(name, color, created_at) VALUES (@name, @color, @createdAt)
            ON CONFLICT(name) DO UPDATE SET color = excluded.color",
            new { name = normalized, color, createdAt = now }, cancellationToken: ct));

        return await db.QuerySingleAsync<Tag>(new CommandDefinition(
            @"SELECT name AS Name, color AS Color, created_at AS CreatedAt,
                     is_system AS IsSystem,
                     user_assignable AS UserAssignable,
                     user_removable AS UserRemovable,
                     0 AS UsageCount
              FROM tags WHERE name = @name",
            new { name = normalized }, cancellationToken: ct));
    }

    public async Task<bool> RenameAsync(ContextRef ctx, string oldName, string newName, CancellationToken ct = default)
    {
        var oldN = TagName.Normalize(oldName);
        var newN = TagName.Normalize(newName);
        if (oldN == newN) return false;
        // Nor can a tag become one: renaming `foo` to `source:mcp` would put
        // the provenance marker on every note that carried foo.
        if (SystemTags.IsReserved(newN))
            throw new ArgumentException($"'{newN}' is a system tag name; a tag can't be renamed to it.");

        return await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            // System tags have load-bearing names — workflows match on them.
            // Silently-different names would break review filters / MCP writes.
            var isSystem = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT is_system FROM tags WHERE name = @oldN",
                new { oldN }, transaction: tx, cancellationToken: token));
            if (isSystem == 1)
                throw new ArgumentException($"Tag '{oldN}' is a system tag and cannot be renamed.");

            var oldExists = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM tags WHERE name = @oldN",
                new { oldN }, transaction: tx, cancellationToken: token));
            if (oldExists == 0) return false;

            // Metadata: either rename row (cheap) or merge into existing newN.
            var newExists = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM tags WHERE name = @newN",
                new { newN }, transaction: tx, cancellationToken: token));

            if (newExists == 0)
            {
                await db.ExecuteAsync(new CommandDefinition(
                    "UPDATE tags SET name = @newN WHERE name = @oldN",
                    new { oldN, newN }, transaction: tx, cancellationToken: token));
            }
            else
            {
                // Target already exists — drop old row, keep new's color.
                await db.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM tags WHERE name = @oldN",
                    new { oldN }, transaction: tx, cancellationToken: token));
            }

            await RewriteNoteTagsAsync(db, tx, oldN, newN, token);
            return true;
        }, ct);
    }

    public async Task<bool> DeleteAsync(ContextRef ctx, string name, CancellationToken ct = default)
    {
        var normalized = TagName.Normalize(name);

        return await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var isSystem = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT is_system FROM tags WHERE name = @normalized",
                new { normalized }, transaction: tx, cancellationToken: token));
            if (isSystem == 1)
                throw new ArgumentException($"Tag '{normalized}' is a system tag and cannot be deleted.");

            var affected = await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tags WHERE name = @normalized",
                new { normalized }, transaction: tx, cancellationToken: token));
            if (affected == 0) return false;

            await RewriteNoteTagsAsync(db, tx, normalized, newName: null, token);
            return true;
        }, ct);
    }

    // ────────── Legacy (personal-context) aliases ──────────

    public Task<IEnumerable<Tag>> GetAllAsync(string userId, CancellationToken ct = default)
        => GetAllAsync(ContextRef.User(userId), ct);

    public Task<Tag> UpsertColorAsync(string userId, string name, string color, CancellationToken ct = default)
        => UpsertColorAsync(ContextRef.User(userId), name, color, ct);

    public Task<bool> RenameAsync(string userId, string oldName, string newName, CancellationToken ct = default)
        => RenameAsync(ContextRef.User(userId), oldName, newName, ct);

    public Task<bool> DeleteAsync(string userId, string name, CancellationToken ct = default)
        => DeleteAsync(ContextRef.User(userId), name, ct);

    public async Task<IReadOnlyList<string>> EnsureExistsAsync(
        IDbConnection db,
        IDbTransaction tx,
        IEnumerable<string> rawNames,
        CancellationToken ct = default)
    {
        var normalized = rawNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(TagName.Normalize)
            .Distinct()
            .ToList();

        if (normalized.Count == 0) return normalized;

        var now = DateTime.UtcNow.ToString("o");
        foreach (var name in normalized)
        {
            await db.ExecuteAsync(new CommandDefinition(
                "INSERT OR IGNORE INTO tags(name, color, created_at) VALUES (@name, @color, @createdAt)",
                new { name, color = TagPalette.DefaultFor(name), createdAt = now },
                transaction: tx, cancellationToken: ct));
        }

        return normalized;
    }

    // Rewrites every note's `tags` JSON array + notes_fts.tags for a single
    // tag name. If newName is null, strips oldName; otherwise replaces it.
    // Runs inside the caller's transaction.
    private static async Task RewriteNoteTagsAsync(
        IDbConnection db, IDbTransaction tx, string oldName, string? newName, CancellationToken ct)
    {
        var notes = (await db.QueryAsync<Note>(new CommandDefinition(@"
            SELECT id AS Id, title AS Title, content AS Content, tags AS Tags
            FROM notes
            WHERE EXISTS (SELECT 1 FROM json_each(notes.tags) je WHERE je.value = @oldName)",
            new { oldName }, transaction: tx, cancellationToken: ct))).ToList();

        var nowIso = DateTime.UtcNow.ToString("o");
        foreach (var note in notes)
        {
            var next = note.Tags.Where(t => t != oldName).ToList();
            if (newName is not null && !next.Contains(newName)) next.Add(newName);

            await db.ExecuteAsync(new CommandDefinition(
                "UPDATE notes SET tags = @Tags, updated_at = @UpdatedAt WHERE id = @Id",
                new { Tags = next, UpdatedAt = nowIso, note.Id },
                transaction: tx, cancellationToken: ct));

            await db.ExecuteAsync(new CommandDefinition(
                @"UPDATE notes_fts SET tags = @TagsFlat
                  WHERE rowid = (SELECT rowid FROM notes WHERE id = @Id)",
                new { note.Id, TagsFlat = string.Join(' ', next) },
                transaction: tx, cancellationToken: ct));
        }
    }
}
