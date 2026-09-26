using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Desktop;
using Fishbowl.Core.Files;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Fishbowl.Api.Endpoints;

// The desktop's REST surface (docs/superpowers/specs/2026-09-26-desktop-apps-design.md).
// Personal under /api/v1/desktop, the space mirror under
// /api/v1/spaces/{slug}/desktop. Tiles are the workspace's arrangement;
// apps are what its owner installed.
//
// Cookie-only: installing code is a human act, never an agent's (Bearer
// 403). In a space every member reads, only the owner writes — sharing a
// space shares its apps. The server never fetches a foreign URL: it
// validates the manifest and entry hash the owner's browser read and posts,
// against the admin's policy (DesktopPolicy) and the mode rules:
//   sandboxed  the default; needs the entry's SRI hash (the pin)
//   trusted    personal desktops only, when Apps:Trusted allows; no pin
//
// GET /apps/frame/{ctxType}/{ctxId}/{appId} serves a sandboxed app's frame
// document: the kit, the guest bridge and the pinned entry, under a per-app
// CSP (FrameCsp). Anything it can't serve is a 404, never a hint.
public static class DesktopApi
{
    public sealed record TileRequest(double? Position, string? Size, string? Color, bool? Hidden);
    public sealed record InstallRequest(string? ManifestUrl, JsonElement Manifest, string? Integrity, string? Mode, string[]? Granted);
    public sealed record UpdateRequest(string? ManifestUrl, JsonElement? Manifest, string? Integrity, string? Mode, string[]? Granted);

    public const string GuestScript = "/js/lib/app-guest.js";

    public static IEndpointRouteBuilder MapDesktopApi(this IEndpointRouteBuilder routes)
    {
        Map(routes.MapGroup("/api/v1/desktop").RequireAuthorization());
        Map(routes.MapGroup("/api/v1/spaces/{slug}/desktop").RequireAuthorization());
        routes.MapGet("/apps/frame/{ctxType}/{ctxId}/{appId}", FrameAsync)
            .WithName("GetAppFrame")
            .WithSummary("A sandboxed app's frame document with its per-app CSP. Cookie only.")
            .ExcludeFromDescription();
        return routes;
    }

    private sealed record Target(ContextRef Ctx, string UserId, bool IsSpace, bool CanWrite);

    private static bool IsBearer(ClaimsPrincipal user) => user.Identity?.AuthenticationType == McpContextClaims.BearerScheme;

    private static IResult Error(int status, string code, string message, string? field = null) =>
        Results.Json(new { error = code, message, field }, statusCode: status);

    private static async Task<(Target? Target, IResult? Error)> ResolveAsync(HttpContext http, CancellationToken ct)
    {
        var user = http.User;
        if (IsBearer(user)) return (null, Results.Forbid());
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (string.IsNullOrEmpty(userId)) return (null, Results.Unauthorized());

        if (http.Request.RouteValues["slug"] is not string slug)
            return (new Target(ContextRef.User(userId), userId, IsSpace: false, CanWrite: true), null);

        var spaces = http.RequestServices.GetRequiredService<ISpaceRepository>();
        var resolved = await SpacesApi.ResolveSpaceAsync(slug, user, spaces, ct);
        if (resolved.Error is not null) return (null, resolved.Error);
        return (new Target(ContextRef.Space(resolved.Space!.Id), userId, IsSpace: true,
            CanWrite: resolved.Role == SpaceRole.Owner), null);
    }

    // What the admin's policy lets this caller do in this workspace.
    private sealed record Allowance(bool Install, bool Trust, IReadOnlyList<string> AllowedOrigins);

