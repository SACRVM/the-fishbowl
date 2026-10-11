using Fishbowl.Core;
using Fishbowl.Data;
using Fishbowl.Data.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Scheduler;

// Makes the vectors search finds by meaning with (VectorBackfill): a
// workspace embedded with another model is embedded again, and mail gets a
// vector after it was stored. Workspaces take turns — each gets a share a
// pass, so a mailbox of tens of thousands doesn't keep the next one waiting —
// and while any has work left the next pass starts at once; then it rests.
public class EmbeddingBackfillService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(30);
    // About a minute of the model's time on a laptop.
    public const int MailPerPass = 256;

    private readonly IServiceScopeFactory _scopes;
    private readonly DatabaseFactory _db;
    private readonly ILogger<EmbeddingBackfillService> _logger;

    public EmbeddingBackfillService(IServiceScopeFactory scopes, DatabaseFactory db, ILogger<EmbeddingBackfillService>? logger = null)
    {
        _scopes = scopes;
        _db = db;
        _logger = logger ?? NullLogger<EmbeddingBackfillService>.Instance;
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
            catch (Exception ex) { _logger.LogWarning(ex, "Embedding pass failed — will retry"); }
            if (busy) continue;
            try { await Task.Delay(Idle, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass over every workspace; how many messages got a vector.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var total = 0;
        foreach (var ctx in MailSyncService.Workspaces(_db))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var scope = _scopes.CreateScope();
                var n = await scope.ServiceProvider.GetRequiredService<VectorBackfill>().RunAsync(ctx, MailPerPass, ct);
                if (n > 0) _logger.LogDebug("Embedded {Count} mail messages in {CtxType}:{CtxId}", n, ctx.Type, ctx.Id);
                total += n;
            }
            catch (OperationCanceledException) { throw; }
            catch (Fishbowl.Core.Auth.InactiveAccountException) { }   // a pending or disabled account's folder
            catch (Exception ex) { _logger.LogWarning(ex, "Embedding: workspace {CtxType}:{CtxId} skipped this pass", ctx.Type, ctx.Id); }
        }
        return total;
    }
}
