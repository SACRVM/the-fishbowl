using Fishbowl.Mail.Models;
using Fishbowl.Mail.Sync;

namespace Fishbowl.Tests.Mail;

/// <summary>One server's mailboxes in memory: folders by role, each with a UIDVALIDITY, UIDs and optional CONDSTORE.</summary>
public sealed class FakeServer
{
    public sealed class Msg
    {
        public required SyncHeader Header { get; set; }
        public SyncBody Body { get; set; } = new("body", null, []);
        public ulong ModSeq { get; set; }
    }

    public sealed class Folder
    {
        public required string Name { get; init; }
        public uint UidValidity { get; set; } = 1;
        public uint UidNext { get; set; } = 1;
        public bool Condstore { get; set; }
        public ulong ModSeq { get; set; } = 1;
        public SortedDictionary<uint, Msg> Messages { get; } = new();
    }

    public Dictionary<string, Folder> ByRole { get; } = new();

    public FakeServer(bool condstore = true)
    {
        Add("inbox", "INBOX", condstore);
        Add("sent", "Sent Messages", condstore);
        Add("archive", "Archive", condstore);
    }

    public Folder Add(string role, string name, bool condstore = true) => ByRole[role] = new Folder { Name = name, Condstore = condstore };
    public void RemoveRole(string role) => ByRole.Remove(role);

    private int _n;

    /// <summary>Adds a message with the next UID; returns the UID.</summary>
    public uint AddMessage(string role, string? messageId = null, string subject = "Hello", string from = "alice@example.com",
        string to = "me@example.com", DateTimeOffset? date = null, string? inReplyTo = null, string[]? references = null,
        bool seen = false, bool flagged = false, string? text = null, bool noMessageId = false)
    {
        var f = ByRole[role];
        var uid = f.UidNext++;
        _n++;
        var id = noMessageId ? null : messageId ?? $"<m{_n}@example.com>";
        var d = date ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(_n);
        f.ModSeq++;
        f.Messages[uid] = new Msg
        {
            Header = new SyncHeader(uid, id, inReplyTo, references ?? [], d, d, new MailAddress("Sender", from),
                [new MailAddress(null, to)], [], [], [], subject, seen, flagged, 100 + _n, null),
            Body = new SyncBody(text ?? $"text of {subject} #{_n}", null, []),
            ModSeq = f.ModSeq,
        };
        return uid;
    }

    /// <summary>Moves a message to another folder (new UID there); returns the new UID.</summary>
    public uint Move(string fromRole, uint uid, string toRole)
    {
        var src = ByRole[fromRole];
        var dst = ByRole[toRole];
        var m = src.Messages[uid];
        src.Messages.Remove(uid);
        src.ModSeq++;
        var nu = dst.UidNext++;
        dst.ModSeq++;
        dst.Messages[nu] = new Msg { Header = m.Header with { Uid = nu }, Body = m.Body, ModSeq = dst.ModSeq };
        return nu;
    }

    public void Delete(string role, uint uid)
    {
        var f = ByRole[role];
        f.Messages.Remove(uid);
        f.ModSeq++;
    }

    public void SetFlags(string role, uint uid, bool? seen = null, bool? flagged = null)
    {
        var f = ByRole[role];
        var m = f.Messages[uid];
        m.Header = m.Header with { Seen = seen ?? m.Header.Seen, Flagged = flagged ?? m.Header.Flagged };
        f.ModSeq++;
        m.ModSeq = f.ModSeq;
    }

    /// <summary>Renumbers the folder under a new UIDVALIDITY.</summary>
    public void ResetUidValidity(string role)
    {
        var f = ByRole[role];
        var old = f.Messages.Values.ToList();
        f.Messages.Clear();
        f.UidValidity++;
        f.UidNext = 1000;
        foreach (var m in old)
        {
            var nu = f.UidNext++;
            f.Messages[nu] = new Msg { Header = m.Header with { Uid = nu }, Body = m.Body, ModSeq = m.ModSeq };
        }
    }
}

public sealed class FakeMailbox : IMailbox
{
    private readonly FakeServer _server;
    private readonly FakeMailboxConnector _connector;

    public FakeMailbox(FakeServer server, FakeMailboxConnector connector)
    {
        _server = server;
        _connector = connector;
    }

    private FakeServer.Folder Find(string name)
    {
        _connector.Fail?.Invoke();
        return _server.ByRole.Values.Single(f => f.Name == name);
    }

    public Task<IReadOnlyList<FolderInfo>> ListFoldersAsync(CancellationToken ct)
    {
        _connector.Fail?.Invoke();
        return Task.FromResult<IReadOnlyList<FolderInfo>>(_server.ByRole.Values.Select(f => new FolderInfo(f.Name, f.Name, null, f.Messages.Count, null)).ToList());
    }

    public Task<string?> RoleFolderAsync(string role, CancellationToken ct)
    {
        _connector.Fail?.Invoke();
        return Task.FromResult(_server.ByRole.TryGetValue(role, out var f) ? f.Name : null);
    }

    public Task<SyncFolderStatus> StatusAsync(string folder, CancellationToken ct)
    {
        var f = Find(folder);
        return Task.FromResult(new SyncFolderStatus(f.UidValidity, f.UidNext, f.Condstore ? f.ModSeq : null, f.Messages.Count));
    }

    public Task<IReadOnlyList<uint>> UidsAsync(string folder, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<uint>>(Find(folder).Messages.Keys.ToList());

    public Task<IReadOnlyList<SyncHeader>> HeadersAsync(string folder, IReadOnlyList<uint> uids, CancellationToken ct)
    {
        var f = Find(folder);
        return Task.FromResult<IReadOnlyList<SyncHeader>>(uids.Where(f.Messages.ContainsKey).Select(u => f.Messages[u].Header).ToList());
    }

    public Task<SyncBody> BodyAsync(string folder, uint uid, CancellationToken ct) => Task.FromResult(Find(folder).Messages[uid].Body);

    public Task<IReadOnlyList<SyncFlags>> FlagsAsync(string folder, ulong? changedSince, CancellationToken ct)
    {
        var f = Find(folder);
        var list = f.Messages.Values.Where(m => changedSince is null || m.ModSeq > changedSince)
            .Select(m => new SyncFlags(m.Header.Uid, m.Header.Seen, m.Header.Flagged)).ToList();
        return Task.FromResult<IReadOnlyList<SyncFlags>>(list);
    }

    public Task AttachmentAsync(string folder, uint uid, string part, Stream target, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeMailboxConnector : IMailboxConnector
{
    private readonly Dictionary<string, FakeServer> _servers = new();

    public List<string> Passwords { get; } = new();
    /// <summary>Called on Open and on every mailbox call; throw from it to fail.</summary>
    public Action? Fail { get; set; }

    public FakeServer For(string accountId) => _servers.TryGetValue(accountId, out var s) ? s : _servers[accountId] = new FakeServer();
    public void Use(string accountId, FakeServer server) => _servers[accountId] = server;

    public IMailbox Open(ResolvedAccount account, string password)
    {
        Passwords.Add(password);
        Fail?.Invoke();
        return new FakeMailbox(For(account.Id), this);
    }
}
