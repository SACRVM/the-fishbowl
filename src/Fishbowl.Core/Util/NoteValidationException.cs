namespace Fishbowl.Core.Util;

// Thrown by NoteRepository when a write fails NoteLimits.Validate. Carries
// the structured error so the API edge can return a useful 413 body and
// MCP tools can surface a meaningful tool error to the caller.
public sealed class NoteValidationException : Exception
{
    public NoteLimits.ValidationError Error { get; }

    public NoteValidationException(NoteLimits.ValidationError error)
        : base(error.ToString())
    {
        Error = error;
    }
}
