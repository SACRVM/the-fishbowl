namespace Fishbowl.Core;

// Three-coordinate routing for an App's `app.db` file:
//   ownerType: "user" | "team"
//   ownerId:   ULID of the owning user or team
//   appId:     ULID of the app itself (also the folder name under apps/)
//
// Separate from `ContextRef` on purpose. `ContextRef` is two coordinates
// (type + id) and feeds the existing dispatcher / claim path; `AppRef` is the
// full triple needed to resolve an app DB file on disk. App MCP tools take a
// `ContextRef.App(appId)` from the dispatcher (so the registry stays uniform)
// and call `McpContextClaims.ResolveApp(principal)` for the owner triple.
public readonly record struct AppRef(string OwnerType, string OwnerId, string AppId)
{
    public const string OwnerTypeUser = "user";
    public const string OwnerTypeTeam = "team";

    public static AppRef OfUser(string userId, string appId) =>
        new(OwnerTypeUser, userId, appId);

    public static AppRef OfTeam(string teamId, string appId) =>
        new(OwnerTypeTeam, teamId, appId);

    // The ContextRef of the *owner* DB (where the `apps` registry row lives).
    // App-row repos use this to read/write the registry; the AppRef itself
    // routes the app's own .db file.
    public ContextRef OwnerContext() => OwnerType switch
    {
        OwnerTypeUser => ContextRef.User(OwnerId),
        OwnerTypeTeam => ContextRef.Team(OwnerId),
        _ => throw new InvalidOperationException($"Unknown AppRef owner type: {OwnerType}"),
    };
}
