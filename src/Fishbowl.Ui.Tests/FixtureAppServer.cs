using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Fishbowl.Ui.Tests;

// A second loopback origin serving one tiny desktop app — app.json plus its
// entry script — so a UI test installs a real app end to end: the browser
// reads the manifest (sac.apps.inspect), the entry is pinned by its hash,
// and the sandboxed frame loads it cross-origin. A raw TcpListener keeps it
// dependency-free and portable (no URL ACLs); every response carries
// Access-Control-Allow-Origin: * as a static host like GitHub Pages does.
//
// Bumping Version changes the entry's bytes, which is what an update is.
// The app, on mount: shows what it was granted, writes and reads back its
// storage (context.fs), and tries the network outside its declared connect
// list — which the frame's CSP must block.
public sealed class FixtureAppServer : IAsyncDisposable
{
    public const string AppId = "fixture-kanban";
    public const string AppName = "Fixture Kanban";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public string Version { get; set; } = "1.0.0";
    public string Origin { get; }
    public string AppUrl => Origin + "/kanban/";

    public FixtureAppServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string Manifest() => $$$"""
        {"id":"{{{AppId}}}","name":"{{{AppName}}}","kind":"view","tag":"app-{{{AppId}}}","entry":"app.js",
         "version":"{{{Version}}}","icon":"grid","description":"Boards and cards for anything with columns.",
         "permissions":{"files":true,"identity":true}}
        """;

    public string Entry() => $$"""
        (function () {
            if (customElements.get("app-{{AppId}}")) return;
            class FixtureApp extends HTMLElement {
                async mount(ctx) {
                    this.innerHTML = '<p id="hello">hello from fixture {{Version}}</p><p id="granted"></p><p id="stored"></p><p id="net"></p>';
                    const g = ctx.granted || {};
                    this.querySelector("#granted").textContent = "identity=" + g.identity + " files=" + g.files + " isolated=" + ctx.isolated;
                    try {
                        await ctx.fs.write("state.json", { n: 1 });
                        const back = await ctx.fs.read("state.json", null);
                        this.querySelector("#stored").textContent = "stored " + JSON.stringify(back);
                    } catch (err) {
                        this.querySelector("#stored").textContent = "no storage: " + (err.code || err.message);
                    }
                    try {
                        await fetch("{{Origin}}/kanban/app.json", { credentials: "omit" });
                        this.querySelector("#net").textContent = "net open";
                    } catch (err) {
                        this.querySelector("#net").textContent = "net blocked";
                    }
                }
            }
            customElements.define("app-{{AppId}}", FixtureApp);
        })();
        """;

    /// <summary>The entry's pin, as the browser computes it.</summary>
    public string EntryIntegrity() =>
        "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Entry())));

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var read = await stream.ReadAsync(buffer, _stop.Token);
                var request = Encoding.ASCII.GetString(buffer, 0, read);
                var line = request.Split("\r\n", 2)[0].Split(' ');
                var path = line.Length > 1 ? line[1].Split('?')[0] : "/";
                var (status, type, body) = path switch
                {
                    "/kanban/app.json" => ("200 OK", "application/json", Manifest()),
                    "/kanban/app.js" => ("200 OK", "text/javascript", Entry()),
                    _ => ("404 Not Found", "text/plain", "not found"),
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n" +
                           "Access-Control-Allow-Origin: *\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                await stream.WriteAsync(bytes, _stop.Token);
            }
            catch { /* a dropped connection is the browser's business */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch { /* stopping */ }
        _stop.Dispose();
    }
}
