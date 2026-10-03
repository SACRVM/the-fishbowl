using Fishbowl.Core.Desktop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Fishbowl.Api;

// Every HTML response that doesn't bring its own policy gets PageCsp — the
// SPA shell, /login, /setup, /pending, /oauth/authorize, a fallback-served
// page. Responses that set one (an app's frame document, file content) keep
// theirs.
public sealed class PageCspMiddleware
{
    private readonly RequestDelegate _next;

    public PageCspMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsPageRequest(context.Request))
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                var type = context.Response.ContentType ?? "";
                if (type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                    && !headers.ContainsKey(HeaderNames.ContentSecurityPolicy))
                {
                    headers[HeaderNames.ContentSecurityPolicy] = PageCsp.Build();
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
}

public static class PageCspMiddlewareExtensions
{
    public static IApplicationBuilder UsePageCsp(this IApplicationBuilder app) =>
        app.UseMiddleware<PageCspMiddleware>();
}
