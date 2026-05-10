using System.Security.Claims;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Fishbowl.Host.Configuration;

// Admin-scoped read/write for a whitelisted slice of `system_config`. Solves
// the "/setup is 404 after first save" gap: once the operator has committed
// one provider, the setup wizard locks itself out, but a Google secret may
// still need rotation, a domain may need to change, a Discord token may
// need re-pasting. This endpoint is the supported escape hatch.
//
// Cookie + IsAdmin gated. Bearer always 403 — system_config is host-level
// wiring, not a data scope. Hot keys (Google:*) take effect immediately
// because OptionsMonitor re-binds from ConfigurationCache; ACME + Discord
// flag `restartRequired: true` because they bind at boot.
//
// Lives in Fishbowl.Host (not Fishbowl.Api) because it depends on
// ConfigurationCache + IOptionsMonitorCache<GoogleOptions>, both of which
// are host-owned. Mirrors the way /api/setup is wired directly in Program.cs.
public static class ConfigAdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminConfigEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/admin/config");

        group.MapGet("/", async (
            ClaimsPrincipal user,
            ISystemRepository system,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(user, system, ct)) return Results.Forbid();

            var rows = new List<object>();
            foreach (var spec in ConfigSchema.Editable)
            {
                var raw = await system.GetConfigAsync(spec.Key, ct);
                var isSet = !string.IsNullOrEmpty(raw) && raw != "placeholder";
                rows.Add(new
                {
                    key = spec.Key,
                    value = isSet ? (spec.Secret ? ConfigSchema.Redact(raw!) : raw) : null,
                    isSet,
                    secret = spec.Secret,
                    restartRequired = spec.RestartRequired,
                    description = spec.Description,
                });
            }
            return Results.Ok(rows);
        })
        .WithName("ListAdminConfig")
        .WithSummary("Lists editable system_config keys with redacted values for secrets.")
        .RequireAuthorization();

        group.MapPut("/{key}", async (
            string key,
            SetConfigRequest request,
            ClaimsPrincipal user,
            ISystemRepository system,
            ConfigurationCache cache,
            IOptionsMonitorCache<GoogleOptions> googleCache,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(user, system, ct)) return Results.Forbid();

            var spec = ConfigSchema.Find(key);
            if (spec is null)
                return Results.NotFound(new { error = "Unknown or non-editable config key." });

            var value = request?.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(value))
                return Results.BadRequest(new { error = "Value required (use DELETE to clear)." });

            var err = spec.Validate(value);
            if (err is not null) return Results.BadRequest(new { error = err });

            await system.SetConfigAsync(spec.Key, value, ct);
            cache.Set(spec.Key, value);

            // Force-rebuild AspNetCore's GoogleOptions snapshot so the next
            // challenge uses the new credentials. Without this, an in-flight
            // monitor would happily hand out the stale "placeholder" tuple.
            if (spec.Key.StartsWith("Google:", StringComparison.Ordinal))
                googleCache.Clear();

            return Results.Ok(new
            {
                key = spec.Key,
                updated = true,
                restartRequired = spec.RestartRequired,
            });
        })
        .WithName("UpdateAdminConfig")
        .WithSummary("Updates a single system_config key. Hot keys take effect immediately; restart-required keys are flagged in the response.")
        .RequireAuthorization();

        group.MapDelete("/{key}", async (
            string key,
            ClaimsPrincipal user,
            ISystemRepository system,
            ConfigurationCache cache,
            IOptionsMonitorCache<GoogleOptions> googleCache,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(user, system, ct)) return Results.Forbid();

            var spec = ConfigSchema.Find(key);
            if (spec is null)
                return Results.NotFound(new { error = "Unknown or non-editable config key." });

            // Empty-string clear, not row delete: lookups go through the cache,
            // which treats empty-string the same as null (`placeholder`). Keeps
            // the audit trail of "this key existed once" via `updated_at`.
            await system.SetConfigAsync(spec.Key, string.Empty, ct);
            cache.Set(spec.Key, null);
            if (spec.Key.StartsWith("Google:", StringComparison.Ordinal))
                googleCache.Clear();

            return Results.Ok(new
            {
                key = spec.Key,
                cleared = true,
                restartRequired = spec.RestartRequired,
            });
        })
        .WithName("ClearAdminConfig")
        .WithSummary("Clears a single system_config key (sets value to empty).")
        .RequireAuthorization();

        return routes;
    }

    private static async Task<bool> IsCookieAdminAsync(
        ClaimsPrincipal user, ISystemRepository system, CancellationToken ct)
    {
        if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return false;
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (string.IsNullOrEmpty(userId)) return false;
        var profile = await system.GetUserAsync(userId, ct);
        return profile?.IsAdmin == true;
    }
}

public sealed record SetConfigRequest(string Value);

internal static class ConfigSchema
{
    public sealed record KeySpec(
        string Key,
        bool Secret,
        bool RestartRequired,
        string Description,
        Func<string, string?> Validate);

    public static readonly KeySpec[] Editable =
    {
        new("Google:ClientId", false, false,
            "Google OAuth client id. Hot — applies on next OAuth challenge.",
            ValidateGoogleClientId),
        new("Google:ClientSecret", true, false,
            "Google OAuth client secret. Hot — applies on next OAuth challenge.",
            ValidateGoogleClientSecret),
        new("Acme:Domains", false, true,
            "Comma-separated DNS hostnames for Let's Encrypt issuance. Restart required (LettuceEncrypt binds at boot).",
            ValidateAcmeDomains),
        new("Acme:Email", false, true,
            "Contact email for Let's Encrypt. Restart required.",
            ValidateAcmeEmail),
        new("Acme:AcceptTos", false, true,
            "Operator acceptance of the Let's Encrypt subscriber agreement (literal \"true\"). Restart required.",
            ValidateAcmeTos),
        new("Discord:BotToken", true, true,
            "Discord bot token. Restart required (gateway connection binds at host start).",
            ValidateDiscordToken),
    };

    public static KeySpec? Find(string key) =>
        Editable.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.Ordinal));

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length <= 8) return new string('*', value.Length);
        return $"{value[..3]}…****{value[^4..]}";
    }

    private static string? ValidateGoogleClientId(string v) =>
        v.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal)
            ? null
            : "ClientId must end with .apps.googleusercontent.com";

    private static string? ValidateGoogleClientSecret(string v) =>
        v.Length >= 20 ? null : "ClientSecret must be at least 20 characters.";

    private static string? ValidateAcmeDomains(string v)
    {
        var domains = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (domains.Length == 0) return "Provide at least one comma-separated domain.";
        foreach (var d in domains)
        {
            if (Uri.CheckHostName(d) != UriHostNameType.Dns)
                return $"Not a valid DNS hostname: {d}";
        }
        return null;
    }

    private static string? ValidateAcmeEmail(string v) =>
        v.Contains('@') && v.IndexOf('@') > 0 && v.IndexOf('@') < v.Length - 1
            ? null
            : "Email must be a valid address (contains @ with text on both sides).";

    private static string? ValidateAcmeTos(string v) =>
        v is "true" or "false"
            ? null
            : "Acme:AcceptTos must be exactly the string \"true\" or \"false\".";

    private static string? ValidateDiscordToken(string v)
    {
        if (v.Length < 50) return "Discord bot token looks too short — they're ~70 characters.";
        if (v.Count(c => c == '.') < 2) return "Discord bot token should contain two '.' separators.";
        return null;
    }
}
