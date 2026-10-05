using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Desktop;
using Fishbowl.Core.Files;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Fishbowl.Api.Endpoints;

// A space's own apps, from its files/.apps/<folder>/app.json (SpaceApps):
// the desktop lists them next to the installed apps (mode "space"), the
// frame document is DesktopApi's (/apps/frame/space/<slug>/space.<folder>,
// cookie), and the code comes from GET /apps/code/<spaceId>/<folder>/<sig>/<file>
// — no cookie (the frame is opaque), the signature is the key. Anything it
// can't serve is a 404.
//
// The error store (AppErrors) under /api/v1/spaces/<slug>/apps/errors:
// GET and DELETE for a Designer (a key needs design:apps), POST for any
// member's browser (cookie) — what broke in the frame, for an app that is
// there, at most ReportsPerHour per person and space.
public static class SpaceAppsApi
{
    // A browser's error reports, per person and space (in memory).
    public const int ReportsPerHour = 60;
    private static readonly HourlyLimit Reports = new(ReportsPerHour);

    public static IEndpointRouteBuilder MapSpaceAppsApi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/apps/code/{spaceId}/{folder}/{sig}/{**file}", CodeAsync)
            .WithName("GetSpaceAppCode")
            .ExcludeFromDescription();

        // The guide the server writes from the live space (SpaceGuide):
        // how to build its apps, its tables, its apps and their errors.
        routes.MapGet("/api/v1/spaces/{slug}/guide", async (string slug, HttpContext http, ISpaceRepository spaces,
            ITableRepository tables, IFileService files, IAppErrorRepository errors, CancellationToken ct) =>
        {
            var resolved = await SpacesApi.ResolveSpaceAsync(slug, http.User, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var md = await SpaceGuide.BuildAsync(resolved.Space!, resolved.Role!.Value, ContextRef.Space(resolved.Space!.Id), tables, files, errors, ct);
            return Results.Text(md, "text/markdown; charset=utf-8");
        }).RequireAuthorization().RequireScope(ScopeCatalog.ReadTables)
          .WithName("GetSpaceGuide").WithSummary("How to build this space's own apps, written from the live space (markdown).");

        // Deploy (space-apps spec phase 7, the deploy-to-fishbowl action): a
        // ZIP of the app's files replaces .apps/<folder> — the old folder goes
        // to the trash first (restorable), then every entry is written. A
        // Designer; a key needs design:apps and write:files.
        routes.MapPost("/api/v1/spaces/{slug}/apps/{folder}/deploy", DeployAsync)
            .RequireAuthorization()
            .WithName("DeploySpaceApp")
            .WithSummary("Replaces .apps/<folder> with the files of a ZIP (the body). Designer; a key needs design:apps and write:files.");

        var g = routes.MapGroup("/api/v1/spaces/{slug}/apps/errors").RequireAuthorization();
        g.MapGet("/", async (string slug, string? app, int? limit, HttpContext http, ISpaceRepository spaces, IAppErrorRepository errors, CancellationToken ct) =>
        {
            var resolved = await SpacesApi.ResolveSpaceAsync(slug, http.User, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanDesign()) return DesignOnly();
            var list = await errors.ListAsync(ContextRef.Space(resolved.Space!.Id), app, limit ?? 100, ct);
            return Results.Ok(new { errors = list.Select(e => new { e.Id, e.At, e.App, e.Kind, e.Message, e.Detail, e.UserId }) });
        }).RequireScope(ScopeCatalog.DesignApps)
          .WithName("ListSpaceAppErrors").WithSummary("A space's app errors, newest first (?app=<folder>, ?limit=). Designer.");

        g.MapPost("/", async (string slug, ReportRequest body, HttpContext http, ISpaceRepository spaces, IAppErrorRepository errors,
            IFileService files, CancellationToken ct) =>
        {
            if (http.User.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return Results.Forbid();
            var resolved = await SpacesApi.ResolveSpaceAsync(slug, http.User, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!SpaceApps.IsFolder(body.App) || body.Kind is null || !AppErrors.FromBrowser.Contains(body.Kind) || string.IsNullOrWhiteSpace(body.Message))
                return ApiErrors.BadRequest("invalid_app_error", "An app error needs app (the folder), kind (load, runtime, call, reported) and message.");
            // Any member may write here and the store keeps only the newest
            // AppErrors.Keep: reports name an app that is there, and each
            // person has a budget per space, so nobody floods out the real ones.
            var ctx = ContextRef.Space(resolved.Space!.Id);
            var userId = http.User.FindFirst(McpContextClaims.UserId)?.Value;
            try { await files.StatAsync(ctx, $"{AppsFolder.Name}/{body.App}", ct); }
            catch (FileStoreException) { return ApiErrors.Json(404, "app_missing", "There is no such app in this space."); }
            if (!Reports.TryTake($"{resolved.Space.Id}/{userId}"))
                return ApiErrors.Json(429, "rate_limited", $"At most {ReportsPerHour} app errors an hour from one person — try again later.");
            await errors.AddAsync(ctx, body.App!, body.Kind, body.Message!, body.Detail, userId, ct);
            return Results.NoContent();
        }).WithName("ReportSpaceAppError").WithSummary("Stores what broke in an app's frame. Cookie only.");

        g.MapDelete("/", async (string slug, string? app, HttpContext http, ISpaceRepository spaces, IAppErrorRepository errors, CancellationToken ct) =>
        {
            var resolved = await SpacesApi.ResolveSpaceAsync(slug, http.User, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (!resolved.Role!.Value.CanDesign()) return DesignOnly();
            return Results.Ok(new { removed = await errors.ClearAsync(ContextRef.Space(resolved.Space!.Id), app, ct) });
        }).RequireScope(ScopeCatalog.DesignApps)
          .WithName("ClearSpaceAppErrors").WithSummary("Clears a space's app errors (?app=<folder> for one). Designer.");
        return routes;
    }

    public const long MaxDeployBytes = 50L * 1024 * 1024;
    public const int MaxDeployFiles = 500;

    private static async Task<IResult> DeployAsync(string slug, string folder, HttpContext http,
        ISpaceRepository spaces, IFileService files, CancellationToken ct)
    {
        var user = http.User;
        if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme
            && !(user.HasClaim(McpContextClaims.Scope, ScopeCatalog.DesignApps) && user.HasClaim(McpContextClaims.Scope, ScopeCatalog.WriteFiles)))
            return Results.Forbid();
        var resolved = await SpacesApi.ResolveSpaceAsync(slug, user, spaces, ct);
        if (resolved.Error is not null) return resolved.Error;
        if (!resolved.Role!.Value.CanDesign()) return DesignOnly();
        if (!SpaceApps.IsFolder(folder))
            return ApiErrors.BadRequest("invalid_value", "The app folder is lower-case letters, digits and -.", new { field = "folder" });
        if (http.Request.ContentLength is > MaxDeployBytes)
            return ApiErrors.Json(413, "too_large", $"A deploy is at most {MaxDeployBytes / (1024 * 1024)} MB.");

        // Read the whole ZIP first (bounded) so a broken one changes nothing.
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxDeployBytes) return ApiErrors.Json(413, "too_large", $"A deploy is at most {MaxDeployBytes / (1024 * 1024)} MB.");
        buffer.Position = 0;
        List<(string Path, byte[] Bytes)> entries;
        try
        {
            using var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
            entries = new();
            foreach (var e in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                var path = e.FullName.Replace('\\', '/');
                if (!SpaceApps.IsFile(path))
                    return ApiErrors.BadRequest("invalid_value", $"\"{path}\" isn't a plain path inside the app folder.", new { field = "zip" });
                if (entries.Count >= MaxDeployFiles)
                    return ApiErrors.BadRequest("invalid_value", $"At most {MaxDeployFiles} files.", new { field = "zip" });
                using var s = e.Open();
                using var m = new MemoryStream();
                await s.CopyToAsync(m, ct);
                entries.Add((path, m.ToArray()));
            }
        }
        catch (InvalidDataException) { return ApiErrors.BadRequest("invalid_value", "The body isn't a ZIP file.", new { field = "zip" }); }
        if (!entries.Any(e => e.Path == SpaceApps.ManifestFile))
            return ApiErrors.BadRequest("invalid_value", "The ZIP has no app.json at its top level.", new { field = "zip" });

        var ctx = ContextRef.Space(resolved.Space!.Id);
        var actor = user.FindFirst(McpContextClaims.UserId)!.Value;
        var root = $"{AppsFolder.Name}/{folder}";
        try { await files.StatAsync(ctx, root, ct); await files.TrashAsync(ctx, root, actor, ct); }
        catch (FileStoreException ex) when (ex.Status == 404) { /* a first deploy */ }
        foreach (var (path, bytes) in entries)
        {
            using var body = new MemoryStream(bytes);
            await files.UploadAsync(ctx, $"{root}/{path}", body, bytes.Length, ifMatch: null, ifNoneMatchStar: true, parents: true, actor, ct);
        }
        return Results.Ok(new { app = SpaceApps.IdOf(folder), files = entries.Count });
    }

