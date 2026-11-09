using Fishbowl.Core;
using Fishbowl.Data;
using Fishbowl.Data.Photos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Scheduler;

// Keeps every workspace's photos in step with its Photos/ folder
// (PhotoIndexer) and makes their previews (PhotoPreviews), the newest
// first. Workspaces take turns — a share of previews each pass — and while
// any has previews left the next pass starts at once; then it rests.
public class PhotosService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(20);
    public const int PreviewsPerPass = 48;

    private readonly IServiceScopeFactory _scopes;
    private readonly DatabaseFactory _db;
    private readonly ILogger<PhotosService> _logger;

    public PhotosService(IServiceScopeFactory scopes, DatabaseFactory db, ILogger<PhotosService>? logger = null)
    {
        _scopes = scopes;
        _db = db;
        _logger = logger ?? NullLogger<PhotosService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            var busy = false;
            try { busy = await RunOnceAsync(stoppingToken) > 0; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "Photos pass failed — will retry"); }
            if (busy) continue;
            try { await Task.Delay(Idle, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass over every workspace; how many previews it made.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var made = 0;
        foreach (var ctx in MailSyncService.Workspaces(_db))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var scope = _scopes.CreateScope();
                var seen = await scope.ServiceProvider.GetRequiredService<PhotoIndexer>().RunAsync(ctx, ct);
                if (seen > 0) _logger.LogDebug("Indexed {Count} photo files in {CtxType}:{CtxId}", seen, ctx.Type, ctx.Id);
                made += await scope.ServiceProvider.GetRequiredService<PhotoPreviews>().RunAsync(ctx, PreviewsPerPass, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Fishbowl.Core.Auth.InactiveAccountException) { }   // a pending or disabled account's folder
            catch (Exception ex) { _logger.LogWarning(ex, "Photos: workspace {CtxType}:{CtxId} skipped this pass", ctx.Type, ctx.Id); }
        }
        return made;
    }
}
