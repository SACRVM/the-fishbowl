using System.Collections.Concurrent;
using System.Text;
using Fishbowl.Mail;
using Fishbowl.Mail.Models;
using Fishbowl.Mail.Sync;

namespace Fishbowl.Host.Testing;

/// <summary>
/// The Playwright fixture's mail server (registered ONLY behind the same
/// double gate as the test sign-in: the Testing environment and
/// FISHBOWL_PLAYWRIGHT_TEST). An account whose IMAP host ends in ".test"
/// reads an in-memory mailbox seeded with a few conversations — a thread in
/// both directions, an HTML newsletter with a tracking image, an invoice with
/// a PDF, archived mail — so the Mail app can be driven end to end; any other
/// host goes to the real IMAP connector. The password is "test-password";
/// anything else is refused like a real server would.
/// </summary>
public sealed class SampleMailboxConnector : IMailboxConnector
{
    public const string Password = "test-password";
    private readonly ImapMailboxConnector _real;
    private static readonly ConcurrentDictionary<string, SampleMailbox.Store> Stores = new(StringComparer.OrdinalIgnoreCase);

    public SampleMailboxConnector(ImapMailboxConnector real) => _real = real;

    public IMailbox Open(ResolvedAccount account, string password)
    {
        if (!account.ImapHost.EndsWith(".test", StringComparison.OrdinalIgnoreCase)) return _real.Open(account, password);
        if (password != Password) return new SampleMailbox(null);
        return new SampleMailbox(Stores.GetOrAdd(account.Address, a => SampleMailbox.Store.Seed(a)));
    }
}

internal sealed class SampleMailbox : IMailbox
{
    internal sealed record Message(uint Uid, SyncHeader Header, SyncBody Body, byte[]? Attachment)
    {
        public bool Seen { get; set; } = Header.Seen;
    }

    internal sealed class Store
    {
        public readonly Dictionary<string, List<Message>> Folders = new()
        {
            ["INBOX"] = new(),
            ["Sent Messages"] = new(),
            ["Archive"] = new(),
        };
        public readonly object Gate = new();