    private static async Task<Allowance> AllowanceAsync(HttpContext http, Target t, CancellationToken ct)
    {
        var system = http.RequestServices.GetRequiredService<ISystemRepository>();
        var install = DesktopPolicy.ParseLevel(await system.GetConfigAsync(DesktopPolicy.InstallKey, ct));
        var trusted = DesktopPolicy.ParseLevel(await system.GetConfigAsync(DesktopPolicy.TrustedKey, ct));
        var origins = DesktopPolicy.ParseOrigins(await system.GetConfigAsync(DesktopPolicy.AllowedOriginsKey, ct));
        var isAdmin = (await system.GetUserAsync(t.UserId, ct))?.IsAdmin == true;

        // A space desktop is its owner's call; only `off` stops it there.
        var canInstall = t.CanWrite && (t.IsSpace ? install != DesktopPolicy.Off : DesktopPolicy.Allows(install, isAdmin));
        var canTrust = canInstall && !t.IsSpace && DesktopPolicy.Allows(trusted, isAdmin);
        return new Allowance(canInstall, canTrust, origins);
    }

    private static object Dto(DesktopApp a) => new
    {
        id = a.Id,
        manifestUrl = a.ManifestUrl,
        manifest = JsonSerializer.Deserialize<JsonElement>(a.Manifest),
        origin = a.Origin,
        entryUrl = a.EntryUrl,
        entryIntegrity = a.EntryIntegrity,
        version = a.Version,
        mode = a.Mode,
        granted = a.Granted,
        dataFolder = AppDataFolders.For(NameOf(a), a.Id),
        installedAt = a.InstalledAt,
        updatedAt = a.UpdatedAt,
    };

    private static string NameOf(DesktopApp a)
    {
        using var doc = JsonDocument.Parse(a.Manifest);
        return doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : a.Id;
    }

    private static object TileDto(DesktopTile t) => new
    {
        key = t.Key,
        position = t.Position,
        size = t.Size,
        color = t.Color,
        hidden = t.Hidden,
    };

