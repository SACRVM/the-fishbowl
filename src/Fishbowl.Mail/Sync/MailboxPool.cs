using System.Security.Cryptography;
using System.Text;
using Fishbowl.Mail.Models;

namespace Fishbowl.Mail.Sync;

/// <summary>
/// Keeps an account's signed-in mailbox for a while after it was used, so the
/// next action takes it as it is instead of connecting, negotiating TLS and
/// signing in again — seconds at some providers (iCloud), for a command that
/// takes milliseconds. A lease is exclusive (a connection does one thing at a
/// time): a second one at the same moment opens its own, and when both come
/// back one stays. What stays idle longer than <see cref="IdleFor"/> is
/// closed; a key that changed (another password, other servers) never meets
/// the old connection. A link the server dropped meanwhile is the session's
/// to mend — it reconnects once by itself.
/// </summary>
public sealed class MailboxPool : IAsyncDisposable
{
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromMinutes(2);

    private sealed record Idle(string Key, IMailbox Box, DateTime Since);

    private readonly Dictionary<string, Idle> _idle = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private readonly Timer? _sweeper;
    private bool _disposed;

    public TimeSpan IdleFor { get; }

    /// <param name="sweep">Close idle connections on a timer; tests call <see cref="Sweep"/> themselves.</param>
    public MailboxPool(TimeSpan? idleFor = null, Func<DateTime>? clock = null, bool sweep = true)
    {
        IdleFor = idleFor ?? DefaultIdle;
        _clock = clock ?? (() => DateTime.UtcNow);
        if (sweep) _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>What makes two connections interchangeable: the servers, the
    /// sign-in and the sending side — hashed, never kept as text.</summary>
    public static string KeyFor(ResolvedAccount a, string password)
    {
        var text = string.Join('\n', a.ImapHost, a.ImapPort, a.ImapSecurity, a.SmtpHost, a.SmtpPort, a.SmtpSecurity, a.Auth, a.User, a.Address, password);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The account's idle mailbox when its key matches, else a new
    /// one from <paramref name="open"/>; disposing the lease hands it back.</summary>
    public IMailbox Lease(string accountId, string key, Func<IMailbox> open)
    {
        IMailbox? stale = null;
        IMailbox? box = null;
        lock (_gate)
        {
            if (_idle.Remove(accountId, out var idle))
            {
                if (idle.Key == key && _clock() - idle.Since < IdleFor) box = idle.Box;
                else stale = idle.Box;
            }
        }
        Close(stale);
        return new Leased(this, accountId, key, box ?? open());
    }

    /// <summary>Closes what has been idle for <see cref="IdleFor"/> or longer.</summary>
    public void Sweep()
    {
        List<IMailbox> old;
        lock (_gate)
        {
            var now = _clock();
            var gone = _idle.Where(x => now - x.Value.Since >= IdleFor).ToList();
            foreach (var x in gone) _idle.Remove(x.Key);
            old = gone.Select(x => x.Value.Box).ToList();
        }
        foreach (var box in old) Close(box);
    }

    /// <summary>How many connections wait to be used again.</summary>
    public int IdleCount { get { lock (_gate) return _idle.Count; } }

    private void Return(string accountId, string key, IMailbox box)
    {
        IMailbox? close = box;
        lock (_gate)
        {
            if (!_disposed && !_idle.ContainsKey(accountId))
            {
                _idle[accountId] = new Idle(key, box, _clock());
                close = null;
            }
        }
        Close(close);
    }

    // Disconnecting is a network round trip: never under the lock, never awaited by the caller.
    private static void Close(IMailbox? box)
    {
        if (box is null) return;
        _ = Task.Run(async () =>
        {
            try { await box.DisposeAsync().ConfigureAwait(false); }
            catch { /* the server is gone anyway */ }
        });
    }

    public async ValueTask DisposeAsync()
    {
        List<IMailbox> all;
        lock (_gate)
        {
            _disposed = true;
            all = _idle.Values.Select(x => x.Box).ToList();
            _idle.Clear();
        }
        if (_sweeper is not null) await _sweeper.DisposeAsync().ConfigureAwait(false);
        foreach (var box in all)
        {
            try { await box.DisposeAsync().ConfigureAwait(false); }
            catch { /* shutting down */ }
        }
    }

    /// <summary>A lease: every call goes to the pooled mailbox; disposing
    /// hands it back instead of closing it.</summary>
    private sealed class Leased(MailboxPool pool, string accountId, string key, IMailbox box) : IMailbox
    {
        private int _returned;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0) pool.Return(accountId, key, box);
            return ValueTask.CompletedTask;
        }

        public Task<IReadOnlyList<FolderInfo>> ListFoldersAsync(CancellationToken ct) => box.ListFoldersAsync(ct);
        public Task<string?> RoleFolderAsync(string role, CancellationToken ct) => box.RoleFolderAsync(role, ct);
        public Task<SyncFolderStatus> StatusAsync(string folder, CancellationToken ct) => box.StatusAsync(folder, ct);
        public Task<IReadOnlyList<uint>> UidsAsync(string folder, CancellationToken ct) => box.UidsAsync(folder, ct);
        public Task<IReadOnlyList<SyncHeader>> HeadersAsync(string folder, IReadOnlyList<uint> uids, CancellationToken ct) => box.HeadersAsync(folder, uids, ct);
        public Task<SyncBody> BodyAsync(string folder, uint uid, CancellationToken ct) => box.BodyAsync(folder, uid, ct);
        public Task<IReadOnlyList<SyncFlags>> FlagsAsync(string folder, ulong? changedSince, CancellationToken ct) => box.FlagsAsync(folder, changedSince, ct);
        public Task AttachmentAsync(string folder, uint uid, string part, Stream target, CancellationToken ct) => box.AttachmentAsync(folder, uid, part, target, ct);
        public Task<string?> InlineAsync(string folder, uint uid, string contentId, Stream target, CancellationToken ct) => box.InlineAsync(folder, uid, contentId, target, ct);
        public Task MarkAsync(string folder, IReadOnlyList<uint> uids, bool? seen, bool? flagged, CancellationToken ct) => box.MarkAsync(folder, uids, seen, flagged, ct);
        public Task TrashAsync(string folder, IReadOnlyList<uint> uids, CancellationToken ct) => box.TrashAsync(folder, uids, ct);
        public Task<MailTransfer> ArchiveAsync(string inbox, IReadOnlyList<uint> uids, string archive, CancellationToken ct) => box.ArchiveAsync(inbox, uids, archive, ct);
        public Task<MailTransfer> UnarchiveAsync(string archive, IReadOnlyList<uint> uids, string inbox, CancellationToken ct) => box.UnarchiveAsync(archive, uids, inbox, ct);
        public Task<MailTransfer> MoveAsync(string folder, IReadOnlyList<uint> uids, string target, CancellationToken ct) => box.MoveAsync(folder, uids, target, ct);
        public Task<uint?> SendAsync(MimeKit.MimeMessage message, string? sent, CancellationToken ct) => box.SendAsync(message, sent, ct);
    }
}
