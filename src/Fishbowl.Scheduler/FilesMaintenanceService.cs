using Fishbowl.Core;
using Fishbowl.Core.Files;
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
// changes reach the feed without a client asking. Contexts are found on
// disk (users/*/files, spaces/*/files), so a cold-imported folder is picked
// up too.
public class FilesMaintenanceService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopes;
    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<FilesMaintenanceService> _logger;

    public FilesMaintenanceService(IServiceScopeFactory scopes, DatabaseFactory dbFactory,
        ILogger<FilesMaintenanceService>? logger = null)
    {
        _scopes = scopes;
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<FilesMaintenanceService>.Instance;
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

    // Visible for tests. Returns how many contexts were maintained.
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var contexts = new List<ContextRef>();
        foreach (var (root, make) in new (string, Func<string, ContextRef>)[]
                 { (_dbFactory.UsersRoot, ContextRef.User), (_dbFactory.SpacesRoot, ContextRef.Space) })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (Directory.Exists(Path.Combine(dir, "files")))
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
        return done;
    }
}
