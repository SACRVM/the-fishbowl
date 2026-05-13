namespace Fishbowl.Core.Mcp;

// The canonical set of scopes a Bearer key can carry. Mirrors the
// `RequiredScope` strings on every `IMcpTool` and every `.RequireScope("…")`
// call in `Fishbowl.Api`. Adding a new resource means: add the pair here,
// add the matching MCP tool, and gate the API endpoints — keeping the list
// in Core so all three sites import the same source of truth instead of
// passing free-form strings.
//
// "tasks" is the wire spelling for what the codebase otherwise calls todos
// (the API surface is `/api/v1/todos`, but the scope predates that rename).
public static class ScopeCatalog
{
    public const string ReadNotes = "read:notes";
    public const string WriteNotes = "write:notes";
    public const string ReadTags = "read:tags";
    public const string WriteTags = "write:tags";
    public const string ReadTasks = "read:tasks";
    public const string WriteTasks = "write:tasks";
    public const string ReadContacts = "read:contacts";
    public const string WriteContacts = "write:contacts";
    public const string ReadEvents = "read:events";
    public const string WriteEvents = "write:events";

    // Apps platform — single-app-wide trio. `app:admin` gates DDL
    // (create/alter/drop table, create index); read/write gate row CRUD.
    // No per-table or per-row scopes in MVP — the bearer token *is* the
    // app boundary.
    public const string AppRead = "app:read";
    public const string AppWrite = "app:write";
    public const string AppAdmin = "app:admin";

    private static readonly HashSet<string> _all = new(StringComparer.Ordinal)
    {
        ReadNotes, WriteNotes,
        ReadTags, WriteTags,
        ReadTasks, WriteTasks,
        ReadContacts, WriteContacts,
        ReadEvents, WriteEvents,
        AppRead, AppWrite, AppAdmin,
    };

    public static IReadOnlyCollection<string> All => _all;

    public static bool IsValid(string? scope)
        => !string.IsNullOrEmpty(scope) && _all.Contains(scope);

    // Returns the subset of `requested` that aren't recognised. Empty list
    // means every scope is valid. Trims whitespace per entry — a typo like
    // " read:notes" should still surface as the offender, not pass silently.
    public static IReadOnlyList<string> UnknownScopes(IEnumerable<string>? requested)
    {
        if (requested is null) return Array.Empty<string>();
        var unknown = new List<string>();
        foreach (var raw in requested)
        {
            var s = raw?.Trim();
            if (string.IsNullOrEmpty(s)) { unknown.Add(raw ?? string.Empty); continue; }
            if (!_all.Contains(s)) unknown.Add(s);
        }
        return unknown;
    }
}
