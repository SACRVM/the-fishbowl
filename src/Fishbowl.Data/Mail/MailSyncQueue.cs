using System.Collections.Concurrent;

namespace Fishbowl.Data.Mail;

/// <summary>
/// "Sync this account now": an account just added, a password changed, the
/// app's refresh. The sync service wakes up at once and treats the account as
/// due. A singleton — the API writes, the service reads.
/// </summary>
public sealed class MailSyncQueue
{
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    public void Request(string accountId)
    {
        _pending[accountId] = 0;
        _signal.Release();
    }

    /// <summary>Takes the request for an account, if there was one.</summary>
    public bool Take(string accountId) => _pending.TryRemove(accountId, out _);

    public bool HasPending => !_pending.IsEmpty;

    /// <summary>Waits for a request or the timeout, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        await _signal.WaitAsync(timeout, ct);
        // Several requests at once wake one pass.
        while (_signal.CurrentCount > 0 && _signal.Wait(0)) { }
    }
}
