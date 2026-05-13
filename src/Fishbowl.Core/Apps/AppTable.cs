namespace Fishbowl.Core.Apps;

// Describe-shape for an owner-defined table inside an app DB. Base columns
// always sit at the head of `Columns`; user columns follow in declaration
// order. The DDL generator uses the same ordering when emitting CREATE TABLE.
public sealed record AppTable(
    string Name,
    IReadOnlyList<AppColumn> Columns);
