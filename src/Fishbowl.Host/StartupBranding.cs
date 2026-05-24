using System.Reflection;
using Fishbowl.Core.Repositories;
using Fishbowl.Host.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Retro.Crt;

namespace Fishbowl.Host;

public static class StartupBranding
{
    // Called from ApplicationStarted so app.Urls / IServerAddressesFeature
    // are populated with the real bindings — including ACME's port-80/443
    // listen, which is wired via ConfigureKestrel and isn't visible at
    // builder time. Reads version + auth state from real sources so the
    // banner can't lie about what the operator actually deployed.
    public static void PrintBanner(WebApplication app, string dataPath)
    {
        try
        {
            if (!Console.IsOutputRedirected)
            {
                Crt.ClrScr();
            }

            var version = ResolveVersion();
            var endpoints = ResolveEndpoints(app);
            var auth = ResolveAuth(app);

            // Dos palette: white accent, dark-grey muted frame, classic
            // green/yellow/red status slots. UseTheme also drives Table.Print's
            // default header (Accent) and border (Muted) colours.
            using (Crt.UseTheme(Themes.Dos))
            {
                Banner.Box("THE FISHBOWL", fg: Color.LightCyan);
                Crt.WriteLine();

                Crt.Write("  > ");
                using (Crt.WithStyle(fg: Color.White, bold: true)) Crt.WriteLine("THE FISHBOWL");
                using (Crt.WithStyle(fg: Color.DarkGray)) Crt.Write($"    {version}");
                Crt.Write(" - ");
                using (Crt.WithStyle(fg: Color.LightGray)) Crt.WriteLine("Your memory lives here. You don't.");
                Crt.WriteLine();

                var headers = new[] { "Service", "Status", "Endpoint" };
                var rows = new List<string[]>();

                var primary = endpoints.Primary;
                if (primary is null)
                {
                    rows.Add(["Web UI", "No listener",
                        "(Kestrel did not report any addresses — check bind config)"]);
                }
                else
                {
                    rows.Add(["Web UI", "Running", primary]);
                    rows.Add(["REST API", "Ready", $"{primary}/api/v1/"]);
                    rows.Add(["MCP", auth.AnyBearerCapable ? "Ready" : "Awaiting setup", $"{primary}/mcp"]);
                }

                rows.Add(["Security", auth.Configured ? "Configured" : "Setup wizard", auth.Summary]);
                rows.Add(["Data", "Local", dataPath]);

                // Per-cell status colours aren't expressible via Table.Print yet
                // (uniform header/border only) — tracked in retro-crt#26. Status
                // reads as plain text until that lands.
                Table.Print(headers, [.. rows], border: TableBorder.Box);
                Crt.WriteLine();

                if (!auth.Configured)
                {
                    using (Crt.WithStyle(fg: Color.Yellow, bold: true)) Crt.Write("> ");
                    Crt.WriteLine(
                        $"First run: open the Web UI and complete the setup wizard at {primary ?? "/setup"}/setup.");
                }

                using (Crt.WithStyle(fg: Color.LightCyan, bold: true)) Crt.Write("> ");
                Crt.Write("Press ");
                using (Crt.WithStyle(fg: Color.White, bold: true)) Crt.Write("Ctrl+C");
                Crt.WriteLine(" to stop the bowl.");
                Crt.WriteLine();
            }
        }
        catch
        {
            // Fail silent if console styling fails (e.g. in non-interactive environments)
            Console.WriteLine("The Fishbowl is running.");
        }
    }

