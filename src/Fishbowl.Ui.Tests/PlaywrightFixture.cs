using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

/// <summary>
/// Launches Fishbowl.Host as a subprocess on a free port and a headless
/// Chromium so the Playwright smoke test can hit real HTTP. Installs
/// Chromium on first use.
/// </summary>
public class PlaywrightFixture : IAsyncLifetime
{
    private Process? _hostProcess;
    // Fresh data dir per fixture (CLAUDE.md § Testing). Without it the host
    // used fishbowl-data/ under the test runner's working directory, which
    // outlived the run — a vault or notes from one run leaked into the next.
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_ui_" + Path.GetRandomFileName());

    // The host fetches the ~90 MB embedding model into <data>/models on
    // first start. A fresh data dir per run would re-download it every time,
    // so a completed download is kept here and seeded into the next run.
    private static readonly string ModelCache = Path.Combine(Path.GetTempPath(), "fishbowl_ui_model_cache");
    public string BaseUrl { get; private set; } = string.Empty;
    public IPlaywright? Playwright { get; private set; }
    public IBrowser? Browser { get; private set; }

    public async ValueTask InitializeAsync()
    {
        // Install chromium (no-op if already cached)
        var installExit = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        if (installExit != 0)
            throw new InvalidOperationException($"Playwright chromium install failed (exit {installExit})");

        // Pick a free loopback port
        var port = FindFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";

        // Launch Fishbowl.Host directly via the compiled dll, bypassing launchSettings.json profiles.
        // The path resolves from bin/Debug/net10.0 up to src, then down into Fishbowl.Host.
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var hostDll = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Fishbowl.Host", "bin", configuration, "net10.0", "Fishbowl.Host.dll"));

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{hostDll}\" --urls {BaseUrl} --data \"{_dataDir}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        // This is the magic marker that enables auto-auth in the Host's middleware
        psi.Environment["FISHBOWL_PLAYWRIGHT_TEST"] = "true";

        if (Directory.Exists(ModelCache)) CopyDirectory(ModelCache, Path.Combine(_dataDir, "models"));

        _hostProcess = new Process { StartInfo = psi };
        _hostProcess.Start();

        // Wait up to 60s for the host to respond
        await WaitForHttpReady(BaseUrl + "/api/v1/version", TimeSpan.FromSeconds(60));

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser != null) await Browser.CloseAsync();
        if (Playwright != null) Playwright.Dispose();
        if (_hostProcess != null && !_hostProcess.HasExited)
        {
            _hostProcess.Kill(entireProcessTree: true);
            await _hostProcess.WaitForExitAsync();
            _hostProcess.Dispose();
        }
        // Refresh the cache when this run's model is newer than the cached
        // one — i.e. the host downloaded it because the cache was missing or
        // failed ModelDownloader's SHA-256 check. A half-finished download
        // that lands here fails that check next run and heals the same way.
        var models = Path.Combine(_dataDir, "models");
        var model = Directory.Exists(models)
            ? Directory.EnumerateFiles(models, "model.onnx", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        var cached = Directory.Exists(ModelCache)
            ? Directory.EnumerateFiles(ModelCache, "model.onnx", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (model is not null && Directory.EnumerateFiles(models, "vocab.txt", SearchOption.AllDirectories).Any()
            && (cached is null || File.GetLastWriteTimeUtc(model) > File.GetLastWriteTimeUtc(cached)))
        {
            try { CopyDirectory(models, ModelCache); }
            catch (IOException) { /* next run downloads again */ }
        }
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { /* a straggling file handle — the OS temp cleaner gets it */ }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForHttpReady(string url, TimeSpan timeout)
    {
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var res = await http.GetAsync(url);
                if (res.IsSuccessStatusCode) return;
            }
            catch (Exception ex) { last = ex; }
            await Task.Delay(500);
        }
        throw new TimeoutException($"Server at {url} did not become ready within {timeout}. Last error: {last?.Message}");
    }
}
