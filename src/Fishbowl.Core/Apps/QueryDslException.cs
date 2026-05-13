namespace Fishbowl.Core.Apps;

// Typed envelope every QueryDsl.Compile failure carries. The MCP/REST surface
// maps `Code` to a structured error response — callers fix the input and
// retry; the wire layer doesn't have to parse free-text messages.
public sealed class QueryDslException : Exception
{
    public string Code { get; }
    public string? Field { get; }

    public QueryDslException(string code, string message, string? field = null) : base(message)
    {
        Code = code;
        Field = field;
    }
}

public static class QueryDslErrorCodes
{
    public const string BadShape = "bad_shape";
    public const string UnknownColumn = "unknown_column";
    public const string UnqueryableColumn = "unqueryable_column";
    public const string UnknownOperator = "unknown_operator";
    public const string OperatorNotAllowed = "operator_not_allowed";
    public const string TypeMismatch = "type_mismatch";
    public const string DepthExceeded = "depth_exceeded";
    public const string LeavesExceeded = "leaves_exceeded";
    public const string InTooLarge = "in_too_large";
    public const string BadDirection = "bad_direction";
    public const string BadCombinator = "bad_combinator";
}
