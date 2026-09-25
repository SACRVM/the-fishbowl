namespace Fishbowl.Core.Util;

// Single shape every *Limits.Validate returns when a write breaches a
// hard cap. `Resource` ("note", "contact", "todo") lets logs and
// generic catch handlers tell which subsystem rejected without a chain
// of typeof() checks.
//
// `Kind` separates "too big" (413) from "not acceptable as sent" (400) —
// e.g. a malformed secret envelope, or a secret in a space. Defaults to
// SizeLimit so every existing cap keeps its meaning.
public sealed record ResourceValidationError(
    string Resource, string Field, string Reason,
    ResourceValidationKind Kind = ResourceValidationKind.SizeLimit)
{
    public override string ToString() => $"{Resource}.{Field}: {Reason}";
}

public enum ResourceValidationKind
{
    SizeLimit,
    Invalid,
}