    public sealed record ReportRequest(string? App, string? Kind, string? Message, string? Detail);

    private static IResult DesignOnly() =>
        Results.Json(new { error = "apps_design_only", message = "App errors are for the space's Designers." }, statusCode: StatusCodes.Status403Forbidden);

    // The instance's signing key, made on first use.
    internal static async Task<byte[]> KeyAsync(ISystemRepository system, CancellationToken ct)
    {
        var stored = await system.GetConfigAsync(SpaceApps.CodeKeyConfig, ct);
        if (!string.IsNullOrEmpty(stored)) return Convert.FromBase64String(stored);
        var key = RandomNumberGenerator.GetBytes(32);
        await system.SetConfigAsync(SpaceApps.CodeKeyConfig, Convert.ToBase64String(key), ct);
        return key;
    }

    // Fishbowl's own origin — plain HTTP on any host too (a LAN install
    // without TLS), where its space apps' code is served from.
    internal static string HostOrigin(HttpContext http)
    {
        var raw = $"{http.Request.Scheme}://{http.Request.Host}";
        return AppOrigins.TryNormalize(raw, out var origin, anyHttp: true) ? origin : raw;
    }

    internal static string CodeBase(HttpContext http, byte[] key, string spaceId, string folder) =>
        $"{HostOrigin(http)}/apps/code/{spaceId}/{folder}/{SpaceApps.Sign(key, spaceId, folder)}/";