    // Pulls the version that `release.yml` injects via -p:Version at publish
    // time. AssemblyInformationalVersionAttribute is what `dotnet publish
    // -p:Version=X.Y.Z` sets; falls back to AssemblyVersion (0.0.0.0 for
    // unversioned dev builds) so the banner never throws.
    private static string ResolveVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        if (asm is null) return "v0.0.0-dev";

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // .NET prepends a +commit suffix on local builds (e.g. "0.0.0+abc123");
            // strip it for display — operators don't care about the SHA, they care
            // about the semver they tagged.
            var plus = info.IndexOf('+');
            return "v" + (plus > 0 ? info[..plus] : info);
        }

        var ver = asm.GetName().Version;
        return ver is null ? "v0.0.0-dev" : $"v{ver}";
    }

    private record EndpointInfo(string? Primary, IReadOnlyList<string> All);

    private static EndpointInfo ResolveEndpoints(WebApplication app)
    {
        // IServerAddressesFeature is populated once Kestrel has bound — that's
        // why this is called from ApplicationStarted. app.Urls is a more
        // forgiving fallback (it reflects configured bindings even pre-start).
        var server = app.Services.GetService<IServer>();
        var fromServer = server?.Features.Get<IServerAddressesFeature>()?.Addresses;

        var urls = (fromServer is { Count: > 0 } ? fromServer : (IEnumerable<string>)app.Urls)
            .Select(u => u.TrimEnd('/'))
            .Where(u => !string.IsNullOrWhiteSpace(u))
            // ASP.NET reports IPv6-wildcard URLs like http://[::]:80 — show the
            // operator a friendlier form (localhost for dev, the wildcard for prod).
            .Select(NormaliseUrl)
            .Distinct()
            .ToList();

        if (urls.Count == 0) return new EndpointInfo(null, Array.Empty<string>());

        // Prefer HTTPS for the primary link — that's the one we want
        // operators to click and bookmark.
        var primary = urls.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                      ?? urls[0];

        return new EndpointInfo(primary, urls);
    }

    private static string NormaliseUrl(string raw)
    {
        // Replace IPv6 wildcard "[::]" or IPv4 "0.0.0.0" with localhost when
        // running on dev-port shapes, otherwise keep as-is (operators on
        // production know to hit their domain externally).
        return raw
            .Replace("[::]", "localhost", StringComparison.Ordinal)
            .Replace("0.0.0.0", "localhost", StringComparison.Ordinal);
    }

    private record AuthState(
        bool Configured,
        bool GoogleConfigured,
        bool LocalConfigured,
        bool DiscordConfigured,
        bool AnyBearerCapable,
        string Summary);

    private static AuthState ResolveAuth(WebApplication app)
    {
        // Read live state — not the appsettings.json that's irrelevant in this
        // architecture. ConfigurationCache reflects what /api/setup writes plus
        // what ConfigurationInitializer loaded at boot.
        var cache = app.Services.GetService<ConfigurationCache>();
        var googleClient = cache?.Get("Google:ClientId");
        var googleConfigured = !string.IsNullOrWhiteSpace(googleClient)
            && !string.Equals(googleClient, "placeholder", StringComparison.OrdinalIgnoreCase);

        var discordToken = cache?.Get("Discord:BotToken");
        var discordConfigured = !string.IsNullOrWhiteSpace(discordToken)
            && !string.Equals(discordToken, "placeholder", StringComparison.OrdinalIgnoreCase);

        // Local-user check is a sync probe via the system repo — banner runs
        // on a background callback so blocking briefly is fine.
        var localConfigured = false;
        try
        {
            using var scope = app.Services.CreateScope();
            var system = scope.ServiceProvider.GetService<ISystemRepository>();
            if (system is not null)
            {
                localConfigured = system.HasLocalUserAsync().GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Repo not available yet (rare) — treat as "not configured" rather
            // than crashing the banner.
        }

        var enabled = new List<string>();
        if (googleConfigured) enabled.Add("Google OAuth");
        if (localConfigured) enabled.Add("Local");
        if (discordConfigured) enabled.Add("Discord bot");

        var summary = enabled.Count == 0
            ? "Setup wizard pending — no auth configured"
            : string.Join(" + ", enabled);

        // Any user-style auth means API keys can be minted, so Bearer/MCP is
        // operable. A discord-only install still wouldn't unlock the MCP
        // surface for an operator to use.
        var bearerCapable = googleConfigured || localConfigured;

        return new AuthState(
            Configured: enabled.Count > 0,
            GoogleConfigured: googleConfigured,
            LocalConfigured: localConfigured,
            DiscordConfigured: discordConfigured,
            AnyBearerCapable: bearerCapable,
            Summary: summary);
    }
}
