using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// Liveness probe. Anonymous (no auth) so container orchestrators,
// uptime monitors, and curl-from-the-terminal can hit it without
// burning a Bearer key. Stays cheap on purpose: no DB hit, no
// embedding probe — those would couple liveness to readiness and
// make the endpoint noisy on cold start.
//
// If we later need a separate readiness probe (model loaded,
// migrations applied, etc.) it gets its own /ready route.
public static class HealthApi
{
    public static RouteGroupBuilder MapHealthApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1");

        group.MapGet("/health", () => Results.Ok(new
        {
            status = "ok",
            time = DateTime.UtcNow.ToString("o"),
        }))
        .WithName("Health")
        .WithSummary("Anonymous liveness probe. Returns ok + a server timestamp.")
        .Produces<object>()
        .AllowAnonymous();

        return group;
    }
}