    private static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", async (HttpContext http, IDesktopRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, ct);
            if (err is not null) return err;
            var allow = await AllowanceAsync(http, t!, ct);
            var tiles = await repo.ListTilesAsync(t!.Ctx, ct);
            var apps = await repo.ListAppsAsync(t.Ctx, ct);
            return Results.Ok(new
            {
                tiles = tiles.Select(TileDto),
                apps = apps.Select(Dto),
                canArrange = t.CanWrite,
                canInstall = allow.Install,
                canTrust = allow.Trust,
            });
        });

        g.MapPut("/tiles/{key}", async (string key, TileRequest body, HttpContext http, IDesktopRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, ct);
            if (err is not null) return err;
            if (!t!.CanWrite) return Results.Forbid();
            if (!DesktopTiles.IsValidKey(key)) return Error(400, "invalid_tile", "Tile keys are lower-case, like builtin:notes or app:<id>.", "key");
            if (body.Size is not null && !DesktopTiles.Sizes.Contains(body.Size))
                return Error(400, "invalid_tile", "size is medium, wide or large.", "size");
            if (body.Color is not null && !TagPalette.IsSlot(body.Color))
                return Error(400, "invalid_tile", "color must be a palette slot.", "color");
            if (body.Position is double p && (double.IsNaN(p) || double.IsInfinity(p)))
                return Error(400, "invalid_tile", "position must be a finite number.", "position");
            var tile = new DesktopTile(key, body.Position, body.Size, body.Color, body.Hidden ?? false);
            await repo.UpsertTileAsync(t.Ctx, tile, ct);
            return Results.Ok(TileDto(tile));
        });

        g.MapPost("/apps", async (InstallRequest body, HttpContext http, IDesktopRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, ct);
            if (err is not null) return err;
            if (!t!.CanWrite) return Results.Forbid();
            var allow = await AllowanceAsync(http, t, ct);
            if (!allow.Install) return Error(403, "install_not_allowed", "Installing apps isn't allowed here.");

            var mode = body.Mode ?? AppModes.Sandboxed;
            ValidatedManifest m;
            IReadOnlyList<string> granted;
            try
            {
                m = AppManifestValidator.Validate(body.ManifestUrl, body.Manifest);
                var modeError = CheckMode(mode, t, allow, body.Integrity);
                if (modeError is not null) return modeError;
                if (!DesktopPolicy.IsOriginAllowed(m.Origin, allow.AllowedOrigins))
                    return Error(403, "origin_not_allowed", "Apps from this origin aren't allowed on this Fishbowl.", "manifestUrl");
                granted = mode == AppModes.Trusted ? Array.Empty<string>() : AppManifestValidator.ValidateGrants(body.Granted, m.Permissions);
            }
            catch (DesktopValidationException ex) { return Error(400, ex.Code, ex.Message, ex.Field); }

            var now = DateTime.UtcNow;
            var app = new DesktopApp(m.Id, m.ManifestUrl, m.Json, m.Origin, m.EntryUrl,
                mode == AppModes.Sandboxed ? body.Integrity : null, m.Version, mode, granted, now, now);
            if (!await repo.InsertAppAsync(t.Ctx, app, ct))
                return Error(409, "already_installed", "An app with this id is already installed here.", "id");
            return Results.Created($"{http.Request.Path}/{app.Id}", Dto(app));
        });

        g.MapPatch("/apps/{id}", async (string id, UpdateRequest body, HttpContext http, IDesktopRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, ct);
            if (err is not null) return err;
            if (!t!.CanWrite) return Results.Forbid();
            var current = await repo.GetAppAsync(t.Ctx, id, ct);
            if (current is null) return Results.NotFound();

            var allow = await AllowanceAsync(http, t, ct);
            var newCode = body.Manifest is not null || body.Integrity is not null;
            var mode = body.Mode ?? current.Mode;
            // New code, or more power, is an install decision again.
            if ((newCode || (mode == AppModes.Trusted && current.Mode != AppModes.Trusted)) && !allow.Install)
                return Error(403, "install_not_allowed", "Installing apps isn't allowed here.");

            try
            {
                ValidatedManifest m;
                if (body.Manifest is JsonElement manifest)
                {
                    m = AppManifestValidator.Validate(body.ManifestUrl ?? current.ManifestUrl, manifest);
                    if (m.Id != current.Id)
                        return Error(400, "id_changed", "The manifest belongs to a different app.", "id");
                    if (m.Origin != current.Origin)
                        return Error(400, "origin_changed", "The app moved to another origin — install it as a new app.", "manifestUrl");
                }
                else
                {
                    using var doc = JsonDocument.Parse(current.Manifest);
                    m = AppManifestValidator.Validate(current.ManifestUrl, doc.RootElement);
                }

                // A switch to sandboxed needs a pin; staying sandboxed keeps the
                // old one unless a new hash came with new code.
                var integrity = mode == AppModes.Trusted ? null : body.Integrity ?? (newCode ? null : current.EntryIntegrity);
                if (body.Manifest is not null && mode == AppModes.Sandboxed && body.Integrity is null)
                    return Error(400, "integrity_required", "New code needs its entry hash (re-pin).", "integrity");
                var modeError = CheckMode(mode, t, allow, integrity, switching: mode != current.Mode);
                if (modeError is not null) return modeError;
                if (!DesktopPolicy.IsOriginAllowed(m.Origin, allow.AllowedOrigins))
                    return Error(403, "origin_not_allowed", "Apps from this origin aren't allowed on this Fishbowl.", "manifestUrl");

                var granted = mode == AppModes.Trusted
                    ? Array.Empty<string>()
                    : AppManifestValidator.ValidateGrants(
                        body.Granted ?? current.Granted.Where(g => m.Permissions.Contains(g)), m.Permissions);

                var updated = current with
                {
                    ManifestUrl = m.ManifestUrl,
                    Manifest = m.Json,
                    EntryUrl = m.EntryUrl,
                    EntryIntegrity = integrity,
                    Version = m.Version,
                    Mode = mode,
                    Granted = granted,
                    UpdatedAt = DateTime.UtcNow,
                };
                await repo.UpdateAppAsync(t.Ctx, updated, ct);
                return Results.Ok(Dto(updated));
            }
            catch (DesktopValidationException ex) { return Error(400, ex.Code, ex.Message, ex.Field); }
        });

        g.MapDelete("/apps/{id}", async (string id, bool? purgeData, HttpContext http, IDesktopRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, ct);
            if (err is not null) return err;
            if (!t!.CanWrite) return Results.Forbid();
            var app = await repo.GetAppAsync(t.Ctx, id, ct);
            if (app is null) return Results.NotFound();

            var purged = false;
            if (purgeData == true)
            {
                var files = http.RequestServices.GetRequiredService<IFileService>();
                try
                {
                    await files.DeletePermanentlyAsync(t.Ctx, AppDataFolders.For(NameOf(app), app.Id), t.UserId, ct);
                    purged = true;
                }
                catch (FileStoreException ex) when (ex.Status == 404)
                {
                    // Nothing stored — nothing to purge.
                }
            }
            await repo.DeleteAppAsync(t.Ctx, id, ct);
            return Results.Ok(new { removed = true, purged });
        });
    }

    private static IResult? CheckMode(string mode, Target t, Allowance allow, string? integrity, bool switching = true)
    {
        if (!AppModes.IsValid(mode))
            return Error(400, "invalid_mode", "mode is sandboxed or trusted.", "mode");
        if (mode == AppModes.Trusted)
        {
            if (t.IsSpace)
                return Error(400, "trusted_personal_only", "Trusted apps run as the viewer — a space can only hold sandboxed apps.", "mode");
            if (switching && !allow.Trust)
                return Error(403, "trusted_not_allowed", "The trusted mode isn't allowed on this Fishbowl.", "mode");
            return null;
        }
        if (!AppManifestValidator.IsValidIntegrity(integrity))
            return Error(400, "integrity_required", "A sandboxed app needs its entry's SRI hash (sha256-…).", "integrity");
        return null;
    }

    // ── The frame ─────────────────────────────────────────────────────────

    private static async Task<IResult> FrameAsync(string ctxType, string ctxId, string appId, HttpContext http, IDesktopRepository repo)
    {
        var ct = http.RequestAborted;
        var user = http.User;
        var userId = user.Identity?.IsAuthenticated == true ? user.FindFirst(McpContextClaims.UserId)?.Value : null;
        if (string.IsNullOrEmpty(userId) || IsBearer(user)) return Results.NotFound();

        ContextRef ctx;
        if (ctxType == "user")
        {
            if (!string.Equals(ctxId, userId, StringComparison.Ordinal)) return Results.NotFound();
            ctx = ContextRef.User(userId);
        }
        else if (ctxType == "space")
        {
            var spaces = http.RequestServices.GetRequiredService<ISpaceRepository>();
            var resolved = await SpacesApi.ResolveSpaceAsync(ctxId, user, spaces, ct);
            if (resolved.Error is not null) return Results.NotFound();
            ctx = ContextRef.Space(resolved.Space!.Id);
        }
        else
        {
            return Results.NotFound();
        }

        var app = await repo.GetAppAsync(ctx, appId, ct);
        if (app is null || app.Mode != AppModes.Sandboxed || !AppManifestValidator.IsValidIntegrity(app.EntryIntegrity))
            return Results.NotFound();

        ValidatedManifest m;
        try
        {
            using var doc = JsonDocument.Parse(app.Manifest);
            m = AppManifestValidator.Validate(app.ManifestUrl, doc.RootElement);
        }
        catch (DesktopValidationException) { return Results.NotFound(); }

        string csp;
        try { csp = FrameCsp.Build(app.Origin, m.Connect); }
        catch (ArgumentException) { return Results.NotFound(); }

        var headers = http.Response.Headers;
        headers.ContentSecurityPolicy = csp;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "SAMEORIGIN";
        headers.CacheControl = "no-store";
        return Results.Content(FrameHtml(app, m), "text/html; charset=utf-8", Encoding.UTF8);
    }

    private static string FrameHtml(DesktopApp app, ValidatedManifest m)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        return $"""
            <!DOCTYPE html>
            <html lang="en" data-app-id="{E(app.Id)}" data-app-tag="{E(m.Tag)}" data-app-kind="{E(m.Kind)}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E(m.Name)}</title>
            <link rel="stylesheet" href="/kit/css/ui.css">
            <script defer src="/kit/js/all.js"></script>
            <script defer src="{GuestScript}"></script>
            <script defer src="{E(app.EntryUrl)}" integrity="{E(app.EntryIntegrity)}" crossorigin="anonymous"></script>
            </head>
            <body class="app-page"></body>
            </html>
            """;
    }
}