        public static Store Seed(string me)
        {
            var s = new Store();
            var now = DateTimeOffset.UtcNow;
            var ada = new MailAddress("Ada Lovelace", "ada@example.org");
            var self = new MailAddress("Playwright", me);
            uint uid = 1;
            void Add(string folder, string id, string? inReplyTo, string[] refs, MailAddress from, MailAddress to, string subject,
                TimeSpan ago, bool seen, string? text, string? html = null, (string Name, string Type, byte[] Bytes)? file = null)
            {
                var header = new SyncHeader(uid, id, inReplyTo, refs, now - ago, now - ago, from, [to], [], [], [], subject, seen, false,
                    (text?.Length ?? 0) + (html?.Length ?? 0), null);
                var attachments = file is { } f ? new List<SyncAttachment> { new(0, "2", f.Name, f.Type, f.Bytes.Length) } : new List<SyncAttachment>();
                s.Folders[folder].Add(new Message(uid++, header, new SyncBody(text, html, attachments), file?.Bytes));
            }
            Add("INBOX", "engine-1@example.org", null, [], ada, self, "Analytical engine notes", TimeSpan.FromDays(2), true,
                "Here are my notes on the engine.\n\nThe operations cards come first.");
            Add("Sent Messages", "engine-2@fishbowl.test", "engine-1@example.org", ["engine-1@example.org"], self, ada,
                "Re: Analytical engine notes", TimeSpan.FromDays(1), true, "Thank you — what about the variables?");
            Add("INBOX", "engine-3@example.org", "engine-2@fishbowl.test", ["engine-1@example.org", "engine-2@fishbowl.test"], ada, self,
                "Re: Analytical engine notes", TimeSpan.FromHours(3), false, "The variables live in the store, column by column.");
            Add("INBOX", "news-1@example.com", null, [], new MailAddress("Weekly News", "news@example.com"), self, "This week in engines",
                TimeSpan.FromHours(5), false, null,
                "<h1>This week</h1><p>Engines everywhere.</p><img src=\"https://example.com/pixel.png\" width=\"1\" height=\"1\">");
            Add("INBOX", "invoice-1@example.net", null, [], new MailAddress("Billing", "billing@example.net"), self, "Your invoice",
                TimeSpan.FromDays(1), true, "Your invoice is attached.",
                file: ("invoice.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n% sample invoice\n")));
            Add("Archive", "old-1@example.org", null, [], new MailAddress("Grace Hopper", "grace@example.org"), self, "Old project",
                TimeSpan.FromDays(40), true, "The old project is done.");
            for (var i = 1; i <= 30; i++)
                Add("Archive", $"digest-{i}@example.com", null, [], new MailAddress("Digest", "digest@example.com"), self, $"Digest #{i}",
                    TimeSpan.FromDays(60 + i), true, $"Digest number {i}.");
            return s;
        }
    }

    private readonly Store? _store;

    public SampleMailbox(Store? store) => _store = store;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Store Data => _store ?? throw new MailKit.Security.AuthenticationException("Authentication failed.");

    private List<Message> Folder(string name) =>
        Data.Folders.TryGetValue(name, out var f) ? f : throw new MailException($"Folder '{name}' not found.");

    public Task<IReadOnlyList<FolderInfo>> ListFoldersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FolderInfo>>(Data.Folders.Keys.Select(k => new FolderInfo(k, k, null, null, null)).ToList());

    public Task<string?> RoleFolderAsync(string role, CancellationToken ct)
    {
        _ = Data;
        return Task.FromResult<string?>(role switch { "inbox" => "INBOX", "sent" => "Sent Messages", "archive" => "Archive", _ => null });
    }

    public Task<SyncFolderStatus> StatusAsync(string folder, CancellationToken ct)
    {
        lock (Data.Gate)
        {
            var f = Folder(folder);
            return Task.FromResult(new SyncFolderStatus(1, f.Count == 0 ? 1 : f.Max(m => m.Uid) + 1, null, f.Count));
        }
    }

    public Task<IReadOnlyList<uint>> UidsAsync(string folder, CancellationToken ct)
    {
        lock (Data.Gate) return Task.FromResult<IReadOnlyList<uint>>(Folder(folder).Select(m => m.Uid).ToList());
    }

    public Task<IReadOnlyList<SyncHeader>> HeadersAsync(string folder, IReadOnlyList<uint> uids, CancellationToken ct)
    {
        lock (Data.Gate)
            return Task.FromResult<IReadOnlyList<SyncHeader>>(Folder(folder).Where(m => uids.Contains(m.Uid))
                .Select(m => m.Header with { Seen = m.Seen }).ToList());
    }

    public Task<SyncBody> BodyAsync(string folder, uint uid, CancellationToken ct)
    {
        lock (Data.Gate) return Task.FromResult(Folder(folder).First(m => m.Uid == uid).Body);
    }

    public Task<IReadOnlyList<SyncFlags>> FlagsAsync(string folder, ulong? changedSince, CancellationToken ct)
    {
        lock (Data.Gate)
            return Task.FromResult<IReadOnlyList<SyncFlags>>(Folder(folder).Select(m => new SyncFlags(m.Uid, m.Seen, false)).ToList());
    }

    public async Task AttachmentAsync(string folder, uint uid, string part, Stream target, CancellationToken ct)
    {
        byte[]? bytes;
        lock (Data.Gate) bytes = Folder(folder).First(m => m.Uid == uid).Attachment;
        if (bytes is null) throw new MailException("No such part.");
        await target.WriteAsync(bytes, ct);
    }

    public Task MarkAsync(string folder, IReadOnlyList<uint> uids, bool? seen, bool? flagged, CancellationToken ct)
    {
        lock (Data.Gate)
            foreach (var m in Folder(folder).Where(m => uids.Contains(m.Uid)))
                if (seen is { } s) m.Seen = s;
        return Task.CompletedTask;
    }
}
