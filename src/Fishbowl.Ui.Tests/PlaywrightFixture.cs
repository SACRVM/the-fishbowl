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

    public string BaseUrl { get; private set; } = string.Empty;

    // The host's data folder, for tests that seed state the UI can't create
    // itself (a pending account needs a Google sign-in).
    public string DataDir => _dataDir;
    public IPlaywright? Playwright { get; private set; }
    public IBrowser? Browser { get; private set; }

    public async ValueTask InitializeAsync()
    {
        // Playwright's Chromium, installed on first use (a no-op once cached) —
        // unless FISHBOWL_UI_TEST_CHROMIUM names a Chromium this machine has.
        var chromium = Environment.GetEnvironmentVariable("FISHBOWL_UI_TEST_CHROMIUM");
        if (string.IsNullOrEmpty(chromium))
        {
            var installExit = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            if (installExit != 0)
                throw new InvalidOperationException($"Playwright chromium install failed (exit {installExit})");
        }

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

        _hostProcess = new Process { StartInfo = psi };
        // Drain stdout/stderr continuously. Redirected but unread, the pipe
        // buffer fills up after enough logging and every host thread that
        // writes a log line blocks on it — requests hang for good, and which
        // one depends on how much the tests before it logged. The last lines
        // are kept for error messages.
        _hostProcess.OutputDataReceived += (_, e) => Remember(e.Data);
        _hostProcess.ErrorDataReceived += (_, e) => Remember(e.Data);
        _hostProcess.Start();
        _hostProcess.BeginOutputReadLine();
        _hostProcess.BeginErrorReadLine();

        // Wait up to 60s for the host to respond
        await WaitForHttpReady(BaseUrl + "/api/v1/version", TimeSpan.FromSeconds(60));

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        // English, whatever the machine's language: the UI follows the
        // browser's language when a user hasn't chosen one (users.language
        // null), and the tests assert English strings. I18nTests switch to
        // German explicitly.
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] { "--lang=en-US", "--accept-lang=en-US,en" },
            // A machine without Playwright's own browsers can name a Chromium
            // it has (CI installs Playwright's; null keeps that one).
            ExecutablePath = string.IsNullOrEmpty(chromium) ? null : chromium,
        });
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
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { /* a straggling file handle — the OS temp cleaner gets it */ }
    }

    private readonly Queue<string> _hostLog = new();

    private void Remember(string? line)
    {
        if (line is null) return;
        lock (_hostLog)
        {
            _hostLog.Enqueue(line);
            while (_hostLog.Count > 200) _hostLog.Dequeue();
        }
    }

    /// <summary>The host's last log lines — for a failing test's message.</summary>
    public string HostLogTail()
    {
        lock (_hostLog) return string.Join(Environment.NewLine, _hostLog);
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