    // The desktop's record of a space app — the install record's shape, so
    // tiles, the burger, the palette and the Apps window take it as is.
    internal static object Dto(HttpContext http, byte[] key, string spaceId, SpaceAppManifest m, DateTime? changed) => new
    {
        id = SpaceApps.IdOf(m.Folder),
        manifest = new
        {
            name = m.Name,
            tag = m.Tag,
            version = m.Version,
            description = m.Description,
            icon = m.Icon,
            width = m.Width,
            height = m.Height,
        },
        origin = HostOrigin(http),
        entryUrl = CodeBase(http, key, spaceId, m.Folder) + m.Entry,
        version = m.Version,
        mode = AppModes.Space,
        folder = $"{AppsFolder.Name}/{m.Folder}",
        granted = Array.Empty<string>(),
        updatedAt = changed,
    };

    private static async Task<IResult> CodeAsync(string spaceId, string folder, string sig, string? file,
        HttpContext http, IFileService files, ISystemRepository system)
    {
        var ct = http.RequestAborted;
        if (!SpaceApps.IsFolder(folder) || !SpaceApps.IsFile(file) || SpaceApps.TypeOf(file!) is not { } type
            || spaceId.Length > 40 || !spaceId.All(char.IsAsciiLetterOrDigit))
            return Results.NotFound();
        if (!SpaceApps.Verify(await KeyAsync(system, ct), spaceId, folder, sig)) return Results.NotFound();

        FileReadHandle h;
        try { h = await files.OpenReadAsync(ContextRef.Space(spaceId), $"{AppsFolder.Name}/{folder}/{file}", ct); }
        catch (FileStoreException) { return Results.NotFound(); }

        var headers = http.Response.Headers;
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        // Opened on its own it is inert: no script, no plugin, its own origin.
        headers[HeaderNames.ContentSecurityPolicy] = "default-src 'none'; sandbox";
        headers["Referrer-Policy"] = "no-referrer";
        // The frame is an opaque origin: fonts, modules and fetches are CORS.
        headers[HeaderNames.AccessControlAllowOrigin] = "*";
        // A Designer's change shows on the next open.
        headers[HeaderNames.CacheControl] = "no-cache";
        return Results.File(h.Stream, type, lastModified: h.Entry.Mtime,
            entityTag: new EntityTagHeaderValue($"\"{h.Entry.Etag}\""));
    }

    // The frame document of a space app (from DesktopApi's frame route,
    // the caller already resolved as a member): the kit's harness, its CSP
    // limited to the app's own code folder.
    internal static async Task<IResult> FrameAsync(HttpContext http, string spaceId, string folder, IFileService files)
    {
        var ct = http.RequestAborted;
        var app = await SpaceAppCatalog.ReadAsync(files, ContextRef.Space(spaceId), folder, ct);
        if (app is null) return Results.NotFound();
        var system = http.RequestServices.GetRequiredService<ISystemRepository>();
        var key = await KeyAsync(system, ct);
        var origin = HostOrigin(http);

        string csp;
        try { csp = FrameCsp.Build(origin, null, origin, CodeBase(http, key, spaceId, folder) + app.Value.Manifest.Entry); }
        catch (ArgumentException) { return Results.NotFound(); }
        return DesktopApi.Frame(http, csp, app.Value.Manifest.Name, app.Value.Manifest.Tag);
    }
}
