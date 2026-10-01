using Fishbowl.Core.Apps;
using Fishbowl.Core.Tables;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Fishbowl.Api;

// A TableException anywhere in a request — the tables API, or a note/event/
// todo/contact delete that a table row still links to — becomes the usual
// refusal `{ error, message, …args }` with its status, not a 500.
public sealed class TableErrorsMiddleware
{
    private readonly RequestDelegate _next;

    public TableErrorsMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (TableException ex) when (!context.Response.HasStarted)
        {
            await ApiErrors.Json(ex.Status, ex.Code, ex.Message, ex.Args).ExecuteAsync(context);
        }
        catch (QueryDslException ex) when (!context.Response.HasStarted)
        {
            // A filter or order the table can't take (unknown column, wrong type…).
            await ApiErrors.Json(StatusCodes.Status400BadRequest, "query_invalid", ex.Message,
                new { reason = ex.Message, rule = ex.Code, column = ex.Field }).ExecuteAsync(context);
        }
    }
}

public static class TableErrorsMiddlewareExtensions
{
    public static IApplicationBuilder UseTableErrors(this IApplicationBuilder app) =>
        app.UseMiddleware<TableErrorsMiddleware>();
}
