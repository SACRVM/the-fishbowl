using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbowl.Mcp.Endpoints;

// Minimal JSON-RPC 2.0 envelope types. Pragmatic subset of the spec — enough
// to speak Streamable HTTP with Claude Code. The Id field stays a JsonElement
// so we echo back exactly what the client sent (number or string); that's
// required by the spec and much simpler than trying to round-trip through
// a CLR type.

public record McpRequest(
    [property: JsonPropertyName("jsonrpc")] string Jsonrpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params);

public record McpError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] object? Data = null);

// One of Result or Error is populated. Both null on notifications (but
// notifications don't serialise a response anyway — they're handled at the
// HTTP layer by returning 202 with an empty body).
public record McpResponse(
    [property: JsonPropertyName("jsonrpc")] string Jsonrpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        object? Result,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        McpError? Error);

public static class McpErrorCodes
{
    // Standard JSON-RPC error codes.
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

public static class McpProtocol
{
    // Pinned to the spec version we implement against (Streamable HTTP
    // 2025-03). Advertised in the `initialize` response.
    public const string ProtocolVersion = "2025-03-26";
    public const string ServerName = "fishbowl";
    public const string ServerVersion = "0.1";
}
