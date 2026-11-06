namespace Fishbowl.Core.Util;

// Single shape every *Limits.Validate returns when a write breaches a
// hard cap. `Resource` ("note", "contact", "todo") lets logs and
// generic catch handlers tell which subsystem rejected without a chain
// of typeof() checks.
//
// `Kind` separates "too big" (413) from "not acceptable as sent" (400) —
// e.g. a malformed secret envelope, or a secret in a space. Defaults to
// SizeLimit so every existing cap keeps its meaning.
//
// `Conflict` (409) is a write the stored state refuses — a locked note, a
// slug another note has — and names its `Code` for the client to translate.
public sealed record ResourceValidationError(
    string Resource, string Field, string Reason,
    ResourceValidationKind Kind = ResourceValidationKind.SizeLimit,
    string? Code = null)
{
    public override string ToString() => $"{Resource}.{Field}: {Reason}";
}

public enum ResourceValidationKind
{
    SizeLimit,
    Invalid,
    Conflict,
}
