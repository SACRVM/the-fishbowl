namespace Fishbowl.Core;

// Discriminated identifier for a data-holding SQLite file. Folder-per-context
// layout: personal notes live in `users/{userId}/personal.db`, team notes in
// `teams/{teamId}/team.db`. The schema is identical — only ownership differs.
// DatabaseFactory resolves a ContextRef to the right file; repositories take
// ContextRef so a single implementation serves both cookie-auth (personal)
// and Bearer-auth (team) callers without duplicate code paths.
public readonly record struct ContextRef(ContextType Type, string Id)
{
    public static ContextRef User(string id) => new(ContextType.User, id);
    public static ContextRef Team(string id) => new(ContextType.Team, id);

    // Carries only the app id. The owner pair (ownerType, ownerId) needed to
    // open the actual app.db lives on `AppRef`, resolved separately from the
    // principal's claims. Keeping App single-id here means existing dispatcher
    // / repository plumbing that pattern-matches on ContextType stays uniform.
    public static ContextRef App(string id) => new(ContextType.App, id);
}

public enum ContextType
{
    User,
    Team,
    App,
}
