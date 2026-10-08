using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Scheduler;

/// <summary>
/// Keeps every workspace's mail accounts in step with their servers (mail
/// spec, decision 14). Wakes every <see cref="Tick"/> or when an account asks
/// (<see cref="MailSyncQueue"/>); an account is due when it was asked for,
/// is new, still reads its history, or wasn't synced for
/// <c>Mail:PollMinutes</c> (default 5). Accounts run one after another; a
/// pass per account is bounded (<see cref="MailSyncer.BatchPerFolder"/>), so
/// a big history fills over several ticks without starving the others. When
/// an account starts failing, whoever added it gets one notification.
/// </summary>
public class MailSyncService : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    public const string PollMinutesConfig = "Mail:PollMinutes";
    public const int DefaultPollMinutes = 5;

    private readonly IServiceScopeFactory _scopes;
    private readonly DatabaseFactory _db;
    private readonly MailSyncQueue _queue;
    private readonly ILogger<MailSyncService> _logger;

    public MailSyncService(IServiceScopeFactory scopes, DatabaseFactory db, MailSyncQueue queue, ILogger<MailSyncService>? logger = null)
    {
        _scopes = scopes;
        _db = db;
        _queue = queue;
        _logger = logger ?? NullLogger<MailSyncService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await _queue.WaitAsync(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "Mail sync pass failed — will retry next tick"); }
            try { await _queue.WaitAsync(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass over every workspace that has a database. Returns how
    /// many accounts were synced. Visible for tests.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<MailRepository>();
        var syncer = sp.GetRequiredService<MailSyncer>();
        var system = sp.GetRequiredService<ISystemRepository>();
        var messages = sp.GetService<IMessageRepository>();
        var poll = TimeSpan.FromMinutes(int.TryParse(await system.GetConfigAsync(PollMinutesConfig, ct), out var m) && m >= 1 ? m : DefaultPollMinutes);

        var synced = 0;
        foreach (var ctx in Workspaces())
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<(MailAccount Account, byte[] Secret)> accounts;
            try { accounts = await repo.SyncAccountsAsync(ctx, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogDebug(ex, "Mail: workspace skipped"); continue; }

            foreach (var (account, secret) in accounts)
            {
                var asked = _queue.Take(account.Id);
                var due = asked || account.State == MailAccountStates.New || !account.BackfillDone
                    || account.LastSyncAt is not { } last || DateTime.UtcNow - last >= poll;
                // A failing account is retried at the poll interval, not every tick.
                if (account.State == MailAccountStates.Failed && !asked && account.LastSyncAt is { } failedAt
                    && DateTime.UtcNow - failedAt < poll) due = false;
                if (!due) continue;

                var result = await syncer.SyncAsync(ctx, account, secret, ct);
                synced++;
                if (!result.Ok && account.State != MailAccountStates.Failed && messages is not null && account.CreatedBy is { } owner)
                {
                    try
                    {
                        await messages.CreateAsync(new[] { owner }, MessageKinds.MailAccountFailed, "mail_account", account.Id,
                            JsonSerializer.Serialize(new { name = account.Name, error = result.Error, space = ctx.Type == ContextType.Space ? ctx.Id : null }), ct);
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Mail: failure notification not sent"); }
                }
            }
        }
        return synced;
    }

    /// <summary>Workspaces with a database on disk — the sync never creates one.</summary>
    private IEnumerable<ContextRef> Workspaces()
    {
        foreach (var (root, file, make) in new (string, string, Func<string, ContextRef>)[]
                 {
                     (_db.UsersRoot, "personal.db", ContextRef.User),
                     (_db.SpacesRoot, "space.db", ContextRef.Space),
                 })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || !File.Exists(Path.Combine(dir, file))) continue;
                yield return make(name);
            }
        }
    }
}
