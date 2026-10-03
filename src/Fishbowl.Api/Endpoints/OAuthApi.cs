using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// OAuth 2.1 for MCP clients (space-apps spec phase 7: claude.ai custom
// connectors sign in with OAuth). The smallest complete server the MCP
// authorization spec asks for:
//   GET  /.well-known/oauth-protected-resource[/mcp]   RFC 9728 — where to sign in for /mcp
//   GET  /.well-known/oauth-authorization-server        RFC 8414 — the endpoints below
//   POST /oauth/register                                RFC 7591 — dynamic client registration (public clients)
//   GET  /oauth/authorize                               the consent page (signed in; else /login and back)
//   GET  /api/v1/oauth/request                          what the consent page shows
//   POST /api/v1/oauth/authorize                        the person's decision → a one-time code (PKCE S256)
//   POST /oauth/token                                   code + verifier → the access token
// The access token is an ordinary API key (fb_live_…) bound to the workspace
// and access the person chose — listed and revocable on the API keys page,
// trimmed to the owner's role like every space key. No refresh tokens: the
// key lives until it is revoked.
public static class OAuthApi
{
    public const int MaxClients = 1000;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public sealed record RegisterRequest(
        [property: JsonPropertyName("client_name")] string? ClientName,
        [property: JsonPropertyName("redirect_uris")] string[]? RedirectUris);

    public sealed record AuthorizeRequest(string? ClientId, string? RedirectUri, string? CodeChallenge, string? State,
        string? Workspace, string? Access);

