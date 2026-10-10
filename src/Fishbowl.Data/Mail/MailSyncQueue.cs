using System.Collections.Concurrent;
using Fishbowl.Core;

namespace Fishbowl.Data.Mail;

/// <summary>
/// "Sync this account now": an account just added, a password changed, the
/// app's refresh, the server's push (IDLE). The sync service wakes up at once
/// and treats the account as due. A singleton — the API and the push write,
/// the service reads. It also keeps each workspace's stamp, which moves on
/// when a pass changed its mail, so the app knows when to look again.
/// </summary>
public sealed class MailSyncQueue
{
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly ConcurrentDictionary<string, long> _stamps = new();
    // A stamp from before a restart never matches one after it.
    private readonly long _epoch = DateTime.UtcNow.Ticks;

    public void Request(string accountId)
    {
        _pending[accountId] = 0;
        _signal.Release();
    }

    /// <summary>Takes the request for an account, if there was one.</summary>
    public bool Take(string accountId) => _pending.TryRemove(accountId, out _);

    public bool HasPending => !_pending.IsEmpty;

    /// <summary>A sync pass changed the workspace's mail: its stamp moves on.</summary>
    public void Changed(ContextRef ctx) => _stamps.AddOrUpdate(Key(ctx), 1, (_, n) => n + 1);

    /// <summary>What the app compares to know whether to look again (in memory, cheap).</summary>
    public long Stamp(ContextRef ctx) => _epoch + (_stamps.TryGetValue(Key(ctx), out var n) ? n : 0);

    private static string Key(ContextRef ctx) => $"{ctx.Type}:{ctx.Id}";

    /// <summary>Waits for a request or the timeout, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        await _signal.WaitAsync(timeout, ct);
        // Several requests at once wake one pass.
        while (_signal.CurrentCount > 0 && _signal.Wait(0)) { }
    }
}
