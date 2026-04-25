namespace Fishbowl.Core.Util;

// Thrown by repository writes that fail their *Limits.Validate gate.
// The API edge translates this into 413 with `{ field, reason }`; the
// MCP dispatcher maps it to JSON-RPC InvalidParams (-32602).
public sealed class ResourceValidationException : Exception
{
    public ResourceValidationError Error { get; }

    public ResourceValidationException(ResourceValidationError error)
        : base(error.ToString())
    {
        Error = error;
    }
}
