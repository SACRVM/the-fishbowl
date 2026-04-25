using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fishbowl.Mcp.Endpoints;

// MCP Streamable HTTP endpoint. Single POST /mcp with JSON-RPC 2.0 payloads;
// synchronous responses only (no SSE). Methods: initialize, tools/list,
// tools/call. Notifications (no `id`) get 202 with an empty body per the
// Streamable HTTP spec.
public static class McpEndpoint
{
    public static IEndpointRouteBuilder MapMcpEndpoint(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/mcp", (Delegate)HandleAsync)
            .WithName("Mcp")
            .WithSummary("MCP Streamable HTTP endpoint (JSON-RPC 2.0).")
            .RequireAuthorization();

        return routes;
    }

    private static async Task<IResult> HandleAsync(HttpContext ctx)
    {
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Mcp");

        McpRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<McpRequest>(
                ctx.Request.Body, JsonOpts, ctx.RequestAborted);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "MCP JSON parse failed");
            return ErrorResponse(id: null, McpErrorCodes.ParseError, "Parse error");
        }

        if (request is null || string.IsNullOrEmpty(request.Method))
            return ErrorResponse(id: null, McpErrorCodes.InvalidRequest, "Invalid Request");

        var isNotification = request.Id is null;

        object? result = null;
        McpError? error = null;
        try
        {
            (result, error) = await DispatchAsync(request, ctx);
        }
        catch (NoteValidationException ex)
        {
            // The tool sent oversized/invalid note data — that's a caller
            // error, not a server fault. JSON-RPC's InvalidParams (-32602)
            // tells the client "fix your input and retry", whereas the
            // generic InternalError suggests "this might work next time".
            logger.LogDebug("MCP {Method} rejected on note validation: {Field}", request.Method, ex.Error.Field);
            error = new McpError(McpErrorCodes.InvalidParams, ex.Message);
        }
        catch (ArgumentException ex)
        {
            // Tools throw ArgumentException for missing required fields
            // (e.g. RememberTool's "title is required"). Same logic — the
            // caller can fix the input.
            logger.LogDebug("MCP {Method} rejected on argument validation: {Message}", request.Method, ex.Message);
            error = new McpError(McpErrorCodes.InvalidParams, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MCP dispatch failed for {Method}", request.Method);
            error = new McpError(McpErrorCodes.InternalError, ex.Message);
        }

        if (isNotification) return Results.Accepted();

        if (result is null && error is null)
            error = new McpError(McpErrorCodes.MethodNotFound, $"Method not found: {request.Method}");

        var response = new McpResponse("2.0", request.Id, error is null ? result : null, error);
        return Results.Json(response, JsonOpts);
    }

    private static async Task<(object? Result, McpError? Error)> DispatchAsync(
        McpRequest request, HttpContext ctx)
    {
        var registry = ctx.RequestServices.GetRequiredService<ToolRegistry>();

        switch (request.Method)
        {
            case "initialize":
                return (new
                {
                    protocolVersion = McpProtocol.ProtocolVersion,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = McpProtocol.ServerName, version = McpProtocol.ServerVersion },
                }, null);

            case "tools/list":
                return (new
                {
                    tools = registry.All.Select(t => new
                    {
                        name = t.Name,
                        description = t.Description,
                        inputSchema = t.InputSchema,
                    }).ToArray(),
                }, null);

            case "tools/call":
                return await CallToolAsync(registry, request, ctx);

            default:
                return (null, null);
        }
    }

    private static async Task<(object? Result, McpError? Error)> CallToolAsync(
        ToolRegistry registry, McpRequest request, HttpContext ctx)
    {
        if (request.Params is not { } p ||
            !p.TryGetProperty("name", out var nameEl) ||
            nameEl.ValueKind != JsonValueKind.String)
        {
            return (null, new McpError(McpErrorCodes.InvalidParams, "tools/call requires `name`"));
        }

        var toolName = nameEl.GetString()!;
        var tool = registry.Get(toolName);
        if (tool is null)
            return (null, new McpError(McpErrorCodes.MethodNotFound, $"Unknown tool: {toolName}"));

        // Cookie/OAuth principals have full access (no scope claims).
        // Bearer principals must carry the tool's RequiredScope.
        var user = ctx.User;
        if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme &&
            !user.HasClaim(McpContextClaims.Scope, tool.RequiredScope))
        {
            return (null, new McpError(
                McpErrorCodes.InternalError,
                $"Scope denied: tool '{toolName}' requires '{tool.RequiredScope}'"));
        }

        ContextRef ctxRef;
        try { ctxRef = McpContextClaims.Resolve(user); }
        catch (InvalidOperationException ex)
        {
            return (null, new McpError(McpErrorCodes.InternalError, ex.Message));
        }

        var actor = user.FindFirst(McpContextClaims.UserId)?.Value ?? "";
        var arguments = p.TryGetProperty("arguments", out var argsEl)
            ? argsEl : default;

        var toolResult = await tool.InvokeAsync(ctxRef, actor, arguments, user, ctx.RequestAborted);

        // MCP spec envelope: array of content entries. We render structured
        // results as a JSON string inside a single text content block —
        // clients re-parse it. Keeps the wire format unambiguous.
        var text = JsonSerializer.Serialize(toolResult, JsonOpts);
        return (new
        {
            content = new[] { new { type = "text", text } },
            isError = false,
        }, null);
    }

    private static IResult ErrorResponse(JsonElement? id, int code, string message)
    {
        var response = new McpResponse("2.0", id, null, new McpError(code, message));
        return Results.Json(response, JsonOpts);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
