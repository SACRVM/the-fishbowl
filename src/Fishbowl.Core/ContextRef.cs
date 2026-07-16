namespace Fishbowl.Core;

// Discriminated identifier for a data-holding SQLite file. Folder-per-context
// layout: personal notes live in `users/{userId}/personal.db`, space notes in
// `spaces/{spaceId}/space.db`. The schema is identical — only ownership differs.
// DatabaseFactory resolves a ContextRef to the right file; repositories take
// ContextRef so a single implementation serves both cookie-auth (personal)
// and Bearer-auth (space) callers without duplicate code paths. A Space may be
// shared with multiple members or owned solo (e.g. an agent's own memory) —
// "shared" is a property of the Space, not a separate context type.
public readonly record struct ContextRef(ContextType Type, string Id)
{
    public static ContextRef User(string id) => new(ContextType.User, id);
    public static ContextRef Space(string id) => new(ContextType.Space, id);

    // Carries only the app id. The owner pair (ownerType, ownerId) needed to
    // open the actual app.db lives on `AppRef`, resolved separately from the
    // principal's claims. Keeping App single-id here means existing dispatcher
    // / repository plumbing that pattern-matches on ContextType stays uniform.
    public static ContextRef App(string id) => new(ContextType.App, id);
}

public enum ContextType
{
    User,
    Space,
    App,
}
