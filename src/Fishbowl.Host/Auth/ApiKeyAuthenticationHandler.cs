using System.Security.Claims;
using System.Text.Encodings.Web;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fishbowl.Host.Auth;

// Resolves Bearer tokens of the form `fb_live_...` into a ClaimsPrincipal
// that carries `fishbowl_user_id`, `fishbowl_context_type`, `fishbowl_context_id`,
// and one `scope` claim per permitted action. Tokens that don't start with
// our prefix return NoResult so other schemes (cookie) can try; tokens that
// match our prefix but don't resolve to a live key Fail.
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private const string BearerPrefix = "Bearer ";
    private const string FishbowlTokenPrefix = "fb_";

    private readonly IApiKeyRepository _repo;
    private readonly ISpaceRepository _spaces;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyRepository repo,
        ISpaceRepository spaces)
        : base(options, logger, encoder)
    {
        _repo = repo;
        _spaces = spaces;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var headerValues))
            return AuthenticateResult.NoResult();

        var header = headerValues.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header.Substring(BearerPrefix.Length).Trim();

        // Not our token format — let another bearer scheme try (none today,
        // but future-proofed). Avoids hijacking Bearer tokens we don't own.
        if (!token.StartsWith(FishbowlTokenPrefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var key = await _repo.LookupAsync(token, Context.RequestAborted);
        if (key is null)
            return AuthenticateResult.Fail("Invalid or revoked API key.");

        // Keys of the retired per-app databases (context "app") open nothing.
        if (string.Equals(key.ContextType, "app", StringComparison.Ordinal))
            return AuthenticateResult.Fail("App keys are retired — make a space key with the tables scopes instead.");

        IReadOnlyList<string> scopes = key.Scopes.ToList();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, key.UserId),
            new(McpContextClaims.UserId, key.UserId),
            new(McpContextClaims.ContextType, key.ContextType),
            new(McpContextClaims.ContextId, key.ContextId),
            new(McpContextClaims.KeyId, key.Id),
        };
        // Space keys: the space's DB folder is keyed by id, the key by slug.
        // Resolve it here, once, for every consumer (REST and MCP alike) —
        // and refuse a key whose owner has left the space.
        if (string.Equals(key.ContextType, "space", StringComparison.Ordinal))
        {
            var space = await _spaces.GetBySlugAsync(key.ContextId, Context.RequestAborted);
            var member = space is null ? null : await _spaces.GetMembershipAsync(space.Id, key.UserId, Context.RequestAborted);
            if (space is null || member is not { } role)
                return AuthenticateResult.Fail("This key's space is gone, or its owner is no longer a member.");
            claims.Add(new Claim(McpContextClaims.SpaceId, space.Id));
            // A key can't do more than its owner may in the space today: a
            // Reader's key loses write:* and send:*, a key below Designer design:*.
            scopes = scopes.Where(s => (!(s.StartsWith("write:", StringComparison.Ordinal) || s.StartsWith("send:", StringComparison.Ordinal)) || role.CanWrite())
                && (!s.StartsWith("design:", StringComparison.Ordinal) || role.CanDesign())).ToList();
        }
        foreach (var scope in scopes)
            claims.Add(new Claim(McpContextClaims.Scope, scope));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);

        // Fire-and-forget — `last_used_at` is a convenience counter for the
        // Settings UI, not a security signal. ApiKeyRepository only depends
        // on the singleton DatabaseFactory, so it's safe to outlive the scope.
        _ = _repo.TouchLastUsedAsync(key.Id);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
