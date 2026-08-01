using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

public static class VersionApi
{
    // Read once at startup — the assembly can't change under a running process.
    private static readonly string RunningVersion = ResolveVersion();

    public static RouteGroupBuilder MapVersionApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1");

        group.MapGet("/version", () =>
            Results.Ok(new { version = RunningVersion }))
        .WithName("GetVersion")
        .WithSummary("Returns the running server version.")
        .Produces<object>();

        return group;
    }

    /// <summary>
    /// The release workflow publishes with <c>-p:Version=&lt;tag without the v&gt;</c>, so a
    /// released binary reports its release tag here and the UI footer can be trusted to say
    /// what's actually deployed. Dev builds set no Version property and report 1.0.0 — the
    /// honest answer for "this did not come from a release".
    /// </summary>
    private static string ResolveVersion()
    {
        var assembly = typeof(VersionApi).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink appends "+<commit-sha>" to the informational version; the sha is
            // noise in a footer.
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
