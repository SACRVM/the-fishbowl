namespace Fishbowl.Core.Util;

// Single shape every *Limits.Validate returns when a write breaches a
// hard cap. `Resource` ("note", "contact", "todo") lets logs and
// generic catch handlers tell which subsystem rejected without a chain
// of typeof() checks.
public sealed record ResourceValidationError(string Resource, string Field, string Reason)
{
    public override string ToString() => $"{Resource}.{Field}: {Reason}";
}
