using System.Text;
using Fishbowl.Mail.Auth;
using Fishbowl.Mail.Models;
using Fishbowl.Mail.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using MessageSummary = Fishbowl.Mail.Models.MessageSummary;
using SearchOptions = Fishbowl.Mail.Models.SearchOptions;

namespace Fishbowl.Mail.Imap;

/// <summary>
/// One IMAP connection per account. <see cref="ImapClient"/> is not thread-safe, so every
/// operation takes the session lock and reconnects transparently if the link dropped.
/// </summary>
public sealed class MailboxSession : IAsyncDisposable
{
    private readonly ResolvedAccount _account;
    private readonly IAuthenticator _auth;
    private readonly MailLimits _limits;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ImapClient _client = new();

    public MailboxSession(ResolvedAccount account, IAuthenticator auth, MailLimits limits, ILogger log)
    {
        _account = account;
        _auth = auth;
        _limits = limits;
        _log = log;
    }

    public ResolvedAccount Account => _account;
    public bool IsConnected => _client.IsConnected && _client.IsAuthenticated;

    // ---------- connection ----------

    public async Task<T> RunAsync<T>(Func<ImapClient, CancellationToken, Task<T>> op, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            try
            {
                return await op(_client, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ImapProtocolException or ServiceNotConnectedException or ServiceNotAuthenticatedException)
            {
                _log.LogWarning(ex, "[{Account}] IMAP link dropped, reconnecting once", _account.Id);
                await ResetAsync().ConfigureAwait(false);
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                return await op(_client, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client.IsConnected && _client.IsAuthenticated) return;
        if (_client.IsConnected) await ResetAsync().ConfigureAwait(false);

        _log.LogInformation("[{Account}] connecting {Host}:{Port}", _account.Id, _account.ImapHost, _account.ImapPort);
        await _client.ConnectAsync(_account.ImapHost, _account.ImapPort, ToSocketOptions(_account.ImapSecurity), ct).ConfigureAwait(false);
        try
        {
            await _auth.AuthenticateAsync(_client, _account, ct).ConfigureAwait(false);
        }
        catch (AuthenticationException ex)
        {
            await ResetAsync().ConfigureAwait(false);
            throw new MailException($"Account '{_account.Id}': IMAP authentication failed ({ex.Message}). {_account.Preset.Notes}", ex);
        }
    }

    private async Task ResetAsync()
    {
        try { if (_client.IsConnected) await _client.DisconnectAsync(true).ConfigureAwait(false); } catch { /* ignore */ }
        _client.Dispose();
        _client = new ImapClient();
    }

    internal static SecureSocketOptions ToSocketOptions(TlsMode mode) => mode switch
    {
        TlsMode.Ssl => SecureSocketOptions.SslOnConnect,
        TlsMode.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };

    // ---------- folders ----------

    public Task<IReadOnlyList<FolderInfo>> ListFoldersAsync(bool withCounts, CancellationToken ct)
        => RunAsync<IReadOnlyList<FolderInfo>>(async (c, t) =>
        {
            var result = new List<FolderInfo>();
            foreach (var ns in c.PersonalNamespaces)
            {
                var folders = await c.GetFoldersAsync(ns, withCounts ? StatusItems.Unread | StatusItems.Count : StatusItems.None, false, t).ConfigureAwait(false);
                foreach (var f in folders)
                {
                    if (f.Attributes.HasFlag(FolderAttributes.NonExistent)) continue;
                    result.Add(new FolderInfo(f.FullName, f.Name,
                        withCounts && !f.Attributes.HasFlag(FolderAttributes.NoSelect) ? f.Unread : null,
                        withCounts && !f.Attributes.HasFlag(FolderAttributes.NoSelect) ? f.Count : null,
                        SpecialUseOf(f)));
                }
            }
            return result;
        }, ct);

    private static string? SpecialUseOf(IMailFolder f)
    {
        if (f.Attributes.HasFlag(FolderAttributes.Inbox)) return "inbox";
        if (f.Attributes.HasFlag(FolderAttributes.Drafts)) return "drafts";
        if (f.Attributes.HasFlag(FolderAttributes.Sent)) return "sent";
        if (f.Attributes.HasFlag(FolderAttributes.Trash)) return "trash";
        if (f.Attributes.HasFlag(FolderAttributes.Junk)) return "junk";
        if (f.Attributes.HasFlag(FolderAttributes.Archive)) return "archive";
        if (f.Attributes.HasFlag(FolderAttributes.All)) return "all";
        if (f.Attributes.HasFlag(FolderAttributes.Flagged)) return "flagged";
        return null;
    }

    /// <summary>Resolves a folder by full path, then by case-insensitive path/name match, then by special-use alias.</summary>
    internal static async Task<IMailFolder> ResolveFolderAsync(ImapClient c, string folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            return c.Inbox;

        var special = folder.ToLowerInvariant() switch
        {
            "drafts" => SpecialFolder.Drafts,
            "sent" => SpecialFolder.Sent,
            "trash" => SpecialFolder.Trash,
            "junk" or "spam" => SpecialFolder.Junk,
            "archive" => SpecialFolder.Archive,
            "all" => SpecialFolder.All,
            _ => (SpecialFolder?)null,
        };
        if (special is { } sp)
        {
            var sf = await FindSpecialFolderAsync(c, sp, ct).ConfigureAwait(false);
            if (sf is not null) return sf;

            // Gmail has no Archive folder: archiving means dropping the INBOX label, which a
            // MOVE to "[Gmail]/All Mail" achieves. Searching "archive" there is All Mail too.
            if (sp == SpecialFolder.Archive && c.Capabilities.HasFlag(ImapCapabilities.GMailExt1))
            {
                var all = await FindSpecialFolderAsync(c, SpecialFolder.All, ct).ConfigureAwait(false);
                if (all is not null) return all;
            }
        }

        try
        {
            var f = await c.GetFolderAsync(folder, ct).ConfigureAwait(false);
            if (f is not null) return f;
        }
        catch (FolderNotFoundException) { /* fall through to fuzzy match */ }

        foreach (var ns in c.PersonalNamespaces)
        {
            var all = await c.GetFoldersAsync(ns, false, ct).ConfigureAwait(false);
            var hit = all.FirstOrDefault(x => x.FullName.Equals(folder, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(x => x.Name.Equals(folder, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(x => x.FullName.EndsWith("/" + folder, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        throw new MailException($"Folder '{folder}' not found. Pass a folder path from the folder list.");
    }

    /// <summary>
    /// Special-use lookup that also works when the server does not advertise SPECIAL-USE/XLIST
    /// but still returns the attributes in LIST (iCloud does this). Legacy folders with a
    /// matching name but no attribute ("Sent", "Trash" left behind by old clients) are ignored here.
    /// </summary>
    internal static async Task<IMailFolder?> FindSpecialFolderAsync(ImapClient c, SpecialFolder sp, CancellationToken ct)
    {
        if (c.Capabilities.HasFlag(ImapCapabilities.SpecialUse) || c.Capabilities.HasFlag(ImapCapabilities.XList))
        {
            var sf = c.GetFolder(sp);
            if (sf is not null) return sf;
        }

        var attr = sp switch
        {
            SpecialFolder.Drafts => FolderAttributes.Drafts,
            SpecialFolder.Sent => FolderAttributes.Sent,
            SpecialFolder.Trash => FolderAttributes.Trash,
            SpecialFolder.Junk => FolderAttributes.Junk,
            SpecialFolder.Archive => FolderAttributes.Archive,
            SpecialFolder.All => FolderAttributes.All,
            SpecialFolder.Flagged => FolderAttributes.Flagged,
            SpecialFolder.Important => FolderAttributes.Important,
            _ => FolderAttributes.None,
        };
        if (attr == FolderAttributes.None) return null;

        foreach (var ns in c.PersonalNamespaces)
        {
            var all = await c.GetFoldersAsync(ns, false, ct).ConfigureAwait(false);
            var hit = all.FirstOrDefault(f => f.Attributes.HasFlag(attr) && !f.Attributes.HasFlag(FolderAttributes.NonExistent));
            if (hit is not null) return hit;
        }
        return null;
    }

    // ---------- search / list ----------

    public Task<IReadOnlyList<MessageSummary>> SearchAsync(SearchOptions o, CancellationToken ct)
        => RunAsync<IReadOnlyList<MessageSummary>>(async (c, t) =>
        {
            var folder = await ResolveFolderAsync(c, o.Folder, t).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, t).ConfigureAwait(false);
            try
            {
                var query = BuildQuery(o, c.Capabilities.HasFlag(ImapCapabilities.GMailExt1));
                var limit = Math.Clamp(o.Limit ?? _limits.DefaultSearchLimit, 1, _limits.MaxSearchLimit);

                var uids = await folder.SearchAsync(query, t).ConfigureAwait(false);
                if (uids.Count == 0) return [];

                // Newest first: UIDs are ascending by arrival, so take the tail.
                var wanted = uids.OrderByDescending(u => u.Id).Take(limit).ToList();

                var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags
                          | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure | MessageSummaryItems.InternalDate;
                if (c.Capabilities.HasFlag(ImapCapabilities.GMailExt1))
                    items |= MessageSummaryItems.GMailLabels | MessageSummaryItems.GMailThreadId;

                var summaries = await folder.FetchAsync(wanted, items, t).ConfigureAwait(false);
                return summaries
                    .OrderByDescending(s => s.InternalDate ?? s.Date)
                    .Select(s => ToSummary(s, folder.FullName))
                    .ToList();
            }
            finally
            {
                if (folder.IsOpen) await folder.CloseAsync(false, t).ConfigureAwait(false);
            }
        }, ct);

    internal static SearchQuery BuildQuery(SearchOptions o, bool gmail)
    {
        SearchQuery q = SearchQuery.All;
        if (!string.IsNullOrWhiteSpace(o.From)) q = q.And(SearchQuery.FromContains(o.From));
        if (!string.IsNullOrWhiteSpace(o.To)) q = q.And(SearchQuery.ToContains(o.To));
        if (!string.IsNullOrWhiteSpace(o.Subject)) q = q.And(SearchQuery.SubjectContains(o.Subject));
        if (!string.IsNullOrWhiteSpace(o.Text)) q = q.And(SearchQuery.BodyContains(o.Text));
        if (o.Since is { } since) q = q.And(SearchQuery.DeliveredAfter(since.UtcDateTime.Date));
        if (o.Before is { } before) q = q.And(SearchQuery.DeliveredBefore(before.UtcDateTime.Date));
        if (o.Unread is true) q = q.And(SearchQuery.NotSeen);
        if (o.Unread is false) q = q.And(SearchQuery.Seen);
        if (o.Flagged is true) q = q.And(SearchQuery.Flagged);
        if (o.Flagged is false) q = q.And(SearchQuery.NotFlagged);
        if (!string.IsNullOrWhiteSpace(o.GmailQuery))
        {
            if (!gmail) throw new MailException("gmailQuery is only supported on Gmail accounts.");
            q = q.And(SearchQuery.GMailRawSearch(o.GmailQuery));
        }
        return q;
    }

    private static MessageSummary ToSummary(IMessageSummary s, string folder)
    {
        var flags = s.Flags ?? MessageFlags.None;
        return new MessageSummary(
            s.UniqueId.Id,
            folder,
            s.InternalDate ?? s.Date,
            s.Envelope?.From is { Count: > 0 } from ? ToAddress(from.Mailboxes.First()) : null,
            s.Envelope?.To?.Mailboxes.Select(ToAddress).ToList() ?? [],
            s.Envelope?.Subject,
            flags.HasFlag(MessageFlags.Seen),
            flags.HasFlag(MessageFlags.Flagged),
            flags.HasFlag(MessageFlags.Answered),
            s.Size,
            s.Attachments.Any(),
            s.GMailLabels?.ToList() ?? [],
            s.GMailThreadId?.ToString());
    }

    private static MailAddress ToAddress(MailboxAddress m) => new(string.IsNullOrWhiteSpace(m.Name) ? null : m.Name, m.Address);

    // ---------- get ----------

    public Task<MessageContent> GetMessageAsync(string folderName, uint uid, int? maxChars, CancellationToken ct)
        => RunAsync(async (c, t) =>
        {
            var folder = await ResolveFolderAsync(c, folderName, t).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, t).ConfigureAwait(false);
            try
            {
                var id = new UniqueId(uid);
                var msg = await folder.GetMessageAsync(id, t).ConfigureAwait(false);

                IReadOnlyList<string> labels = [];
                if (c.Capabilities.HasFlag(ImapCapabilities.GMailExt1))
                {
                    var s = await folder.FetchAsync([id], MessageSummaryItems.GMailLabels, t).ConfigureAwait(false);
                    labels = s.FirstOrDefault()?.GMailLabels?.ToList() ?? [];
                }

                return ToContent(msg, folder.FullName, uid, labels, maxChars ?? _limits.MaxBodyChars);
            }
            catch (MessageNotFoundException)
            {
                throw new MailException($"Message uid {uid} not found in '{folderName}'.");
            }
            finally
            {
                if (folder.IsOpen) await folder.CloseAsync(false, t).ConfigureAwait(false);
            }
        }, ct);

    internal static MessageContent ToContent(MimeMessage msg, string folder, uint uid, IReadOnlyList<string> labels, int maxChars)
    {
        string text;
        string source;
        if (!string.IsNullOrWhiteSpace(msg.TextBody))
        {
            text = HtmlToText.Normalize(msg.TextBody);
            source = "text/plain";
        }
        else if (!string.IsNullOrWhiteSpace(msg.HtmlBody))
        {
            text = HtmlToText.Convert(msg.HtmlBody);
            source = "text/html";
        }
        else
        {
            text = string.Empty;
            source = "none";
        }

        var (body, truncated) = HtmlToText.Truncate(text, maxChars);

        var attachments = msg.Attachments.Select((a, i) => new AttachmentInfo(
            i,
            a is MimePart p ? p.FileName : (a as MessagePart)?.ContentDisposition?.FileName,
            a.ContentType.MimeType,
            a is MimePart mp && mp.Content is not null ? mp.Content.Stream?.Length : null)).ToList();

        return new MessageContent(
            uid, folder, msg.MessageId, msg.Date == default ? null : msg.Date,
            msg.From.Mailboxes.Select(ToAddress).FirstOrDefault(),
            msg.To.Mailboxes.Select(ToAddress).ToList(),
            msg.Cc.Mailboxes.Select(ToAddress).ToList(),
            msg.ReplyTo.Mailboxes.Select(ToAddress).ToList(),
            msg.Subject, body, truncated, source, attachments, labels,
            MimeInspector.Describe(msg), MimeInspector.ParseBounce(msg));
    }

    /// <summary>Loads the raw MimeMessage (used by reply and attachment export).</summary>
    public Task<MimeMessage> GetMimeMessageAsync(string folderName, uint uid, CancellationToken ct)
        => RunAsync(async (c, t) =>
        {
            var folder = await ResolveFolderAsync(c, folderName, t).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, t).ConfigureAwait(false);
            try
            {
                return await folder.GetMessageAsync(new UniqueId(uid), t).ConfigureAwait(false);
            }
            catch (MessageNotFoundException)
            {
                throw new MailException($"Message uid {uid} not found in '{folderName}'.");
            }
            finally
            {
                if (folder.IsOpen) await folder.CloseAsync(false, t).ConfigureAwait(false);
            }
        }, ct);

    /// <summary>Saves an attachment (by index) or any MIME part (by IMAP part id) to disk.</summary>
    public async Task<string> SaveAttachmentAsync(string folderName, uint uid, int? index, string? part, string targetPath, CancellationToken ct)
    {
        var msg = await GetMimeMessageAsync(folderName, uid, ct).ConfigureAwait(false);
        return await SavePartAsync(msg, uid, index, part, targetPath, ct).ConfigureAwait(false);
    }

    private static async Task<string> SavePartAsync(MimeMessage msg, uint uid, int? index, string? part, string targetPath, CancellationToken ct)
    {
        if (index is null == part is null)
            throw new MailException("Pass either index or part.");

        var entity = part is not null
            ? MimeInspector.Find(msg, part)
            : msg.Attachments.ElementAtOrDefault(index!.Value)
              ?? throw new MailException($"Message uid {uid} has no attachment with index {index}. Use part with an id from the message's parts list.");

        var fallback = part is not null ? $"part-{uid}-{part}{DefaultExtension(entity)}" : $"attachment-{uid}-{index}.bin";
        var finalPath = ResolveTargetFile(targetPath, MimeInspector.FileNameOf(entity) ?? fallback);
        await using var fs = File.Create(finalPath);
        await MimeInspector.WriteContentAsync(entity, fs, ct).ConfigureAwait(false);
        return finalPath;
    }

    private static string DefaultExtension(MimeEntity e) => e.ContentType.MimeType.ToLowerInvariant() switch
    {
        "message/rfc822" or "message/global" => ".eml",
        var t when t.StartsWith("text/") || t.StartsWith("message/") => ".txt",
        _ => ".bin",
    };

    /// <summary>An existing directory (or a path ending in a slash) gets <paramref name="fileName"/> inside it.</summary>
    private static string ResolveTargetFile(string targetPath, string fileName)
    {
        if (!Path.IsPathRooted(targetPath))
            throw new MailException("targetPath must be an absolute path.");

        var finalPath = Directory.Exists(targetPath) || targetPath.EndsWith(Path.DirectorySeparatorChar) || targetPath.EndsWith('/')
            ? Path.Combine(targetPath, SanitizeFileName(fileName))
            : targetPath;
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        return finalPath;
    }

    /// <summary>One MIME part as text, or written to disk when targetPath is given (required for binary parts).</summary>
    public async Task<PartContent> GetPartAsync(string folderName, uint uid, string part, string? targetPath, int? maxChars, CancellationToken ct)
    {
        var msg = await GetMimeMessageAsync(folderName, uid, ct).ConfigureAwait(false);
        var entity = MimeInspector.Find(msg, part);
        var fileName = MimeInspector.FileNameOf(entity);

        if (targetPath is not null)
        {
            var path = await SavePartAsync(msg, uid, null, part, targetPath, ct).ConfigureAwait(false);
            return new PartContent(uid, folderName, part, entity.ContentType.MimeType, fileName, null, false, path);
        }
        if (!MimeInspector.IsTextual(entity))
            throw new MailException($"Part {part} is {entity.ContentType.MimeType}; pass targetPath to save it.");

        var (text, truncated) = HtmlToText.Truncate(MimeInspector.ReadText(entity), maxChars ?? _limits.MaxBodyChars);
        return new PartContent(uid, folderName, part, entity.ContentType.MimeType, fileName, text, truncated, null);
    }

    // ---------- raw / export ----------

    /// <summary>Full RFC822 source via BODY.PEEK[], so the \Seen flag is untouched.</summary>
    public Task<RawMessage> GetRawAsync(string folderName, uint uid, string? targetPath, int? maxChars, CancellationToken ct)
        => RunAsync(async (c, t) =>
        {
            var folder = await ResolveFolderAsync(c, folderName, t).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, t).ConfigureAwait(false);
            try
            {
                await using var raw = await folder.GetStreamAsync(new UniqueId(uid), t).ConfigureAwait(false);
                if (targetPath is not null)
                {
                    var path = ResolveTargetFile(targetPath, $"{uid}.eml");
                    await using var fs = File.Create(path);
                    await raw.CopyToAsync(fs, t).ConfigureAwait(false);
                    return new RawMessage(uid, folder.FullName, fs.Length, null, false, path);
                }

                using var ms = new MemoryStream();
                await raw.CopyToAsync(ms, t).ConfigureAwait(false);
                var (text, truncated) = HtmlToText.Truncate(Encoding.UTF8.GetString(ms.ToArray()), maxChars ?? _limits.MaxBodyChars);
                return new RawMessage(uid, folder.FullName, ms.Length, text, truncated, null);
            }
            catch (MessageNotFoundException)
            {
                throw new MailException($"Message uid {uid} not found in '{folderName}'.");
            }
            finally
            {
                if (folder.IsOpen) await folder.CloseAsync(false, t).ConfigureAwait(false);
            }
        }, ct);

    /// <summary>
    /// Writes messages as &lt;uid&gt;.eml into targetDir, selected by explicit uids or by the search
    /// filters (newest first). Read-only: read-only SELECT and BODY.PEEK[].
    /// </summary>
    public Task<ExportResult> ExportAsync(SearchOptions filter, IReadOnlyList<uint>? uids, string targetDir, int limit, CancellationToken ct)
        => RunAsync(async (c, t) =>
        {
            if (!Path.IsPathRooted(targetDir))
                throw new MailException("targetDir must be an absolute path.");
            Directory.CreateDirectory(targetDir);

            var folder = await ResolveFolderAsync(c, filter.Folder, t).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, t).ConfigureAwait(false);
            try
            {
                List<uint> selected;
                if (uids is { Count: > 0 }) selected = uids.Distinct().ToList();
                else
                {
                    var found = await folder.SearchAsync(BuildQuery(filter, c.Capabilities.HasFlag(ImapCapabilities.GMailExt1)), t).ConfigureAwait(false);
                    selected = found.Select(u => u.Id).OrderByDescending(u => u).ToList();
                }

                var files = new List<ExportedMessage>();
                var missing = new List<uint>();
                foreach (var uid in selected.Take(limit))
                {
                    try
                    {
                        await using var raw = await folder.GetStreamAsync(new UniqueId(uid), t).ConfigureAwait(false);
                        var path = Path.Combine(targetDir, $"{uid}.eml");
                        await using var fs = File.Create(path);
                        await raw.CopyToAsync(fs, t).ConfigureAwait(false);
                        files.Add(new ExportedMessage(uid, path, fs.Length));
                    }
                    catch (MessageNotFoundException)
                    {
                        missing.Add(uid);
                    }
                }
                return new ExportResult(folder.FullName, targetDir, files, missing, selected.Count > limit);
            }
            finally
            {
                if (folder.IsOpen) await folder.CloseAsync(false, t).ConfigureAwait(false);
            }
        }, ct);

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? "attachment.bin" : name;
    }

    // ---------- flags / move ----------

    public Task MarkAsync(string folderName, IReadOnlyList<uint> uids, bool? seen, bool? flagged, CancellationToken ct)
        => RunAsync<object?>(async (c, t) =>
        {
            var folder = await ResolveFolderAsync(c, folderName, t).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadWrite, t).ConfigureAwait(false);
            try
            {
                var ids = uids.Select(u => new UniqueId(u)).ToList();
                if (seen is true) await folder.AddFlagsAsync(ids, MessageFlags.Seen, true, t).ConfigureAwait(false);
                if (seen is false) await folder.RemoveFlagsAsync(ids, MessageFlags.Seen, true, t).ConfigureAwait(false);
                if (flagged is true) await folder.AddFlagsAsync(ids, MessageFlags.Flagged, true, t).ConfigureAwait(false);
                if (flagged is false) await folder.RemoveFlagsAsync(ids, MessageFlags.Flagged, true, t).ConfigureAwait(false);
                return null;
            }
            finally
            {
                if (folder.IsOpen) await folder.CloseAsync(false, t).ConfigureAwait(false);
            }
        }, ct);

    public Task<IReadOnlyList<uint>> MoveAsync(string folderName, IReadOnlyList<uint> uids, string targetFolder, CancellationToken ct)
        => RunAsync<IReadOnlyList<uint>>(async (c, t) =>
        {
            var source = await ResolveFolderAsync(c, folderName, t).ConfigureAwait(false);
            var target = await ResolveFolderAsync(c, targetFolder, t).ConfigureAwait(false);
            await source.OpenAsync(FolderAccess.ReadWrite, t).ConfigureAwait(false);
            try
            {
                var ids = uids.Select(u => new UniqueId(u)).ToList();
                var map = await source.MoveToAsync(ids, target, t).ConfigureAwait(false);
                return map.Destination.Select(u => u.Id).ToList();
            }
            finally
            {
                if (source.IsOpen) await source.CloseAsync(true, t).ConfigureAwait(false);
            }
        }, ct);

    /// <summary>Appends a message to a special folder (Drafts or Sent). Returns the new UID when the server reports it.</summary>
    public Task<uint?> AppendAsync(SpecialFolder special, MimeMessage message, MessageFlags flags, CancellationToken ct)
        => RunAsync<uint?>(async (c, t) =>
        {
            var folder = await ResolveFolderAsync(c, special.ToString(), t).ConfigureAwait(false);
            var uid = await folder.AppendAsync(message, flags, t).ConfigureAwait(false);
            return uid?.Id;
        }, ct);

    public async Task<AccountStatus> ProbeAsync(CancellationToken ct)
    {
        try
        {
            await RunAsync<object?>((_, _) => Task.FromResult<object?>(null), ct).ConfigureAwait(false);
            return Status(true, null);
        }
        catch (Exception ex)
        {
            return Status(false, ex.Message);
        }
    }

    public AccountStatus Status(bool connected, string? error) => new(
        _account.Id, _account.Preset.Name, _account.User, _account.Auth.ToString().ToLowerInvariant(),
        _account.ReadOnly, connected, error);

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { await ResetAsync().ConfigureAwait(false); }
        finally { _lock.Release(); _lock.Dispose(); }
    }
}
