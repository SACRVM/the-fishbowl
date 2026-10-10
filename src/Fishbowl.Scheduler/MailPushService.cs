using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Mail;
using Fishbowl.Mail.Models;
using Fishbowl.Mail.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Scheduler;

/// <summary>
/// New mail as it comes (mail spec, phase 4: IDLE). One connection per
/// account waits on its inbox; whenever the server says something changed
/// there — new mail, a delete, a flag — the account's sync is asked for at
/// once (<see cref="MailSyncQueue"/>) instead of at the next poll. A server
/// that can't push is left to the poll. The accounts are looked at every
/// <see cref="Recheck"/>: a new one gets a watcher, a removed or failing one
/// loses it, a changed password or server starts it again. A watcher whose
/// connection drops waits (30 s, doubling up to 10 min), connects again and
/// asks for a sync, since mail may have come meanwhile.
/// </summary>
public class MailPushService : BackgroundService
{
    public static readonly TimeSpan Recheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LastRetry = TimeSpan.FromMinutes(10);
    // A watch that ran this long before its link dropped starts the waits over.
    private static readonly TimeSpan Settled = TimeSpan.FromMinutes(5);

    private sealed record Watcher(string Key, CancellationTokenSource Stop, Task Run);

    private readonly IServiceScopeFactory _scopes;
    private readonly DatabaseFactory _db;
    private readonly MailSyncQueue _queue;
    private readonly IMailboxConnector _connector;
    private readonly ILogger<MailPushService> _logger;
    private readonly Dictionary<string, Watcher> _watchers = new();

    public MailPushService(IServiceScopeFactory scopes, DatabaseFactory db, MailSyncQueue queue, IMailboxConnector connector,
        ILogger<MailPushService>? logger = null)
    {
        _scopes = scopes;
        _db = db;
        _queue = queue;
        _connector = connector;
        _logger = logger ?? NullLogger<MailPushService>.Instance;
    }

    /// <summary>Accounts with a watcher now (a server that can't push keeps
    /// its finished one, so it isn't tried again every minute).</summary>
    public int Watching => _watchers.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RecheckAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Mail push: accounts not rechecked — again in a minute"); }
                await Task.Delay(Recheck, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await StopAllAsync(); }
    }

    /// <summary>Starts and stops watchers to match the accounts. Visible for tests.</summary>
    public async Task RecheckAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<MailRepository>();
        var wanted = new Dictionary<string, (ResolvedAccount Account, string Password, string Key)>();
        foreach (var ctx in MailSyncService.Workspaces(_db))
        {
            IReadOnlyList<(MailAccount Account, byte[] Secret)> accounts;
            try { accounts = await repo.SyncAccountsAsync(ctx, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogDebug(ex, "Mail push: workspace skipped"); continue; }
            foreach (var (account, secret) in accounts)
            {
                // A failing account comes back through the poll (its own retry).
                if (account.State == MailAccountStates.Failed) continue;
                if (await repo.PasswordAsync(account.Id, secret, ct) is not { } password) continue;
                var resolved = MailSyncer.Resolve(account);
                wanted[account.Id] = (resolved, password, MailboxPool.KeyFor(resolved, password));
            }
        }

        foreach (var (id, w) in _watchers.ToList())
        {
            if (wanted.TryGetValue(id, out var want) && want.Key == w.Key) continue;
            _watchers.Remove(id);
            await StopAsync(w);
        }
        foreach (var (id, want) in wanted)
        {
            if (_watchers.ContainsKey(id)) continue;
            var stop = new CancellationTokenSource();
            _watchers[id] = new Watcher(want.Key, stop, Task.Run(() => WatchAsync(id, want.Account, want.Password, stop.Token)));
        }
    }

    /// <summary>Stops every watcher (the host's stop; tests).</summary>
    public async Task StopAllAsync()
    {
        var all = _watchers.Values.ToList();
        _watchers.Clear();
        foreach (var w in all) await StopAsync(w);
    }

    private static async Task StopAsync(Watcher w)
    {
        await w.Stop.CancelAsync();
        try { await w.Run; } catch { /* it logged its own end */ }
        w.Stop.Dispose();
    }

    private async Task WatchAsync(string accountId, ResolvedAccount account, string password, CancellationToken ct)
    {
        var wait = FirstRetry;
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                await using var box = _connector.Open(account, password);
                var inbox = await box.RoleFolderAsync(MailRoles.Inbox, ct);
                // Nothing to wait on, or a server that can't push: the poll it is.
                if (inbox is null || !await box.WatchAsync(inbox, () => _queue.Request(accountId), ct)) return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (DateTime.UtcNow - started >= Settled) wait = FirstRetry;
                _logger.LogInformation("Mail push: account {AccountId} interrupted ({Code}), again in {Wait}", accountId, MailSyncer.ErrorCode(ex), wait);
            }
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { return; }
            wait = wait * 2 < LastRetry ? wait * 2 : LastRetry;
            // Back after a gap: what came meanwhile is fetched now.
            _queue.Request(accountId);
        }
    }
}
