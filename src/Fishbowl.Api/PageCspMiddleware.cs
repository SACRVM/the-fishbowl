using Fishbowl.Core;
using Fishbowl.Core.Desktop;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;

namespace Fishbowl.Api;

// Every HTML response that doesn't bring its own policy gets PageCsp — the
// SPA shell, /login, /setup, /pending, a fallback-served page. Responses that
// set one (an app's frame document, file content) keep theirs. For a signed-in
// viewer the origins of their trusted apps join script-src; that lookup only
// runs for page requests, never for /api, /mcp or assets.
public sealed class PageCspMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PageCspMiddleware> _logger;

    public PageCspMiddleware(RequestDelegate next, ILogger<PageCspMiddleware>? logger = null)
    {
        _next = next;
        _logger = logger ?? NullLogger<PageCspMiddleware>.Instance;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsPageRequest(context.Request))
        {
            var trusted = await TrustedOriginsAsync(context);
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                var type = context.Response.ContentType ?? "";
                if (type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                    && !headers.ContainsKey(HeaderNames.ContentSecurityPolicy))
                {
                    headers[HeaderNames.ContentSecurityPolicy] = PageCsp.Build(trusted);
                }
                return Task.CompletedTask;
            });
        }
        await _next(context);
    }

    private static bool IsPageRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;
        var path = request.Path.Value ?? "/";
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/apps/frame/", StringComparison.OrdinalIgnoreCase)) return false;
        var last = path[(path.LastIndexOf('/') + 1)..];
        return !last.Contains('.') || last.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<string>> TrustedOriginsAsync(HttpContext context)
    {
        var userId = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst(McpContextClaims.UserId)?.Value
            : null;
        if (string.IsNullOrEmpty(userId)) return Array.Empty<string>();
        try
        {
            var desktop = context.RequestServices.GetRequiredService<IDesktopRepository>();
            var apps = await desktop.ListAppsAsync(ContextRef.User(userId), context.RequestAborted);
            return apps.Where(a => a.Mode == AppModes.Trusted).Select(a => a.Origin).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No personal DB (pending, deleted) or a read failure: the page
            // still gets a policy, just without trusted apps.
            _logger.LogDebug(ex, "Trusted app origins unavailable for the page CSP");
            return Array.Empty<string>();
        }
    }
}

public static class PageCspMiddlewareExtensions
{
    public static IApplicationBuilder UsePageCsp(this IApplicationBuilder app) =>
        app.UseMiddleware<PageCspMiddleware>();
}
