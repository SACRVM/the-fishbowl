using System.Security.Claims;

namespace Fishbowl.Core.Mcp;

// Claim names shared by the Bearer auth handler and downstream endpoints.
// Cookie-authenticated requests only carry `UserId`; Bearer-authenticated
// requests additionally carry `ContextType` + `ContextId` (the binding the
// API key was issued against) and one `Scope` claim per permitted action.
public static class McpContextClaims
{
    public const string UserId = "fishbowl_user_id";
    public const string ContextType = "fishbowl_context_type";
    public const string ContextId = "fishbowl_context_id";
    public const string Scope = "scope";

    // App-bearer keys carry the owner pair (the user or team that owns the
    // app) alongside the appId in ContextId. Together they let the auth
    // handler build an AppRef without a second DB lookup. Empty on user/team
    // keys.
    public const string OwnerType = "fishbowl_owner_type";
    public const string OwnerId = "fishbowl_owner_id";

    // Name of the authentication scheme used for Bearer tokens. Duplicated
    // here (vs ApiKeyAuthenticationOptions.DefaultScheme in Fishbowl.Host)
    // because Fishbowl.Api sits below Host in the dep graph and must not
    // reference host-level auth types.
    public const string BearerScheme = "ApiKey";

    // Collapses the principal to a single `ContextRef`. Bearer principals
    // resolve to whichever ContextType claim they carry (team/app); cookie
    // users and Bearer keys bound to a user resolve to ContextRef.User.
    // Throws when the principal has no usable identity — callers should have
    // already gated with [Authorize].
    public static ContextRef Resolve(ClaimsPrincipal user)
    {
        var type = user.FindFirst(ContextType)?.Value;
        var id = user.FindFirst(ContextId)?.Value;
        if (!string.IsNullOrEmpty(id))
        {
            if (string.Equals(type, "team", StringComparison.Ordinal))
                return ContextRef.Team(id);
            if (string.Equals(type, "app", StringComparison.Ordinal))
                return ContextRef.App(id);
        }

        var uid = user.FindFirst(UserId)?.Value;
        if (!string.IsNullOrEmpty(uid)) return ContextRef.User(uid);

        throw new InvalidOperationException("Principal has no resolvable context.");
    }

    // App-routing variant: reads the three app claims and returns an AppRef.
    // Throws when the principal isn't an app-bearer (cookie users, user-keys,
    // and team-keys all fall through to InvalidOperationException). App MCP
    // tools call this inside InvokeAsync to fetch the owner-id needed to open
    // the app's .db file.
    public static AppRef ResolveApp(ClaimsPrincipal user)
    {
        var type = user.FindFirst(ContextType)?.Value;
        if (!string.Equals(type, "app", StringComparison.Ordinal))
            throw new InvalidOperationException("Principal is not bound to an app context.");

        var appId = user.FindFirst(ContextId)?.Value;
        var ownerType = user.FindFirst(OwnerType)?.Value;
        var ownerId = user.FindFirst(OwnerId)?.Value;

        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(ownerType) || string.IsNullOrEmpty(ownerId))
            throw new InvalidOperationException("App principal is missing owner_type/owner_id/context_id.");

        return new AppRef(ownerType, ownerId, appId);
    }
}
