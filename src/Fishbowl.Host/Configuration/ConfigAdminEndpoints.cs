using System.Security.Claims;
using Fishbowl.Api;
using Fishbowl.Api.Endpoints;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Desktop;
using Fishbowl.Core.Files;
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
            IUserAdminRepository admin,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(user, system, ct)) return Results.Forbid();

            var spec = ConfigSchema.Find(key);
            if (spec is null)
                return ApiErrors.NotFound("unknown_config_key", "Unknown or non-editable config key.", new { field = key });

            var value = request?.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(value))
                return ApiErrors.BadRequest("value_required", "Value required (use DELETE to clear).", new { field = spec.Key });

            // With `urls` set the boot skips ACME (Program.cs): saying yes to
            // a Let's Encrypt key would promise something that never happens.
            if (spec.Key.StartsWith("Acme:", StringComparison.Ordinal) && !string.IsNullOrEmpty(configuration["urls"]))
                return ConfigSchema.AcmeBehindUrls.ToResult(spec.Key);

            var err = spec.Validate(value);
            if (err is not null) return err.ToResult(spec.Key);

            await system.SetConfigAsync(spec.Key, value, ct);
            cache.Set(spec.Key, value);
            await admin.RecordAdminActionAsync(ActorId(user), AdminActions.ConfigSet, "config", spec.Key, ct);

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
            IUserAdminRepository admin,
            CancellationToken ct) =>
        {
            if (!await IsCookieAdminAsync(user, system, ct)) return Results.Forbid();

            var spec = ConfigSchema.Find(key);
            if (spec is null)
                return ApiErrors.NotFound("unknown_config_key", "Unknown or non-editable config key.", new { field = key });

            // Empty-string clear, not row delete: lookups go through the cache,
            // which treats empty-string the same as null (`placeholder`). Keeps
            // the audit trail of "this key existed once" via `updated_at`.
            await system.SetConfigAsync(spec.Key, string.Empty, ct);
            cache.Set(spec.Key, null);
            await admin.RecordAdminActionAsync(ActorId(user), AdminActions.ConfigClear, "config", spec.Key, ct);
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

    private static string ActorId(ClaimsPrincipal user) =>
        user.FindFirst(McpContextClaims.UserId)?.Value ?? "";

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

/// <summary>A refused config value: a code + arguments for the client's
/// i18n table, and the English message agents and logs read.</summary>
internal sealed record ConfigError(string Code, string Message, object? Args = null)
{
    public IResult ToResult(string key) =>
        ApiErrors.BadRequest(Code, Message,
            ApiErrors.Args(Args).Prepend(new KeyValuePair<string, object?>("field", key)).ToList());
}

internal static class ConfigSchema
{
    public sealed record KeySpec(
        string Key,
        bool Secret,
        bool RestartRequired,
        string Description,
        Func<string, ConfigError?> Validate);

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
        new(SignUpPolicy.ModeKey, false, false,
            "Who may create an account: approval (default — an admin approves each new one), open, or closed. Hot.",
            ValidateSignUpMode),
        new(SignUpPolicy.AllowedDomainsKey, false, false,
            "Comma-separated e-mail domains new accounts must come from (empty = any). Existing accounts are unaffected. Hot.",
            ValidateEmailDomains),
        new(FileLimits.DefaultUserQuotaBytesKey, false, false,
            "The storage quota (bytes, 0 = unlimited) an approval pre-fills for a new account. Hot.",
            ValidateQuotaBytes),
        new(DataLifecycleLimits.ArchiveRetentionDaysKey, false, false,
            "Days an archived space is kept before the daily tick deletes it (default 30, 0 = keep forever). Hot.",
            ValidateArchiveDays),
        new(DataLifecycleLimits.ExportCombinedMaxBytesKey, false, false,
            "Files size (bytes) up to which the export offers database + files in one ZIP (default 4 GiB, 0 = always). Hot.",
            ValidateQuotaBytes),
        new(DesktopPolicy.InstallKey, false, false,
            "Who may install apps into their personal desktop: everyone (default), admins, or off. A space's owner installs into the space unless this is off. Hot.",
            ValidateAppsLevel),
        new(DesktopPolicy.AllowedOriginsKey, false, false,
            "Comma-separated origins apps may be installed from (e.g. https://owner.github.io); empty = any. Hot.",
            ValidateAppOrigins),
        new(DesktopPolicy.StoreOwnersKey, false, false,
            "Comma-separated GitHub owners the App Store tab lists (default SACRVM). Hot.",
            ValidateStoreOwners),
        new(DesktopPolicy.StoreTopicKey, false, false,
            "The GitHub repo topic that marks an app for the App Store tab (default sacrvm-app). Hot.",
            ValidateStoreTopic),
        new("Digest:Enabled", false, false,
            "The daily \"here's your day\" DM to every user with a linked chat channel: true or false (default false). Hot.",
            ValidateBool),
        new("Digest:Hour", false, false,
            "Server-local hour (0–23) the daily digest goes out at (default 7). Hot.",
            ValidateHour),
        new("Logging:RetentionDays", false, true,
            "Days of daily log files to keep (1–365, default 7). Restart required.",
            ValidateLogRetentionDays),
        new("Logging:Format", false, true,
            "Log file format: plain (default) or json. Restart required.",
            ValidateLogFormat),
    };

    public static KeySpec? Find(string key) =>
        Editable.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.Ordinal));

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length <= 8) return new string('*', value.Length);
        return $"{value[..3]}…****{value[^4..]}";
    }

    // Let's Encrypt keys while the listening addresses come from `urls`
    // (host.config.json, --urls, ASPNETCORE_URLS): a reverse proxy owns the
    // public ports and HTTPS there, and the boot skips ACME.
    public static readonly ConfigError AcmeBehindUrls =
        new("acme_urls_set", "This Fishbowl listens where its urls setting says (host.config.json) — HTTPS is the reverse proxy's job there, so Let's Encrypt settings would be ignored. Remove urls first to use them.");

    // Shared refusals: one code each, the English sentence as before.
    private static ConfigError OneOf(params string[] allowed) =>
        new("one_of", $"Must be one of: {string.Join(", ", allowed)}.", new { allowed = string.Join(", ", allowed) });
    private static ConfigError ListEmpty(string message) => new("list_empty", message);

    private static ConfigError? ValidateGoogleClientId(string v) =>
        v.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal)
            ? null
            : new("google_client_id", "ClientId must end with .apps.googleusercontent.com");

    private static ConfigError? ValidateGoogleClientSecret(string v) =>
        v.Length >= 20 ? null : new("min_length", "ClientSecret must be at least 20 characters.", new { min = 20 });

    private static ConfigError? ValidateAcmeDomains(string v)
    {
        var domains = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (domains.Length == 0) return ListEmpty("Provide at least one comma-separated domain.");
        foreach (var d in domains)
        {
            if (Uri.CheckHostName(d) != UriHostNameType.Dns)
                return new("invalid_hostname", $"Not a valid DNS hostname: {d}", new { value = d });
        }
        return null;
    }

    private static ConfigError? ValidateAcmeEmail(string v) =>
        v.Contains('@') && v.IndexOf('@') > 0 && v.IndexOf('@') < v.Length - 1
            ? null
            : new("invalid_email", "Email must be a valid address (contains @ with text on both sides).");

    private static ConfigError? ValidateAcmeTos(string v) =>
        v is "true" or "false"
            ? null
            : new("one_of", "Acme:AcceptTos must be exactly the string \"true\" or \"false\".", new { allowed = "true, false" });

    private static ConfigError? ValidateSignUpMode(string v) =>
        SignUpPolicy.Modes.Contains(v)
            ? null
            : new("one_of", "Auth:SignUp must be one of: approval, open, closed.", new { allowed = "approval, open, closed" });

    private static ConfigError? ValidateEmailDomains(string v)
    {
        var domains = SignUpPolicy.ParseDomains(v);
        if (domains.Count == 0) return ListEmpty("Provide at least one comma-separated domain (use DELETE to allow any).");
        foreach (var d in domains)
        {
            if (Uri.CheckHostName(d) != UriHostNameType.Dns || !d.Contains('.'))
                return new("invalid_email_domain", $"Not a valid e-mail domain: {d}", new { value = d });
        }
        return null;
    }

    private static ConfigError? ValidateArchiveDays(string v) =>
        int.TryParse(v, out var n) && n >= 0
            ? null
            : new("whole_number_min", "Retention must be a whole number of days, 0 or more (0 = keep forever).", new { min = 0 });

    private static ConfigError? ValidateQuotaBytes(string v) =>
        long.TryParse(v, out var n) && n >= 0
            ? null
            : new("whole_number_min", "Quota must be a whole number of bytes, 0 or more (0 = unlimited).", new { min = 0 });

    private static ConfigError? ValidateAppsLevel(string v) =>
        DesktopPolicy.Levels.Contains(v) ? null : OneOf("everyone", "admins", "off");

    private static ConfigError? ValidateAppOrigins(string v)
    {
        var entries = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0) return ListEmpty("Provide at least one comma-separated origin (use DELETE to allow any).");
        foreach (var e in entries)
        {
            if (!AppOrigins.TryParseExact(e, out _))
                return new("invalid_origin", $"Not an https origin (scheme and host only): {e}", new { value = e });
        }
        return null;
    }

    private static ConfigError? ValidateStoreOwners(string v)
    {
        var owners = DesktopPolicy.ParseList(v);
        if (owners.Count == 0) return ListEmpty("Provide at least one GitHub owner.");
        foreach (var o in owners)
        {
            if (o.Length > 39 || !o.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || o.StartsWith('-'))
                return new("invalid_github_owner", $"Not a GitHub owner name: {o}", new { value = o });
        }
        return null;
    }

    private static ConfigError? ValidateStoreTopic(string v) =>
        v.Length is > 0 and <= 50 && v.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
            ? null
            : new("invalid_topic", "A topic is lower-case letters, digits and hyphens (max 50).", new { max = 50 });

    private static ConfigError? ValidateBool(string v) =>
        v is "true" or "false" ? null : new("one_of", "Must be exactly \"true\" or \"false\".", new { allowed = "true, false" });

    private static ConfigError? ValidateHour(string v) =>
        int.TryParse(v, out var h) && h is >= 0 and <= 23
            ? null
            : new("whole_number_range", "Hour must be a whole number from 0 to 23.", new { min = 0, max = 23 });

    private static ConfigError? ValidateLogRetentionDays(string v) =>
        int.TryParse(v, out var d) && d is >= 1 and <= 365
            ? null
            : new("whole_number_range", "Retention must be a whole number of days from 1 to 365.", new { min = 1, max = 365 });

    private static ConfigError? ValidateLogFormat(string v) =>
        v is "plain" or "json" ? null : new("one_of", "Format must be plain or json.", new { allowed = "plain, json" });

    private static ConfigError? ValidateDiscordToken(string v)
    {
        if (v.Length < 50) return new("discord_token_short", "Discord bot token looks too short — they're ~70 characters.");
        if (v.Count(c => c == '.') < 2) return new("discord_token_dots", "Discord bot token should contain two '.' separators.");
        return null;
    }
}
