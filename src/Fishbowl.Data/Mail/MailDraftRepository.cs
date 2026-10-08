using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Util;

namespace Fishbowl.Data.Mail;

/// <summary>
/// Drafts (mail spec decision 12, user/space schema v24): kept in the
/// workspace, never in the server's Drafts folder, with the files that go
/// along — the bytes of what was uploaded or taken from Files, only where a
/// forwarded original's attachment is (it is fetched when the mail goes).
/// </summary>
public class MailDraftRepository
{
    private readonly DatabaseFactory _db;

    public MailDraftRepository(DatabaseFactory db) => _db = db;

    private static string Iso(DateTime d) => TimeUtil.AsUtc(d).ToString("o");

    private const string Columns = @"id, account_id, kind, ref_message_id, thread_id, to_list AS ""to"", cc_list AS cc,
        bcc_list AS bcc, subject, body, created_by, created_at, updated_at";

    public sealed record FileContent(MailDraftFile File, byte[]? Data, string? SourceMessageId, int? SourceIndex);

    /// <summary>The drafts <paramref name="createdBy"/> is writing, newest first.</summary>
    public async Task<IReadOnlyList<MailDraft>> ListAsync(ContextRef ctx, string createdBy, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        var drafts = (await db.QueryAsync<MailDraft>(new CommandDefinition(
            $"SELECT {Columns} FROM mail_drafts WHERE created_by = @createdBy ORDER BY updated_at DESC",
            new { createdBy }, cancellationToken: ct))).ToList();
        var files = (await db.QueryAsync<(string DraftId, long Idx, string Name, string Mime, long Size, string? SourceMessageId)>(new CommandDefinition(
            "SELECT draft_id, idx, name, mime, size, source_message_id FROM mail_draft_files ORDER BY idx", cancellationToken: ct)))
            .ToLookup(f => f.DraftId);
        foreach (var d in drafts)
            d.Files = files[d.Id].Select(f => new MailDraftFile((int)f.Idx, f.Name, f.Mime, f.Size, f.SourceMessageId is not null)).ToList();
        return drafts;
    }

