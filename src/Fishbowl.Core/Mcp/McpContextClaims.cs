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
    // A space key's space as its folder id (the ContextId claim stays the
    // slug the key was issued for). Set by the key handler, which resolves
    // the slug once per request and checks the owner is still a member.
    public const string SpaceId = "fishbowl_space_id";
    // A cookie session's copy of users.session_stamp (see AccountStateMiddleware).
    public const string SessionStamp = "fishbowl_session";
    public const string Scope = "scope";

    // Name of the authentication scheme used for Bearer tokens. Duplicated
    // here (vs ApiKeyAuthenticationOptions.DefaultScheme in Fishbowl.Host)
    // because Fishbowl.Api sits below Host in the dep graph and must not
    // reference host-level auth types.
    public const string BearerScheme = "ApiKey";

    // Collapses the principal to a single `ContextRef`. Bearer principals
    // resolve to whichever ContextType claim they carry (space/app); cookie
    // users and Bearer keys bound to a user resolve to ContextRef.User.
    // Throws when the principal has no usable identity — callers should have
    // already gated with [Authorize].
    public static ContextRef Resolve(ClaimsPrincipal user)
    {
        var type = user.FindFirst(ContextType)?.Value;
        var id = user.FindFirst(ContextId)?.Value;
        if (!string.IsNullOrEmpty(id))
        {
            // The DB lives in spaces/<id>/ — never open it by slug.
            if (string.Equals(type, "space", StringComparison.Ordinal))
                return ContextRef.Space(user.FindFirst(SpaceId)?.Value ?? id);
        }

        var uid = user.FindFirst(UserId)?.Value;
        if (!string.IsNullOrEmpty(uid)) return ContextRef.User(uid);

        throw new InvalidOperationException("Principal has no resolvable context.");
    }

}
