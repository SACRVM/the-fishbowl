using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Fishbowl.Data.Repositories;

namespace Fishbowl.Data.Mail;

/// <summary>
/// A workspace's mail rules (mail spec decision 11, user/space schema v26).
/// The tags a rule gives are the workspace's tags: storing a rule makes the
/// ones it names, like giving them by hand.
/// </summary>
public class MailRuleRepository
{
    private readonly DatabaseFactory _db;
    private readonly ITagRepository _tags;

    public MailRuleRepository(DatabaseFactory db, ITagRepository? tags = null)
    {
        _db = db;
        _tags = tags ?? new TagRepository(db);
    }

    private static string Iso(DateTime d) => TimeUtil.AsUtc(d).ToString("o");

    private const string Columns = "id, name, enabled, match_all, conditions, add_tags, archive, mark_read, created_by, created_at, updated_at";

    public async Task<IReadOnlyList<MailRule>> ListAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        return (await db.QueryAsync<MailRule>(new CommandDefinition(
            $"SELECT {Columns} FROM mail_rules ORDER BY name COLLATE NOCASE, id", cancellationToken: ct))).ToList();
    }

    public async Task<MailRule?> GetAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        return await db.QuerySingleOrDefaultAsync<MailRule>(new CommandDefinition(
            $"SELECT {Columns} FROM mail_rules WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<int> CountAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        return await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM mail_rules", cancellationToken: ct));
    }

    public Task<MailRule> CreateAsync(ContextRef ctx, MailRule rule, string? createdBy, CancellationToken ct = default) =>
        _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var now = DateTime.UtcNow;
            rule.Id = Ulid.NewUlid().ToString();
            rule.CreatedBy = createdBy;
            rule.CreatedAt = rule.UpdatedAt = now;
            await CleanAsync(db, tx, rule, token);
            await db.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO mail_rules (id, name, enabled, match_all, conditions, add_tags, archive, mark_read, created_by, created_at, updated_at)
                VALUES (@Id, @Name, @Enabled, @MatchAll, @Conditions, @AddTags, @Archive, @MarkRead, @CreatedBy, @now, @now)",
                new { rule.Id, rule.Name, rule.Enabled, rule.MatchAll, rule.Conditions, rule.AddTags, rule.Archive, rule.MarkRead, rule.CreatedBy, now = Iso(now) },
                tx, cancellationToken: token));
            return rule;
        }, ct);

    /// <summary>Replaces what the rule is; null when there is no such rule.</summary>
    public Task<MailRule?> UpdateAsync(ContextRef ctx, string id, MailRule rule, CancellationToken ct = default) =>
        _db.WithContextTransactionAsync<MailRule?>(ctx, async (db, tx, token) =>
        {
            await CleanAsync(db, tx, rule, token);
            var now = DateTime.UtcNow;
            var n = await db.ExecuteAsync(new CommandDefinition(@"
                UPDATE mail_rules SET name = @Name, enabled = @Enabled, match_all = @MatchAll, conditions = @Conditions,
                    add_tags = @AddTags, archive = @Archive, mark_read = @MarkRead, updated_at = @now
                WHERE id = @id",
                new { id, rule.Name, rule.Enabled, rule.MatchAll, rule.Conditions, rule.AddTags, rule.Archive, rule.MarkRead, now = Iso(now) },
                tx, cancellationToken: token));
            if (n == 0) return null;
            return await db.QuerySingleAsync<MailRule>(new CommandDefinition(
                $"SELECT {Columns} FROM mail_rules WHERE id = @id", new { id }, tx, cancellationToken: token));
        }, ct);

    public async Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM mail_rules WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    /// <summary>Makes sure the tags exist (a tag deleted since a rule named it
    /// comes back when the rule gives it).</summary>
    public Task<IReadOnlyList<string>> EnsureTagsAsync(ContextRef ctx, IEnumerable<string> tags, CancellationToken ct = default) =>
        _db.WithContextTransactionAsync(ctx, (db, tx, token) => _tags.EnsureExistsAsync(db, tx, tags, token), ct);

    // Trimmed text, the workspace's tag names; a source tag (an account's,
    // given by no one) is never given by a rule either.
    private async Task CleanAsync(System.Data.IDbConnection db, System.Data.IDbTransaction tx, MailRule rule, CancellationToken ct)
    {
        rule.Name = rule.Name.Trim();
        rule.Conditions = rule.Conditions.Select(c => new MailRuleCondition(c.Field, c.Value.Trim())).ToList();
        var fixedNames = (await db.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM tags WHERE is_system = 1 AND user_assignable = 0", transaction: tx, cancellationToken: ct))).ToHashSet();
        var wanted = rule.AddTags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(TagName.Normalize).Where(t => !fixedNames.Contains(t));
        rule.AddTags = (await _tags.EnsureExistsAsync(db, tx, wanted, ct)).ToList();
    }
}
