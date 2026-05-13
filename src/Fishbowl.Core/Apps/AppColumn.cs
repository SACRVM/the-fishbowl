namespace Fishbowl.Core.Apps;

// Owner-declared (or base-injected) column descriptor. Default is held as the
// untyped CLR value the row layer will validate against `Type` later — string
// for TEXT/DATETIME/JSON, long/double for INTEGER/REAL, bool for BOOLEAN.
public sealed record AppColumn(
    string Name,
    AppColumnType Type,
    bool Nullable = true,
    object? DefaultValue = null,
    bool IsUnique = false,
    bool IsBaseColumn = false);
