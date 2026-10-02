using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Desktop;
using Fishbowl.Core.Files;
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
public static class SpaceAppsApi
{
    public static IEndpointRouteBuilder MapSpaceAppsApi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/apps/code/{spaceId}/{folder}/{sig}/{**file}", CodeAsync)
            .WithName("GetSpaceAppCode")
            .ExcludeFromDescription();
        return routes;
    }

    // The instance's signing key, made on first use.
    internal static async Task<byte[]> KeyAsync(ISystemRepository system, CancellationToken ct)
    {
        var stored = await system.GetConfigAsync(SpaceApps.CodeKeyConfig, ct);
        if (!string.IsNullOrEmpty(stored)) return Convert.FromBase64String(stored);
        var key = RandomNumberGenerator.GetBytes(32);
        await system.SetConfigAsync(SpaceApps.CodeKeyConfig, Convert.ToBase64String(key), ct);
        return key;
    }

    internal static string HostOrigin(HttpContext http)
    {
        var raw = $"{http.Request.Scheme}://{http.Request.Host}";
        return AppOrigins.TryNormalize(raw, out var origin) ? origin : raw;
    }

    internal static string CodeBase(HttpContext http, byte[] key, string spaceId, string folder) =>
        $"{HostOrigin(http)}/apps/code/{spaceId}/{folder}/{SpaceApps.Sign(key, spaceId, folder)}/";

    /// <summary>The apps in a space's .apps — those whose app.json reads.</summary>
    internal static async Task<IReadOnlyList<(SpaceAppManifest Manifest, DateTime? Changed)>> ListAsync(
        IFileService files, ContextRef ctx, CancellationToken ct)
    {
        FileListPage page;
        try { page = await files.ListAsync(ctx, AppsFolder.Name, null, 500, ct); }
        catch (FileStoreException) { return Array.Empty<(SpaceAppManifest, DateTime?)>(); }

        var apps = new List<(SpaceAppManifest, DateTime?)>();
        foreach (var dir in page.Entries.Where(e => e.Kind == "folder" && SpaceApps.IsFolder(e.Name)))
        {
            var m = await ReadAsync(files, ctx, dir.Name, ct);
            if (m is not null) apps.Add(m.Value);
        }
        return apps.OrderBy(a => a.Item1.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static async Task<(SpaceAppManifest Manifest, DateTime? Changed)?> ReadAsync(
        IFileService files, ContextRef ctx, string folder, CancellationToken ct)
    {
        try
        {
            var h = await files.OpenReadAsync(ctx, $"{AppsFolder.Name}/{folder}/{SpaceApps.ManifestFile}", ct);
            await using var stream = h.Stream;
            if (h.Entry.Size > SpaceApps.MaxManifestBytes) return null;
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var m = SpaceApps.Parse(folder, doc.RootElement);
            return m is null ? null : (m, h.Entry.Mtime);
        }
        catch (FileStoreException) { return null; }
        catch (JsonException) { return null; }
    }

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
        var app = await ReadAsync(files, ContextRef.Space(spaceId), folder, ct);
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