    public static string Origin(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

    // What each access level grants (the key owner's role still trims it).
    public static IReadOnlyList<string> ScopesFor(string access, bool space)
    {
        var read = new List<string> { ScopeCatalog.ReadNotes, ScopeCatalog.ReadTags, ScopeCatalog.ReadTasks, ScopeCatalog.ReadContacts, ScopeCatalog.ReadEvents, ScopeCatalog.ReadFiles };
        if (space) read.Add(ScopeCatalog.ReadTables);
        if (access == "read") return read;
        var write = read.Concat(new[] { ScopeCatalog.WriteNotes, ScopeCatalog.WriteTags, ScopeCatalog.WriteTasks, ScopeCatalog.WriteContacts, ScopeCatalog.WriteEvents, ScopeCatalog.WriteFiles }).ToList();
        if (space) write.Add(ScopeCatalog.WriteTables);
        if (access == "write" || !space) return write;
        return write.Concat(new[] { ScopeCatalog.DesignTables, ScopeCatalog.DesignApps }).ToList();   // "build"
    }

    public static IEndpointRouteBuilder MapOAuthApi(this IEndpointRouteBuilder routes)
    {
        IResult Resource(HttpContext http) => Results.Json(new
        {
            resource = $"{Origin(http)}/mcp",
            authorization_servers = new[] { Origin(http) },
            bearer_methods_supported = new[] { "header" },
            scopes_supported = ScopeCatalog.All,
        });
        routes.MapGet("/.well-known/oauth-protected-resource", Resource).ExcludeFromDescription();
        routes.MapGet("/.well-known/oauth-protected-resource/mcp", Resource).ExcludeFromDescription();

        routes.MapGet("/.well-known/oauth-authorization-server", (HttpContext http) => Results.Json(new
        {
            issuer = Origin(http),
            authorization_endpoint = $"{Origin(http)}/oauth/authorize",
            token_endpoint = $"{Origin(http)}/oauth/token",
            registration_endpoint = $"{Origin(http)}/oauth/register",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" },
            scopes_supported = ScopeCatalog.All,
        })).ExcludeFromDescription();

        routes.MapPost("/oauth/register", async (RegisterRequest body, IOAuthRepository repo, CancellationToken ct) =>
        {
            var uris = (body.RedirectUris ?? Array.Empty<string>()).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();
            if (uris.Count == 0 || uris.Count > 10 || !uris.All(IsAllowedRedirect))
                return OAuthError(400, "invalid_redirect_uri", "redirect_uris: 1–10 https URLs (http only on localhost).");
            if (await repo.CountClientsAsync(ct) >= MaxClients)
                return OAuthError(400, "invalid_client_metadata", "This Fishbowl takes no more client registrations.");
            var name = string.IsNullOrWhiteSpace(body.ClientName) ? "MCP client" : body.ClientName.Trim()[..Math.Min(80, body.ClientName.Trim().Length)];
            var client = await repo.RegisterClientAsync(name, uris, ct);
            return Results.Json(new
            {
                client_id = client.ClientId,
                client_name = client.Name,
                redirect_uris = client.RedirectUris,
                token_endpoint_auth_method = "none",
                grant_types = new[] { "authorization_code" },
                response_types = new[] { "code" },
                client_id_issued_at = new DateTimeOffset(client.CreatedAt).ToUnixTimeSeconds(),
            }, statusCode: 201);
        }).ExcludeFromDescription();

        // The consent page: signed in, else sign in and come back.
        routes.MapGet("/oauth/authorize", async (HttpContext http, IResourceProvider resources, CancellationToken ct) =>
        {
            if (http.User.Identity?.IsAuthenticated != true || http.User.Identity.AuthenticationType == McpContextClaims.BearerScheme)
                return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(http.Request.Path + http.Request.QueryString));
            var page = await resources.GetAsync("oauth.html", ct);
            return page is null ? Results.NotFound() : Results.Bytes(page.Data, "text/html");
        }).ExcludeFromDescription();

        // What the consent page shows: who asks, where it sends you back, the workspaces you may give.
        routes.MapGet("/api/v1/oauth/request", async (string? client_id, string? redirect_uri, HttpContext http,
            IOAuthRepository repo, ISpaceRepository spaces, CancellationToken ct) =>
        {
            if (Me(http) is not { } me) return Results.Unauthorized();
            var client = client_id is null ? null : await repo.GetClientAsync(client_id, ct);
            if (client is null || redirect_uri is null || !client.RedirectUris.Contains(redirect_uri))
                return ApiErrors.BadRequest("oauth_invalid_request", "This sign-in link isn't valid — start again from the app.");
            var memberships = await spaces.ListByMemberAsync(me, ct);
            return Results.Ok(new
            {
                client = client.Name,
                redirectHost = new Uri(redirect_uri).Host,
                workspaces = new[] { new { id = "personal", name = (string?)null, role = (string?)"owner" } }
                    .Concat(memberships.Select(m => new { id = "space:" + m.Space.Slug, name = (string?)m.Space.Name, role = (string?)m.Role.ToDbValue() })),
            });
        }).RequireAuthorization().ExcludeFromDescription();

        routes.MapPost("/api/v1/oauth/authorize", async (AuthorizeRequest body, HttpContext http, IOAuthRepository repo,
            ISpaceRepository spaces, CancellationToken ct) =>
        {
            if (Me(http) is not { } me) return Results.Unauthorized();
            var client = body.ClientId is null ? null : await repo.GetClientAsync(body.ClientId, ct);
            if (client is null || body.RedirectUri is null || !client.RedirectUris.Contains(body.RedirectUri))
                return ApiErrors.BadRequest("oauth_invalid_request", "This sign-in link isn't valid — start again from the app.");
            if (string.IsNullOrEmpty(body.CodeChallenge) || body.CodeChallenge.Length is < 43 or > 128)
                return ApiErrors.BadRequest("oauth_invalid_request", "The app didn't send a PKCE code challenge (S256).");
            if (body.Access is not ("read" or "write" or "build"))
                return ApiErrors.BadRequest("invalid_value", "access is read, write or build", new { field = "access" });

            ContextRef ctx;
            var space = false;
            if (body.Workspace == "personal") ctx = ContextRef.User(me);
            else if (body.Workspace?.StartsWith("space:", StringComparison.Ordinal) == true)
            {
                var s = await spaces.GetBySlugAsync(body.Workspace[6..], ct);
                if (s is null || await spaces.GetMembershipAsync(s.Id, me, ct) is null)
                    return ApiErrors.BadRequest("invalid_value", "Pick one of your workspaces.", new { field = "workspace" });
                ctx = ContextRef.Space(s.Slug);   // a space key names its space by slug
                space = true;
            }
            else return ApiErrors.BadRequest("invalid_value", "Pick one of your workspaces.", new { field = "workspace" });

            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await repo.SaveCodeAsync(new OAuthCode(Hash(code), client.ClientId, me, space ? "space" : "user", ctx.Id,
                ScopesFor(body.Access, space), body.RedirectUri, body.CodeChallenge, DateTime.UtcNow + CodeLifetime), ct);
            var sep = body.RedirectUri.Contains('?') ? '&' : '?';
            var redirect = $"{body.RedirectUri}{sep}code={code}" + (string.IsNullOrEmpty(body.State) ? "" : $"&state={Uri.EscapeDataString(body.State)}");
            return Results.Ok(new { redirect });
        }).RequireAuthorization().ExcludeFromDescription();

        routes.MapPost("/oauth/token", async (HttpContext http, IOAuthRepository repo, IApiKeyRepository keys, CancellationToken ct) =>
        {
            if (!http.Request.HasFormContentType) return OAuthError(400, "invalid_request", "Send the token request as application/x-www-form-urlencoded.");
            var form = await http.Request.ReadFormAsync(ct);
            if (form["grant_type"] != "authorization_code") return OAuthError(400, "unsupported_grant_type", "Only authorization_code.");
            var code = form["code"].ToString();
            var verifier = form["code_verifier"].ToString();
            if (code.Length == 0 || verifier.Length is < 43 or > 128) return OAuthError(400, "invalid_request", "code and code_verifier are required.");
            var taken = await repo.TakeCodeAsync(Hash(code), ct);
            if (taken is null) return OAuthError(400, "invalid_grant", "The code is unknown, used or expired.");
            if (taken.ClientId != form["client_id"].ToString() || taken.RedirectUri != form["redirect_uri"].ToString())
                return OAuthError(400, "invalid_grant", "client_id or redirect_uri doesn't match the code.");
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge), Encoding.ASCII.GetBytes(taken.CodeChallenge)))
                return OAuthError(400, "invalid_grant", "The code_verifier doesn't match.");

            var client = await repo.GetClientAsync(taken.ClientId, ct);
            var ctx = taken.ContextType == "space" ? ContextRef.Space(taken.ContextId) : ContextRef.User(taken.ContextId);
            var issued = await keys.IssueAsync(taken.UserId, ctx, $"{client?.Name ?? "MCP client"} (OAuth)", taken.Scopes, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { access_token = issued.RawToken, token_type = "Bearer", scope = string.Join(' ', taken.Scopes) });
        }).DisableAntiforgery().ExcludeFromDescription();

        return routes;
    }

    private static string? Me(HttpContext http) =>
        http.User.Identity?.AuthenticationType == McpContextClaims.BearerScheme ? null : http.User.FindFirst(McpContextClaims.UserId)?.Value;

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsAllowedRedirect(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && string.IsNullOrEmpty(u.Fragment)
        && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

    private static IResult OAuthError(int status, string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: status);
}
