using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Scheduler;

// The Files app's daily tick (spec § Change journal, § Trash, § Streaming):
// for every context folder that has a files/ tree — sweep uploads killed
// mid-flight, purge trash past Files:TrashRetentionDays, compact the journal
// past Files:JournalRetentionDays, reconcile the whole tree so out-of-band
// changes reach the feed without a client asking; then purge archived
// spaces past Archive:RetentionDays. Contexts are found on
// disk (users/*/files, spaces/*/files), so a cold-imported folder is picked
// up too.
public class FilesMaintenanceService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopes;
    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<FilesMaintenanceService> _logger;
    private readonly SchedulerStatus? _status;

    public FilesMaintenanceService(IServiceScopeFactory scopes, DatabaseFactory dbFactory,
        ILogger<FilesMaintenanceService>? logger = null, SchedulerStatus? status = null)
    {
        _scopes = scopes;
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<FilesMaintenanceService>.Instance;
        _status = status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Files maintenance failed — will retry next interval"); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // The record trash (deleted notes, todos, events, contacts) keeps the
    // same retention as the file trash — one trash, one rule.
    private async Task PurgeRecordTrashAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var system = sp.GetRequiredService<ISystemRepository>();
            var raw = await system.GetConfigAsync(FileLimits.TrashRetentionDaysKey, ct);
            var days = long.TryParse(raw, out var d) ? d : FileLimits.DefaultTrashRetentionDays;
            if (days <= 0) return;   // 0 = keep forever
            var cutoff = DateTime.UtcNow.AddDays(-days);
            var trash = sp.GetRequiredService<ITrashRepository>();
            var purged = 0;
            foreach (var (root, file, make) in new (string, string, Func<string, ContextRef>)[]
                     {
                         (_dbFactory.UsersRoot, "personal.db", ContextRef.User),
                         (_dbFactory.SpacesRoot, "space.db", ContextRef.Space),
                     })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Path.GetFileName(dir).StartsWith('.') || !File.Exists(Path.Combine(dir, file))) continue;
                    try { purged += await trash.PurgeAsync(make(Path.GetFileName(dir)), cutoff, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _logger.LogDebug(ex, "Trash purge skipped {Dir}", Path.GetFileName(dir)); }
                }
            }
            if (purged > 0) _logger.LogInformation("Purged {Count} items from the record trash", purged);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Record trash purge failed — will retry next interval"); }
    }

    // Visible for tests. Returns how many contexts were maintained.
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var contexts = new List<ContextRef>();
        foreach (var (root, make) in new (string, Func<string, ContextRef>)[]
                 { (_dbFactory.UsersRoot, ContextRef.User), (_dbFactory.SpacesRoot, ContextRef.Space) })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
                // .deleted-/.restoring- folders aren't workspaces (the archiver sweeps them).
                if (!Path.GetFileName(dir).StartsWith('.') && Directory.Exists(Path.Combine(dir, "files")))
                    contexts.Add(make(Path.GetFileName(dir)));
        }

        var done = 0;
        foreach (var ctx in contexts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IFileService>().RunMaintenanceAsync(ctx, ct);
                done++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Files maintenance skipped {CtxType}:{CtxId}", ctx.Type, ctx.Id);
            }
        }
        if (done > 0) _logger.LogInformation("Files maintenance ran for {Count} workspaces", done);

        await PurgeRecordTrashAsync(ct);

        // Archived spaces and accounts past Archive:RetentionDays, and
        // leftover folders.
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISpaceArchiveService>().PurgeAsync(ct);
            var users = scope.ServiceProvider.GetService<IUserArchiveService>();
            if (users is not null) await users.PurgeAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Archive purge failed — will retry next interval"); }
        _status?.MarkMaintenance(DateTime.UtcNow);
        return done;
    }
}
