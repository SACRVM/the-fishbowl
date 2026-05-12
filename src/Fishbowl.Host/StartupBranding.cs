using System.Reflection;
using Fishbowl.Core.Repositories;
using Fishbowl.Host.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

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
                AnsiConsole.Clear();
            }

            var version = ResolveVersion();
            var endpoints = ResolveEndpoints(app);
            var auth = ResolveAuth(app);

            AnsiConsole.Write(new Rule("[gold1]THE FISHBOWL[/]").RuleStyle("grey"));
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine("[bold gold1]  > [/] [bold white]THE FISHBOWL[/]");
            AnsiConsole.MarkupLine($"[bold grey]    {Markup.Escape(version)}[/] - [italic]Your memory lives here. You don't.[/]");
            AnsiConsole.WriteLine();

            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            table.AddColumn(new TableColumn("[u]Service[/]").Centered());
            table.AddColumn(new TableColumn("[u]Status[/]").Centered());
            table.AddColumn(new TableColumn("[u]Endpoint[/]").LeftAligned());

            var primary = endpoints.Primary;
            if (primary is null)
            {
                table.AddRow("Web UI",
                    "[red]No listener[/]",
                    "[grey](Kestrel did not report any addresses — check bind config)[/]");
            }
            else
            {
                table.AddRow("Web UI",
                    "[green]Running[/]",
                    $"[link={primary}]{Markup.Escape(primary)}[/]");
                table.AddRow("REST API",
                    "[green]Ready[/]",
                    Markup.Escape($"{primary}/api/v1/"));
                table.AddRow("MCP",
                    auth.AnyBearerCapable ? "[green]Ready[/]" : "[grey]Awaiting setup[/]",
                    Markup.Escape($"{primary}/mcp"));
            }

            table.AddRow("Security",
                auth.Configured ? "[gold1]Configured[/]" : "[grey]Setup wizard[/]",
                Markup.Escape(auth.Summary));

            table.AddRow("Data",
                "[green]Local[/]",
                Markup.Escape(dataPath));

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            if (!auth.Configured)
            {
                AnsiConsole.MarkupLine(
                    "[bold yellow]>[/] First run: open the Web UI and complete the setup wizard at " +
                    $"[bold]{Markup.Escape(primary ?? "/setup")}/setup[/].");
            }

            AnsiConsole.MarkupLine("[bold cyan]>[/] Press [bold white]Ctrl+C[/] to stop the bowl.");
            AnsiConsole.WriteLine();
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
