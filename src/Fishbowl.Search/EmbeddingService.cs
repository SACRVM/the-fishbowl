using Fishbowl.Core.Search;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Search;

// Facade over E5Pipeline with lazy, thread-safe init tied to the
// ModelDownloader's readiness signal. Register as a singleton in DI — the
// ONNX session is heavy; reuse it for the app lifetime.
public sealed class EmbeddingService : IEmbeddingService, IDisposable
{
    private readonly ModelDownloader _downloader;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly Lock _gate = new();
    private E5Pipeline? _pipeline;
    private bool _disposed;

    public EmbeddingService(ModelDownloader downloader, ILogger<EmbeddingService>? logger = null)
    {
        _downloader = downloader;
        _logger = logger ?? NullLogger<EmbeddingService>.Instance;
    }

    public int Dimensions => E5Pipeline.EmbeddingDim;

    public string Model => ModelDownloader.ModelName;

    // e5 was trained with the two sides marked: what is searched is a
    // "passage", what someone searches with a "query". ORT's Run() is
    // synchronous but thread-safe; the Tasks come back completed.
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(GetOrInit().Embed("passage: " + text));
    }

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(GetOrInit().Embed("query: " + query));
    }

    public Task<IReadOnlyList<float[]>> EmbedManyAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pipeline = GetOrInit();
        return Task.FromResult<IReadOnlyList<float[]>>(pipeline.Embed(texts.Select(t => "passage: " + t).ToList()));
    }

    private E5Pipeline GetOrInit()
    {
        var p = _pipeline;
        if (p is not null) return p;

        lock (_gate)
        {
            if (_pipeline is not null) return _pipeline;

            if (!_downloader.IsReady())
            {
                throw new EmbeddingUnavailableException(
                    "The embedding model isn't on disk yet. The first-run " +
                    "download is still in progress or previously failed.");
            }

            try
            {
                _pipeline = new E5Pipeline(_downloader.ModelPath, _downloader.TokenizerPath);
                _logger.LogInformation("Embedding model initialised from {Dir}",
                    Path.GetDirectoryName(_downloader.ModelPath));
                return _pipeline;
            }
            catch (Exception ex)
            {
                throw new EmbeddingUnavailableException(
                    "Failed to initialise the embedding model — see inner exception", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _pipeline?.Dispose();
        _disposed = true;
    }
}

// IHostedService that kicks off the first-run download in the background so
// the rest of the app can start immediately. If the download fails we log it
// and stay silent — callers on the hot path catch EmbeddingUnavailableException
// and degrade gracefully (FTS-only search, skipped embedding on note writes).
// A subsequent restart retries.
public sealed class EmbeddingInitializer : IHostedService
{
    private readonly ModelDownloader _downloader;
    private readonly ILogger<EmbeddingInitializer> _logger;
    private Task? _runner;
    private CancellationTokenSource? _cts;

    public EmbeddingInitializer(ModelDownloader downloader, ILogger<EmbeddingInitializer>? logger = null)
    {
        _downloader = downloader;
        _logger = logger ?? NullLogger<EmbeddingInitializer>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Detached on purpose — blocking startup on a 470 MB download over
        // residential bandwidth would make the app look hung. Progress
        // surfaces through ModelDownloader's own logging.
        _runner = Task.Run(async () =>
        {
            try
            {
                // Also when the files are there: EnsureModelAsync checks the
                // pinned hash (once per start, ~470 MB read in the
                // background) and fetches a truncated or damaged model again
                // — present alone isn't whole (a full disk, a copy that
                // stopped, a file from before downloads went through .part).
                await _downloader.EnsureModelAsync(_cts.Token);
            }
            catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
            {
                _logger.LogInformation("Embedding model download cancelled during shutdown");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding model download failed — search will run in FTS-only mode until retried");
            }
        }, _cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var cts = _cts;
        if (cts is null) return;
        _cts = null;

        // WebApplicationFactory in tests can call StopAsync twice via double
        // dispose, so null-out above is load-bearing — Cancel() on a disposed
        // token source throws ObjectDisposedException.
        try { cts.Cancel(); } catch (ObjectDisposedException) { }

        if (_runner is not null)
        {
            try { await _runner.WaitAsync(cancellationToken); } catch { /* best-effort */ }
        }
        cts.Dispose();
    }
}
