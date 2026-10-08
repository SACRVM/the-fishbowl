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

    /// <summary>What an account sent through its (sample) SMTP server, oldest first — for tests.</summary>
    public static IReadOnlyList<MimeKit.MimeMessage> Outbox(string address)
    {
        if (!Stores.TryGetValue(address, out var store)) return [];
        lock (store.Gate) return store.Outbox.ToList();
    }
}

internal sealed class SampleMailbox : IMailbox
{
    internal sealed record Message(uint Uid, SyncHeader Header, SyncBody Body, byte[]? Attachment)
    {
        public bool Seen { get; set; } = Header.Seen;
        public bool Flagged { get; set; } = Header.Flagged;
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
        public readonly List<Message> Trashed = new();   // the server's Trash (not synced)
        public uint NextUid = 1;                          // one counter over every folder, like a fresh UIDVALIDITY
        public readonly List<MimeKit.MimeMessage> Outbox = new();   // what went out over "SMTP"

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
                // Like real newsletters: a background of its own (on paper), body
                // margin forced to 0, a last block whose margin runs out of the
                // body — the frame must still fit it.
                "<style>body{margin:0 !important;padding:0}</style><h1>This week</h1><p>Engines everywhere.</p>" +
                "<table width=\"100%\" bgcolor=\"#f4f4f4\" style=\"margin-bottom:40px\"><tr><td>Footer</td></tr></table>" +
                "<div style=\"width:100vw;height:1px\"></div>" +
                "<img src=\"https://example.com/pixel.png\" width=\"1\" height=\"1\">");
            // A plain HTML mail as Outlook writes it — no backgrounds, its
            // signature's logo embedded as cid: — with a PDF attached.
            Add("INBOX", "invoice-1@example.net", null, [], new MailAddress("Billing", "billing@example.net"), self, "Your invoice",
                TimeSpan.FromDays(1), true, "Your invoice is attached.",
                "<p style=\"color:windowtext\">Your invoice is attached.</p><p><img src=\"cid:logo@billing\" alt=\"Billing\" width=\"8\" height=\"8\"></p>",
                file: ("invoice.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n% sample invoice\n")));
            // Designed for light and dark, as Revolut's are: a colour-scheme
            // declaration and dark rules that turn the text white.
            Add("Archive", "old-1@example.org", null, [], new MailAddress("Grace Hopper", "grace@example.org"), self, "Old project",
                TimeSpan.FromDays(40), true, "The old project is done.",
                "<meta name=\"color-scheme\" content=\"light dark\"><style>.card{background-color:#ffffff}.t{color:#191c1f}" +
                "@media (prefers-color-scheme: dark){.card{background-color:#262626 !important}.t{color:#ffffff !important}}</style>" +
                "<div class=\"card\"><p class=\"t\">The old project is done.</p></div>");
            for (var i = 1; i <= 30; i++)
                Add("Archive", $"digest-{i}@example.com", null, [], new MailAddress("Digest", "digest@example.com"), self, $"Digest #{i}",
                    TimeSpan.FromDays(60 + i), true, $"Digest number {i}.");
            s.NextUid = uid;
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
            return Task.FromResult<IReadOnlyList<SyncFlags>>(Folder(folder).Select(m => new SyncFlags(m.Uid, m.Seen, m.Flagged)).ToList());
    }

    public async Task AttachmentAsync(string folder, uint uid, string part, Stream target, CancellationToken ct)
    {
        byte[]? bytes;
        lock (Data.Gate) bytes = Folder(folder).First(m => m.Uid == uid).Attachment;
        if (bytes is null) throw new MailException("No such part.");
        await target.WriteAsync(bytes, ct);
    }

    // The one embedded part in the sample: an 8x8 PNG logo.
    private static readonly byte[] Logo = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR4nGNQcvmPFTEMLQkAqIZZQVz3JK0AAAAASUVORK5CYII=");

    public async Task<string?> InlineAsync(string folder, uint uid, string contentId, Stream target, CancellationToken ct)
    {
        lock (Data.Gate) _ = Folder(folder).First(m => m.Uid == uid);
        if (contentId != "logo@billing") return null;
        await target.WriteAsync(Logo, ct);
        return "image/png";
    }

    public Task MarkAsync(string folder, IReadOnlyList<uint> uids, bool? seen, bool? flagged, CancellationToken ct)
    {
        lock (Data.Gate)
            foreach (var m in Folder(folder).Where(m => uids.Contains(m.Uid)))
            {
                if (seen is { } s) m.Seen = s;
                if (flagged is { } f) m.Flagged = f;
            }
        return Task.CompletedTask;
    }

    public Task TrashAsync(string folder, IReadOnlyList<uint> uids, CancellationToken ct)
    {
        lock (Data.Gate)
        {
            var f = Folder(folder);
            Data.Trashed.AddRange(f.Where(m => uids.Contains(m.Uid)));
            f.RemoveAll(m => uids.Contains(m.Uid));
        }
        return Task.CompletedTask;
    }

    // SMTP as a server that files the copy itself would: out, and into Sent.
    public Task<uint?> SendAsync(MimeKit.MimeMessage message, string? sent, CancellationToken ct)
    {
        lock (Data.Gate)
        {
            Data.Outbox.Add(message);
            if (sent is null) return Task.FromResult<uint?>(null);
            static MailAddress Person(MimeKit.MailboxAddress m) => new(string.IsNullOrEmpty(m.Name) ? null : m.Name, m.Address);
            static List<MailAddress> People(MimeKit.InternetAddressList l) => l.Mailboxes.Select(Person).ToList();
            var uid = Data.NextUid++;
            var header = new SyncHeader(uid, message.MessageId, message.InReplyTo, message.References.ToList(), message.Date, message.Date,
                message.From.Mailboxes.Select(Person).FirstOrDefault(), People(message.To), People(message.Cc), People(message.Bcc), [],
                message.Subject, true, false, 1000, null);
            var attachments = message.Attachments.OfType<MimeKit.MimePart>()
                .Select((a, i) => new SyncAttachment(i, (i + 2).ToString(), a.FileName, a.ContentType.MimeType, null)).ToList();
            Folder(sent).Add(new Message(uid, header, new SyncBody(message.TextBody, message.HtmlBody, attachments), null));
            return Task.FromResult<uint?>(uid);
        }
    }

    public Task<MailTransfer> ArchiveAsync(string inbox, IReadOnlyList<uint> uids, string archive, CancellationToken ct) =>
        Task.FromResult(Move(inbox, uids, archive));

    public Task<MailTransfer> UnarchiveAsync(string archive, IReadOnlyList<uint> uids, string inbox, CancellationToken ct) =>
        Task.FromResult(Move(archive, uids, inbox));

    // A move as a UIDPLUS server makes it: a new UID in the target, reported.
    private MailTransfer Move(string from, IReadOnlyList<uint> uids, string to)
    {
        lock (Data.Gate)
        {
            var source = Folder(from);
            var target = Folder(to);
            var map = new Dictionary<uint, uint>();
            foreach (var m in source.Where(m => uids.Contains(m.Uid)).ToList())
            {
                var uid = Data.NextUid++;
                var moved = new Message(uid, m.Header with { Uid = uid }, m.Body, m.Attachment) { Seen = m.Seen, Flagged = m.Flagged };
                target.Add(moved);
                map[m.Uid] = moved.Uid;
                source.Remove(m);
            }
            return new MailTransfer(map, false);
        }
    }
}