    public async Task<MailDraft?> GetAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        var d = await db.QuerySingleOrDefaultAsync<MailDraft>(new CommandDefinition(
            $"SELECT {Columns} FROM mail_drafts WHERE id = @id", new { id }, cancellationToken: ct));
        if (d is null) return null;
        d.Files = (await db.QueryAsync<(long Idx, string Name, string Mime, long Size, string? SourceMessageId)>(new CommandDefinition(
            "SELECT idx, name, mime, size, source_message_id FROM mail_draft_files WHERE draft_id = @id ORDER BY idx",
            new { id }, cancellationToken: ct)))
            .Select(f => new MailDraftFile((int)f.Idx, f.Name, f.Mime, f.Size, f.SourceMessageId is not null)).ToList();
        return d;
    }

    /// <summary>Stores a new draft; <paramref name="forwarded"/> are the
    /// original's attachments it carries (message id, index, name, type, size).</summary>
    public Task<MailDraft> CreateAsync(ContextRef ctx, MailDraft d, IReadOnlyList<(string MessageId, MailAttachment Attachment)> forwarded, CancellationToken ct = default) =>
        _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            d.Id = Ulid.NewUlid().ToString();
            d.CreatedAt = d.UpdatedAt = DateTime.UtcNow;
            await db.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO mail_drafts (id, account_id, kind, ref_message_id, thread_id, to_list, cc_list, bcc_list,
                    subject, body, created_by, created_at, updated_at)
                VALUES (@Id, @AccountId, @Kind, @RefMessageId, @ThreadId, @To, @Cc, @Bcc, @Subject, @Body, @CreatedBy, @Now, @Now)",
                new { d.Id, d.AccountId, d.Kind, d.RefMessageId, d.ThreadId, d.To, d.Cc, d.Bcc, d.Subject, d.Body, d.CreatedBy, Now = Iso(d.CreatedAt) },
                tx, cancellationToken: token));
            d.Files = new();
            foreach (var (messageId, a) in forwarded)
            {
                var file = new MailDraftFile(d.Files.Count, a.Name ?? $"attachment-{a.Index}", a.Type, a.Size ?? 0, true);
                await db.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO mail_draft_files (draft_id, idx, name, mime, size, source_message_id, source_index)
                    VALUES (@draftId, @Index, @Name, @Type, @Size, @messageId, @source)",
                    new { draftId = d.Id, file.Index, file.Name, file.Type, file.Size, messageId, source = a.Index }, tx, cancellationToken: token));
                d.Files.Add(file);
            }
            return d;
        }, ct);

    /// <summary>Changes what is named; false when there is no such draft.</summary>
    public async Task<bool> UpdateAsync(ContextRef ctx, string id, MailDraftUpdate u, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        return await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE mail_drafts SET
                account_id = COALESCE(@AccountId, account_id),
                to_list = COALESCE(@To, to_list),
                cc_list = COALESCE(@Cc, cc_list),
                bcc_list = COALESCE(@Bcc, bcc_list),
                subject = COALESCE(@Subject, subject),
                body = COALESCE(@Body, body),
                updated_at = @now
            WHERE id = @id",
            new { id, u.AccountId, u.To, u.Cc, u.Bcc, u.Subject, u.Body, now = Iso(DateTime.UtcNow) }, cancellationToken: ct)) > 0;
    }

    public Task<bool> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default) =>
        _db.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            await db.ExecuteAsync(new CommandDefinition("DELETE FROM mail_draft_files WHERE draft_id = @id", new { id }, tx, cancellationToken: token));
            return await db.ExecuteAsync(new CommandDefinition("DELETE FROM mail_drafts WHERE id = @id", new { id }, tx, cancellationToken: token)) > 0;
        }, ct);

    /// <summary>The bytes the draft's files hold so far (forwarded ones count
    /// by their size).</summary>
    public async Task<long> FilesBytesAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        return await db.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(SUM(size), 0) FROM mail_draft_files WHERE draft_id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>Adds a file with its bytes; null when there is no such draft.</summary>
    public Task<MailDraftFile?> AddFileAsync(ContextRef ctx, string id, string name, string type, byte[] data, CancellationToken ct = default) =>
        _db.WithContextTransactionAsync<MailDraftFile?>(ctx, async (db, tx, token) =>
        {
            if (await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM mail_drafts WHERE id = @id", new { id }, tx, cancellationToken: token)) == 0)
                return null;
            var idx = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COALESCE(MAX(idx) + 1, 0) FROM mail_draft_files WHERE draft_id = @id", new { id }, tx, cancellationToken: token));
            var file = new MailDraftFile((int)idx, name, type, data.LongLength, false);
            await db.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO mail_draft_files (draft_id, idx, name, mime, size, data) VALUES (@id, @idx, @name, @type, @size, @data);
                UPDATE mail_drafts SET updated_at = @now WHERE id = @id;",
                new { id, idx, name, type, size = data.LongLength, data, now = Iso(DateTime.UtcNow) }, tx, cancellationToken: token));
            return file;
        }, ct);

    public async Task<bool> RemoveFileAsync(ContextRef ctx, string id, int index, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        return await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mail_draft_files WHERE draft_id = @id AND idx = @index", new { id, index }, cancellationToken: ct)) > 0;
    }

    /// <summary>Every file of the draft with what it needs to go out.</summary>
    public async Task<IReadOnlyList<FileContent>> FileContentsAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        return (await db.QueryAsync<(long Idx, string Name, string Mime, long Size, byte[]? Data, string? SourceMessageId, long? SourceIndex)>(new CommandDefinition(
            "SELECT idx, name, mime, size, data, source_message_id, source_index FROM mail_draft_files WHERE draft_id = @id ORDER BY idx",
            new { id }, cancellationToken: ct)))
            .Select(f => new FileContent(new MailDraftFile((int)f.Idx, f.Name, f.Mime, f.Size, f.SourceMessageId is not null),
                f.Data, f.SourceMessageId, (int?)f.SourceIndex)).ToList();
    }
}
